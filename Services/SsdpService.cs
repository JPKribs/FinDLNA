using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FinDLNA.Models;

namespace FinDLNA.Services;

// MARK: SsdpService
public class SsdpService : IDisposable
{
    private readonly ILogger<SsdpService> _logger;
    private readonly IConfiguration _configuration;
    private readonly DlnaDevice _device;
    private UdpClient? _udpClient;
    private Timer? _advertiseTimer;
    private bool _isRunning;

    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string ServiceType = "urn:schemas-upnp-org:service:ContentDirectory:1";

    public SsdpService(ILogger<SsdpService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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
            
            _advertiseTimer = new Timer(SendAliveNotification, null, TimeSpan.Zero, TimeSpan.FromMinutes(30));
            
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
                
                if (message.StartsWith("M-SEARCH") && 
                    (message.Contains("ssdp:all") || message.Contains(DeviceType) || message.Contains("upnp:rootdevice")))
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
            
            var response = $"HTTP/1.1 200 OK\r\n" +
                          $"CACHE-CONTROL: max-age=1800\r\n" +
                          $"DATE: {DateTime.UtcNow:R}\r\n" +
                          $"EXT:\r\n" +
                          $"LOCATION: {location}\r\n" +
                          $"SERVER: FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0\r\n" +
                          $"ST: {DeviceType}\r\n" +
                          $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                          "\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);
            
            using var responseClient = new UdpClient();
            await responseClient.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);
            
            _logger.LogDebug("Sent SSDP response to {RemoteEndPoint}", remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP response");
        }
    }

    // MARK: SendAliveNotification
    private async void SendAliveNotification(object? state)
    {
        try
        {
            var localIp = GetLocalIPAddress();
            var location = $"http://{localIp}:{_device.Port}/device.xml";
            
            var notification = $"NOTIFY * HTTP/1.1\r\n" +
                              $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                              $"CACHE-CONTROL: max-age=1800\r\n" +
                              $"LOCATION: {location}\r\n" +
                              $"NT: {DeviceType}\r\n" +
                              $"NTS: ssdp:alive\r\n" +
                              $"SERVER: FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0\r\n" +
                              $"USN: uuid:{_device.Uuid}::{DeviceType}\r\n" +
                              "\r\n";

            var notificationBytes = Encoding.UTF8.GetBytes(notification);
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            
            using var notifyClient = new UdpClient();
            await notifyClient.SendAsync(notificationBytes, notificationBytes.Length, multicastEndPoint);
            
            _logger.LogDebug("Sent SSDP alive notification");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SSDP alive notification");
        }
    }

    // MARK: GetLocalIPAddress
    private string GetLocalIPAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            {
                return ip.ToString();
            }
        }
        return "127.0.0.1";
    }

    // MARK: GetDeviceDescriptionXml
    public string GetDeviceDescriptionXml()
    {
        var localIp = GetLocalIPAddress();
        var baseUrl = $"http://{localIp}:{_device.Port}";
        
        return $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
            <specVersion>
                <major>1</major>
                <minor>0</minor>
            </specVersion>
            <device>
                <deviceType>{DeviceType}</deviceType>
                <friendlyName>{_device.FriendlyName}</friendlyName>
                <manufacturer>{_device.Manufacturer}</manufacturer>
                <modelName>{_device.ModelName}</modelName>
                <modelNumber>{_device.ModelNumber}</modelNumber>
                <UDN>uuid:{_device.Uuid}</UDN>
                <presentationURL>{baseUrl}</presentationURL>
                <serviceList>
                    <service>
                        <serviceType>{ServiceType}</serviceType>
                        <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
                        <controlURL>/ContentDirectory/control</controlURL>
                        <eventSubURL>/ContentDirectory/event</eventSubURL>
                        <SCPDURL>/ContentDirectory/scpd.xml</SCPDURL>
                    </service>
                </serviceList>
            </device>
        </root>
        """;
    }

    // MARK: StopAsync
    public async Task StopAsync()
    {
        _isRunning = false;
        
        _advertiseTimer?.Dispose();
        _udpClient?.Close();
        _udpClient?.Dispose();
        
        await Task.CompletedTask;
        _logger.LogInformation("SSDP service stopped");
    }

    public void Dispose()
    {
        StopAsync().Wait();
        GC.SuppressFinalize(this);
    }
}