using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FinDLNA.Models;
using FinDLNA.Services;

namespace FinDLNA.Services;

// MARK: SsdpService
public class SsdpService : IDisposable
{
    private readonly ILogger<SsdpService> _logger;
    private readonly IConfiguration _configuration;
    private readonly XmlTemplateService _xmlTemplateService;
    private readonly DlnaDevice _device;
    private UdpClient? _udpClient;
    private Timer? _advertiseTimer;
    private bool _isRunning;

    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string ContentDirectoryServiceType = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string ConnectionManagerServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";

    public SsdpService(ILogger<SsdpService> logger, IConfiguration configuration, XmlTemplateService xmlTemplateService)
    {
        _logger = logger;
        _configuration = configuration;
        _xmlTemplateService = xmlTemplateService;
        _device = new DlnaDevice
        {
            FriendlyName = _configuration["Dlna:ServerName"] ?? "FinDLNA Server",
            Port = int.Parse(_configuration["Dlna:Port"] ?? "8200")
        };
    }

    // MARK: StartAsync
    public async Task StartAsync()
    {
        if (_isRunning) return;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            _udpClient.JoinMulticastGroup(IPAddress.Parse(SsdpAddress));

            _isRunning = true;

            _ = Task.Run(ListenForRequests);

            // MARK: Start with immediate announcement, then every 30 minutes
            _advertiseTimer = new Timer(SendAliveNotifications, null, TimeSpan.Zero, TimeSpan.FromMinutes(30));

            _logger.LogInformation("SSDP service started on port {Port}", SsdpPort);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SSDP service");
            throw;
        }
    }

    // MARK: ListenForRequests
    private async Task ListenForRequests()
    {
        while (_isRunning && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                var message = Encoding.UTF8.GetString(result.Buffer);

                _logger.LogTrace("SSDP Request from {EndPoint}: {Message}", result.RemoteEndPoint, message);

                if (message.StartsWith("M-SEARCH") &&
                    (message.Contains("ssdp:all") ||
                     message.Contains(DeviceType) ||
                     message.Contains("upnp:rootdevice")))
                {
                    await SendSearchResponse(result.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing SSDP request");
            }
        }
    }

    // MARK: SendSearchResponse
    private async Task SendSearchResponse(IPEndPoint remoteEndPoint)
    {
        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";

            // MARK: Send single MediaServer response - Samsung TVs prefer this approach
            var response = $"HTTP/1.1 200 OK\r\n" +
                          $"CACHE-CONTROL: max-age=1800\r\n" +
                          $"DATE: {DateTime.UtcNow:R}\r\n" +
                          $"EXT:\r\n" +
                          $"LOCATION: {location}\r\n" +
                          $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                          $"ST: {DeviceType}\r\n" +
                          $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                          $"BOOTID.UPNP.ORG: 1\r\n" +
                          $"CONFIGID.UPNP.ORG: 1\r\n" +
                          "\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);

            using var responseClient = new UdpClient();
            await responseClient.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

            _logger.LogDebug("Sent SSDP response to {RemoteEndPoint} for MediaServer", remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP response");
        }
    }

    // MARK: SendAliveNotifications
    private async void SendAliveNotifications(object? state)
    {
        if (!_isRunning) return;

        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);

            // MARK: Send essential notifications only - too many can confuse Samsung TVs
            var notifications = new List<string>();

            // MARK: Root device notification
            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                             $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                             $"CACHE-CONTROL: max-age=1800\r\n" +
                             $"LOCATION: {location}\r\n" +
                             $"NT: upnp:rootdevice\r\n" +
                             $"NTS: ssdp:alive\r\n" +
                             $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                             $"USN: uuid:{_device.Uuid}::upnp:rootdevice\r\n" +
                             $"BOOTID.UPNP.ORG: 1\r\n" +
                             $"CONFIGID.UPNP.ORG: 1\r\n" +
                             "\r\n");

            // MARK: MediaServer device notification
            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                             $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                             $"CACHE-CONTROL: max-age=1800\r\n" +
                             $"LOCATION: {location}\r\n" +
                             $"NT: {DeviceType}\r\n" +
                             $"NTS: ssdp:alive\r\n" +
                             $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                             $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                             $"BOOTID.UPNP.ORG: 1\r\n" +
                             $"CONFIGID.UPNP.ORG: 1\r\n" +
                             "\r\n");

            // MARK: UUID notification
            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                             $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                             $"CACHE-CONTROL: max-age=1800\r\n" +
                             $"LOCATION: {location}\r\n" +
                             $"NT: uuid:{_device.Uuid}\r\n" +
                             $"NTS: ssdp:alive\r\n" +
                             $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                             $"USN: uuid:{_device.Uuid}\r\n" +
                             $"BOOTID.UPNP.ORG: 1\r\n" +
                             $"CONFIGID.UPNP.ORG: 1\r\n" +
                             "\r\n");

            using var notifyClient = new UdpClient();
            foreach (var notification in notifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(200); // MARK: Longer delay between notifications
            }

            _logger.LogDebug("Sent {Count} SSDP alive notifications", notifications.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP alive notifications");
        }
    }

    // MARK: GetLocalIPAddress
    private string GetLocalIPAddress()
    {
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
            _logger.LogDebug(ex, "Error getting local IP address");
        }
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

    // MARK: StopAsync
    public async Task StopAsync()
    {
        _isRunning = false;

        // MARK: Send byebye notifications
        await SendByeByeNotifications();

        _advertiseTimer?.Dispose();
        _udpClient?.Close();
        _udpClient?.Dispose();

        await Task.CompletedTask;
        _logger.LogInformation("SSDP service stopped");
    }

    // MARK: SendByeByeNotifications
    private async Task SendByeByeNotifications()
    {
        try
        {
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);

            var notifications = new List<string>
            {
                $"NOTIFY * HTTP/1.1\r\n" +
                $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                $"NT: upnp:rootdevice\r\n" +
                $"NTS: ssdp:byebye\r\n" +
                $"USN: uuid:{_device.Uuid}::upnp:rootdevice\r\n" +
                $"BOOTID.UPNP.ORG: 1\r\n" +
                "\r\n",

                $"NOTIFY * HTTP/1.1\r\n" +
                $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                $"NT: {DeviceType}\r\n" +
                $"NTS: ssdp:byebye\r\n" +
                $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                $"BOOTID.UPNP.ORG: 1\r\n" +
                "\r\n"
            };

            using var notifyClient = new UdpClient();
            foreach (var notification in notifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(100);
            }

            _logger.LogDebug("Sent SSDP byebye notifications");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP byebye notifications");
        }
    }

    // MARK: SendManualAliveNotificationsAsync
    public async Task SendManualAliveNotificationsAsync()
    {
        _logger.LogInformation("Forcing SSDP refresh for Tizen compatibility");
        
        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            
            var notifications = new List<string>();
            
            // MARK: Send immediate alive notifications
            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                            $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                            $"CACHE-CONTROL: max-age=1800\r\n" +
                            $"LOCATION: {location}\r\n" +
                            $"NT: upnp:rootdevice\r\n" +
                            $"NTS: ssdp:alive\r\n" +
                            $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                            $"USN: uuid:{_device.Uuid}::upnp:rootdevice\r\n" +
                            $"BOOTID.UPNP.ORG: 1\r\n" +
                            $"CONFIGID.UPNP.ORG: 1\r\n" +
                            "\r\n");

            notifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                            $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                            $"CACHE-CONTROL: max-age=1800\r\n" +
                            $"LOCATION: {location}\r\n" +
                            $"NT: {DeviceType}\r\n" +
                            $"NTS: ssdp:alive\r\n" +
                            $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                            $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                            $"BOOTID.UPNP.ORG: 1\r\n" +
                            $"CONFIGID.UPNP.ORG: 1\r\n" +
                            "\r\n");

            using var notifyClient = new UdpClient();
            foreach (var notification in notifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(500); // Longer delay for manual refresh
            }
            
            _logger.LogInformation("Sent immediate SSDP alive notifications for Tizen");
            
            // MARK: Send Samsung-specific notifications
            var samsungNotifications = new List<string>();
            
            samsungNotifications.Add($"NOTIFY * HTTP/1.1\r\n" +
                                    $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                                    $"CACHE-CONTROL: max-age=1800\r\n" +
                                    $"LOCATION: {location}\r\n" +
                                    $"NT: urn:samsung.com:device:RemoteControlReceiver:1\r\n" +
                                    $"NTS: ssdp:alive\r\n" +
                                    $"SERVER: Linux/3.14 UPnP/1.0 FinDLNA/1.0\r\n" +
                                    $"USN: uuid:{_device.Uuid}::urn:samsung.com:device:RemoteControlReceiver:1\r\n" +
                                    "\r\n");

            foreach (var notification in samsungNotifications)
            {
                var notificationBytes = Encoding.UTF8.GetBytes(notification);
                await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
                await Task.Delay(200);
            }
            
            _logger.LogInformation("Sent Samsung-specific SSDP notifications");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send manual SSDP alive notifications");
            throw;
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
        GC.SuppressFinalize(this);
    }
}