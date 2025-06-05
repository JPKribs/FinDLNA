using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FinDLNA.Models;
using FinDLNA.Services;
using System.Collections.Concurrent;

namespace FinDLNA.Services;

// MARK: Enhanced SsdpService
public class SsdpService : IDisposable
{
    private readonly ILogger<SsdpService> _logger;
    private readonly IConfiguration _configuration;
    private readonly XmlTemplateService _xmlTemplateService;
    private readonly DlnaDevice _device;
    
    private UdpClient? _udpClient;
    private Timer? _advertiseTimer;
    private Timer? _bootIdTimer;
    private Timer? _cleanupTimer;
    
    private bool _isRunning;
    private int _bootId = 1;
    private int _configId = 1;

    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string ContentDirectoryServiceType = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string ConnectionManagerServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";

    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _recentRequests = new();
    private readonly ConcurrentDictionary<string, int> _deviceStats = new();

    public SsdpService(ILogger<SsdpService> logger, IConfiguration configuration, XmlTemplateService xmlTemplateService)
    {
        _logger = logger;
        _configuration = configuration;
        _xmlTemplateService = xmlTemplateService;
        _device = new DlnaDevice
        {
            FriendlyName = _configuration["Dlna:ServerName"] ?? "FinDLNA Server",
            Port = int.Parse(_configuration["Dlna:Port"] ?? "8200"),
            Uuid = GenerateStableUuid()
        };
    }

    // MARK: StartAsync
    public async Task StartAsync()
    {
        if (_isRunning) return;

        try
        {
            await InitializeNetworking();
            StartTimers();
            
            _isRunning = true;
            
            _ = Task.Run(ListenForRequests);
            
            await SendInitialAliveNotifications();

            _logger.LogInformation("SSDP service started on port {Port} with BootID {BootId} and UUID {Uuid}", 
                SsdpPort, _bootId, _device.Uuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SSDP service");
            throw;
        }
    }

    // MARK: InitializeNetworking
    private async Task InitializeNetworking()
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            try
            {
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
                _logger.LogDebug("Successfully bound to SSDP port {Port}", SsdpPort);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogWarning("SSDP port {Port} already in use, attempting alternative binding", SsdpPort);
                _udpClient.Dispose();
                _udpClient = new UdpClient(0);
            }
            
            _udpClient.JoinMulticastGroup(IPAddress.Parse(SsdpAddress));
            _logger.LogInformation("Joined SSDP multicast group {Address}", SsdpAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SSDP networking");
            throw;
        }
        
        await Task.CompletedTask;
    }

    // MARK: StartTimers
    private void StartTimers()
    {
        _advertiseTimer = new Timer(SendPeriodicAliveNotifications, null, 
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(25));
            
        _bootIdTimer = new Timer(IncrementBootId, null, 
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            
        _cleanupTimer = new Timer(CleanupOldRequests, null, 
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // MARK: SendInitialAliveNotifications
    private async Task SendInitialAliveNotifications()
    {
        try
        {
            await SendAliveNotifications();
            await Task.Delay(1000);
            await SendAliveNotifications();
            _logger.LogInformation("Sent initial SSDP alive notifications");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send initial alive notifications");
        }
    }

    // MARK: ListenForRequests
    private async Task ListenForRequests()
    {
        _logger.LogDebug("Starting SSDP request listener");
        
        while (_isRunning && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (IsRecentDuplicateRequest(result.RemoteEndPoint))
                {
                    continue;
                }

                _logger.LogTrace("SSDP Request from {EndPoint}: {MessagePreview}", 
                    result.RemoteEndPoint, message.Substring(0, Math.Min(100, message.Length)));

                if (message.StartsWith("M-SEARCH"))
                {
                    await ProcessSearchRequest(message, result.RemoteEndPoint);
                }
                else if (message.StartsWith("NOTIFY"))
                {
                    ProcessNotifyMessage(message, result.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("UDP client disposed, stopping listener");
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                _logger.LogDebug("Socket operation interrupted");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing SSDP request");
            }
        }
        
        _logger.LogDebug("SSDP request listener stopped");
    }

    // MARK: ProcessSearchRequest
    private async Task ProcessSearchRequest(string message, IPEndPoint remoteEndPoint)
    {
        try
        {
            var searchParams = ParseSearchRequest(message);
            
            _logger.LogDebug("M-SEARCH: ST={SearchTarget}, User-Agent={UserAgent}, MX={MaxDelay} from {EndPoint}",
                searchParams.SearchTarget, searchParams.UserAgent, searchParams.MaxDelay, remoteEndPoint);

            IncrementDeviceStats(searchParams.UserAgent);

            if (ShouldRespondToSearch(searchParams.SearchTarget))
            {
                await SendSearchResponse(remoteEndPoint, searchParams);
            }
            else
            {
                _logger.LogTrace("Ignoring M-SEARCH for unsupported target: {SearchTarget}", 
                    searchParams.SearchTarget);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing M-SEARCH from {EndPoint}", remoteEndPoint);
        }
    }

    // MARK: ProcessNotifyMessage
    private void ProcessNotifyMessage(string message, IPEndPoint remoteEndPoint)
    {
        try
        {
            if (message.Contains("ssdp:byebye"))
            {
                _logger.LogTrace("Received byebye notification from {EndPoint}", remoteEndPoint);
            }
            else if (message.Contains("ssdp:alive"))
            {
                _logger.LogTrace("Received alive notification from {EndPoint}", remoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error processing NOTIFY from {EndPoint}", remoteEndPoint);
        }
    }

    // MARK: ParseSearchRequest
    private SearchRequestParams ParseSearchRequest(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new SearchRequestParams();

        foreach (var line in lines)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            var key = line.Substring(0, colonIndex).Trim().ToUpperInvariant();
            var value = line.Substring(colonIndex + 1).Trim();

            switch (key)
            {
                case "ST":
                    result.SearchTarget = value;
                    break;
                case "USER-AGENT":
                    result.UserAgent = value;
                    break;
                case "HOST":
                    result.Host = value;
                    break;
                case "MX":
                    if (int.TryParse(value, out var mx))
                        result.MaxDelay = mx;
                    break;
                case "MAN":
                    result.Man = value;
                    break;
            }
        }

        return result;
    }

    // MARK: IsRecentDuplicateRequest
    private bool IsRecentDuplicateRequest(IPEndPoint endpoint)
    {
        var now = DateTime.UtcNow;
        var isDuplicate = _recentRequests.TryGetValue(endpoint, out var lastSeen) && 
                         (now - lastSeen).TotalSeconds < 2;

        _recentRequests[endpoint] = now;
        return isDuplicate;
    }

    // MARK: IncrementDeviceStats
    private void IncrementDeviceStats(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return;

        var deviceKey = ExtractDeviceType(userAgent);
        _deviceStats.AddOrUpdate(deviceKey, 1, (key, count) => count + 1);
    }

    // MARK: ExtractDeviceType
    private string ExtractDeviceType(string userAgent)
    {
        if (userAgent.Contains("Samsung", StringComparison.OrdinalIgnoreCase) || 
            userAgent.Contains("SEC_HHP", StringComparison.OrdinalIgnoreCase))
            return "Samsung";
        if (userAgent.Contains("LG", StringComparison.OrdinalIgnoreCase))
            return "LG";
        if (userAgent.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return "Xbox";
        if (userAgent.Contains("PlayStation", StringComparison.OrdinalIgnoreCase))
            return "PlayStation";
        if (userAgent.Contains("BRAVIA", StringComparison.OrdinalIgnoreCase))
            return "Sony";
        if (userAgent.Contains("Panasonic", StringComparison.OrdinalIgnoreCase))
            return "Panasonic";
        
        return "Generic";
    }

    // MARK: ShouldRespondToSearch
    private bool ShouldRespondToSearch(string searchTarget)
    {
        var validTargets = new[]
        {
            "ssdp:all",
            "upnp:rootdevice",
            DeviceType,
            ContentDirectoryServiceType,
            ConnectionManagerServiceType,
            $"uuid:{_device.Uuid}"
        };

        return validTargets.Any(target => target.Equals(searchTarget, StringComparison.OrdinalIgnoreCase));
    }

    // MARK: SendSearchResponse
    private async Task SendSearchResponse(IPEndPoint remoteEndPoint, SearchRequestParams searchParams)
    {
        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";
            var usn = GetUsnForSearchTarget(searchParams.SearchTarget);

            var delay = CalculateResponseDelay(searchParams.UserAgent, searchParams.MaxDelay);
            if (delay > 0)
            {
                await Task.Delay(delay);
            }

            var response = BuildSearchResponse(location, searchParams.SearchTarget, usn, searchParams.UserAgent);
            var responseBytes = Encoding.UTF8.GetBytes(response);

            using var responseClient = new UdpClient();
            await responseClient.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

            _logger.LogDebug("Sent SSDP response to {RemoteEndPoint} for {SearchTarget} (USN: {Usn}, Delay: {Delay}ms)", 
                remoteEndPoint, searchParams.SearchTarget, usn, delay);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP response to {RemoteEndPoint}", remoteEndPoint);
        }
    }

    // MARK: BuildSearchResponse
    private string BuildSearchResponse(string location, string searchTarget, string usn, string userAgent)
    {
        var response = new StringBuilder();
        response.AppendLine("HTTP/1.1 200 OK");
        response.AppendLine($"CACHE-CONTROL: max-age=1800");
        response.AppendLine($"DATE: {DateTime.UtcNow:R}");
        response.AppendLine("EXT:");
        response.AppendLine($"LOCATION: {location}");
        response.AppendLine("SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0");
        response.AppendLine($"ST: {searchTarget}");
        response.AppendLine($"USN: {usn}");
        response.AppendLine($"BOOTID.UPNP.ORG: {_bootId}");
        response.AppendLine($"CONFIGID.UPNP.ORG: {_configId}");

        if (IsSamsungDevice(userAgent))
        {
            response.AppendLine($"SEARCHPORT.UPNP.ORG: {SsdpPort}");
        }

        response.AppendLine();
        return response.ToString();
    }

    // MARK: GetUsnForSearchTarget
    private string GetUsnForSearchTarget(string searchTarget)
    {
        return searchTarget.ToLowerInvariant() switch
        {
            "ssdp:all" or "upnp:rootdevice" => $"uuid:{_device.Uuid}::upnp:rootdevice",
            var st when st == DeviceType.ToLowerInvariant() => $"uuid:{_device.Uuid}::{DeviceType}",
            var st when st == ContentDirectoryServiceType.ToLowerInvariant() => $"uuid:{_device.Uuid}::{ContentDirectoryServiceType}",
            var st when st == ConnectionManagerServiceType.ToLowerInvariant() => $"uuid:{_device.Uuid}::{ConnectionManagerServiceType}",
            var st when st.StartsWith("uuid:") => $"uuid:{_device.Uuid}",
            _ => $"uuid:{_device.Uuid}::{searchTarget}"
        };
    }

    // MARK: CalculateResponseDelay
    private int CalculateResponseDelay(string userAgent, int maxDelay)
    {
        var baseDelay = maxDelay > 0 ? Random.Shared.Next(0, Math.Min(maxDelay * 1000, 3000)) : 0;
        
        if (IsSamsungDevice(userAgent))
        {
            return Math.Max(baseDelay, Random.Shared.Next(100, 800));
        }
        
        if (IsLgDevice(userAgent))
        {
            return Math.Max(baseDelay, Random.Shared.Next(200, 600));
        }

        if (IsXboxDevice(userAgent))
        {
            return Math.Max(baseDelay, Random.Shared.Next(0, 400));
        }

        return Math.Max(baseDelay, Random.Shared.Next(0, 500));
    }

    // MARK: Device Detection Methods
    private bool IsSamsungDevice(string userAgent) =>
        userAgent.Contains("SEC_HHP", StringComparison.OrdinalIgnoreCase) ||
        userAgent.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ||
        userAgent.Contains("Tizen", StringComparison.OrdinalIgnoreCase);

    private bool IsLgDevice(string userAgent) =>
        userAgent.Contains("LG", StringComparison.OrdinalIgnoreCase) ||
        userAgent.Contains("webOS", StringComparison.OrdinalIgnoreCase);

    private bool IsXboxDevice(string userAgent) =>
        userAgent.Contains("Xbox", StringComparison.OrdinalIgnoreCase);

    // MARK: SendPeriodicAliveNotifications
    private async void SendPeriodicAliveNotifications(object? state)
    {
        if (!_isRunning) return;

        try
        {
            await SendAliveNotifications();
            LogDeviceStatistics();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send periodic SSDP alive notifications");
        }
    }

    // MARK: SendAliveNotifications
    private async Task SendAliveNotifications()
    {
        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);

            var notifications = CreateAliveNotifications(location);

            using var notifyClient = new UdpClient();
            foreach (var notification in notifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(300);
            }

            _logger.LogDebug("Sent {Count} SSDP alive notifications with BootID {BootId}", 
                notifications.Count, _bootId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP alive notifications");
        }
    }

    // MARK: CreateAliveNotifications
    private List<string> CreateAliveNotifications(string location)
    {
        var notifications = new List<string>();
        var baseHeaders = BuildNotificationHeaders(location);

        var notificationTargets = new[]
        {
            ("upnp:rootdevice", $"uuid:{_device.Uuid}::upnp:rootdevice"),
            ($"uuid:{_device.Uuid}", $"uuid:{_device.Uuid}"),
            (DeviceType, $"uuid:{_device.Uuid}::{DeviceType}"),
            (ContentDirectoryServiceType, $"uuid:{_device.Uuid}::{ContentDirectoryServiceType}"),
            (ConnectionManagerServiceType, $"uuid:{_device.Uuid}::{ConnectionManagerServiceType}")
        };

        foreach (var (nt, usn) in notificationTargets)
        {
            var notification = new StringBuilder();
            notification.AppendLine("NOTIFY * HTTP/1.1");
            notification.Append(baseHeaders);
            notification.AppendLine($"NT: {nt}");
            notification.AppendLine($"USN: {usn}");
            notification.AppendLine();
            
            notifications.Add(notification.ToString());
        }

        return notifications;
    }

    // MARK: BuildNotificationHeaders
    private string BuildNotificationHeaders(string location)
    {
        var headers = new StringBuilder();
        headers.AppendLine($"HOST: {SsdpAddress}:{SsdpPort}");
        headers.AppendLine("CACHE-CONTROL: max-age=1800");
        headers.AppendLine($"LOCATION: {location}");
        headers.AppendLine("SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0");
        headers.AppendLine("NTS: ssdp:alive");
        headers.AppendLine($"BOOTID.UPNP.ORG: {_bootId}");
        headers.AppendLine($"CONFIGID.UPNP.ORG: {_configId}");
        return headers.ToString();
    }

    // MARK: IncrementBootId
    private void IncrementBootId(object? state)
    {
        _bootId++;
        _logger.LogDebug("Incremented BootID to {BootId}", _bootId);
    }

    // MARK: CleanupOldRequests
    private void CleanupOldRequests(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            var keysToRemove = _recentRequests
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _recentRequests.TryRemove(key, out _);
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogTrace("Cleaned up {Count} old SSDP request entries", keysToRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error during SSDP cleanup");
        }
    }

    // MARK: LogDeviceStatistics
    private void LogDeviceStatistics()
    {
        if (_deviceStats.IsEmpty) return;

        var totalRequests = _deviceStats.Values.Sum();
        _logger.LogInformation("SSDP Device Statistics - Total Requests: {Total}", totalRequests);

        foreach (var kvp in _deviceStats.OrderByDescending(kvp => kvp.Value))
        {
            var percentage = (double)kvp.Value / totalRequests * 100;
            _logger.LogInformation("  {Device}: {Count} ({Percentage:F1}%)", 
                kvp.Key, kvp.Value, percentage);
        }
    }

    // MARK: GetLocalIPAddress
    private string GetLocalIPAddress()
    {
        try
        {
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                           ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            foreach (var ni in networkInterfaces)
            {
                var properties = ni.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                 !IPAddress.IsLoopback(addr.Address))
                    .Select(addr => addr.Address);

                var preferredAddress = addresses.FirstOrDefault(addr => 
                    addr.ToString().StartsWith("192.168.") ||
                    addr.ToString().StartsWith("10.") ||
                    addr.ToString().StartsWith("172."));

                if (preferredAddress != null)
                {
                    return preferredAddress.ToString();
                }

                var firstAddress = addresses.FirstOrDefault();
                if (firstAddress != null)
                {
                    return firstAddress.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting local IP address via network interfaces");
        }

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting local IP address via DNS");
        }
        
        _logger.LogWarning("Could not determine local IP address, using loopback");
        return "127.0.0.1";
    }

    // MARK: GetDeviceDescriptionXml
    public string GetDeviceDescriptionXml()
    {
        var localIp = GetLocalIPAddress();
        var baseUrl = $"http://{localIp}:{_device.Port}";

        return _xmlTemplateService.GetTemplate("DeviceDescription",
            _device.FriendlyName,
            _device.Manufacturer,
            _device.ModelName,
            _device.ModelNumber,
            _device.Uuid,
            baseUrl);
    }

    // MARK: GenerateStableUuid
    private string GenerateStableUuid()
    {
        try
        {
            var machineName = Environment.MachineName;
            var serverName = _configuration["Dlna:ServerName"] ?? "FinDLNA";
            var seed = $"{machineName}-{serverName}-FinDLNA";
            
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(seed));
            
            var guid = new Guid(hash.Take(16).ToArray());
            return guid.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate stable UUID, using random UUID");
            return Guid.NewGuid().ToString();
        }
    }

    // MARK: SendManualAliveNotificationsAsync
    public async Task SendManualAliveNotificationsAsync()
    {
        _logger.LogInformation("Manual SSDP refresh requested - sending immediate notifications");
        
        try
        {
            await SendAliveNotifications();
            await Task.Delay(1000);
            await SendAliveNotifications();
            
            _logger.LogInformation("Manual SSDP refresh completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send manual SSDP alive notifications");
            throw;
        }
    }

    // MARK: StopAsync
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping SSDP service");
        _isRunning = false;

        await SendByeByeNotifications();

        _advertiseTimer?.Dispose();
        _bootIdTimer?.Dispose();
        _cleanupTimer?.Dispose();

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing UDP client");
        }

        LogFinalStatistics();
        _logger.LogInformation("SSDP service stopped");
    }

    // MARK: SendByeByeNotifications
    private async Task SendByeByeNotifications()
    {
        try
        {
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            var notifications = CreateByeByeNotifications();

            using var notifyClient = new UdpClient();
            foreach (var notification in notifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(200);
            }

            _logger.LogDebug("Sent SSDP byebye notifications");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP byebye notifications");
        }
    }

    // MARK: CreateByeByeNotifications
    private List<string> CreateByeByeNotifications()
    {
        var notifications = new List<string>();
        var baseHeaders = $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                         "NTS: ssdp:byebye\r\n" +
                         $"BOOTID.UPNP.ORG: {_bootId}\r\n";

        var notificationTargets = new[]
        {
            ("upnp:rootdevice", $"uuid:{_device.Uuid}::upnp:rootdevice"),
            ($"uuid:{_device.Uuid}", $"uuid:{_device.Uuid}"),
            (DeviceType, $"uuid:{_device.Uuid}::{DeviceType}")
        };

        foreach (var (nt, usn) in notificationTargets)
        {
            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                           baseHeaders +
                           $"NT: {nt}\r\n" +
                           $"USN: {usn}\r\n" +
                           "\r\n");
        }

        return notifications;
    }

    // MARK: LogFinalStatistics
    private void LogFinalStatistics()
    {
        if (!_deviceStats.IsEmpty)
        {
            _logger.LogInformation("Final SSDP Statistics:");
            LogDeviceStatistics();
        }
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _ = StopAsync();
        }
        
        try
        {
            _advertiseTimer?.Dispose();
            _bootIdTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _udpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during SSDP service disposal");
        }
        
        GC.SuppressFinalize(this);
    }

    // MARK: Helper Classes
    private class SearchRequestParams
    {
        public string SearchTarget { get; set; } = "";
        public string UserAgent { get; set; } = "";
        public string Host { get; set; } = "";
        public string Man { get; set; } = "";
        public int MaxDelay { get; set; } = 3;
    }
}