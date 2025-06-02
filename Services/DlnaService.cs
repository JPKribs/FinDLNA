using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FinDLNA.Services;

namespace FinDLNA.Services;

// MARK: DlnaService
public class DlnaService : IDisposable
{
    private readonly ILogger<DlnaService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SsdpService _ssdpService;
    private readonly ContentDirectoryService _contentDirectoryService;
    private readonly JellyfinService _jellyfinService;
    private readonly StreamingService _streamingService;
    private HttpListener? _httpListener;
    private bool _isRunning;

    public DlnaService(
        ILogger<DlnaService> logger, 
        IConfiguration configuration,
        SsdpService ssdpService,
        ContentDirectoryService contentDirectoryService,
        JellyfinService jellyfinService,
        StreamingService streamingService)
    {
        _logger = logger;
        _configuration = configuration;
        _ssdpService = ssdpService;
        _contentDirectoryService = contentDirectoryService;
        _jellyfinService = jellyfinService;
        _streamingService = streamingService;
    }

    // MARK: StartAsync
    public async Task StartAsync()
    {
        if (!_jellyfinService.IsConfigured)
        {
            _logger.LogWarning("Jellyfin not configured, DLNA service will not start");
            return;
        }

        if (_isRunning) return;

        try
        {
            var port = int.Parse(_configuration["Dlna:Port"] ?? "8200");
            
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://*:{port}/");
            _httpListener.Start();

            _isRunning = true;

            await _ssdpService.StartAsync();

            _ = Task.Run(ProcessHttpRequests);

            _logger.LogInformation("DLNA service started on port {Port}", port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start DLNA service");
            throw;
        }
    }

    // MARK: ProcessHttpRequests
    private async Task ProcessHttpRequests()
    {
        while (_isRunning && _httpListener != null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing HTTP request");
            }
        }
    }

    // MARK: HandleRequest
    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "";

            _logger.LogDebug("DLNA request: {Method} {Path}", request.HttpMethod, path);

            switch (path)
            {
                case "/device.xml":
                    await HandleDeviceDescription(response);
                    break;

                case "/ContentDirectory/scpd.xml":
                    await HandleServiceDescription(response);
                    break;

                case "/ContentDirectory/control":
                    await HandleControlRequest(request, response);
                    break;

                case "/ContentDirectory/event":
                    await HandleEventSubscription(request, response);
                    break;

                case "/ConnectionManager/scpd.xml":
                    await HandleConnectionManagerDescription(response);
                    break;

                case "/ConnectionManager/control":
                    await HandleConnectionManagerControl(request, response);
                    break;

                case "/ConnectionManager/event":
                    await HandleEventSubscription(request, response);
                    break;

                default:
                    if (path.StartsWith("/stream/"))
                    {
                        var itemId = path.Substring(8); // Remove "/stream/"
                        await _streamingService.HandleStreamRequest(context, itemId);
                        return; // StreamingService handles response
                    }
                    else
                    {
                        response.StatusCode = 404;
                        response.Close();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DLNA request");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    // MARK: HandleDeviceDescription
    private async Task HandleDeviceDescription(HttpListenerResponse response)
    {
        var xml = _ssdpService.GetDeviceDescriptionXml();
        var buffer = Encoding.UTF8.GetBytes(xml);

        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("Cache-Control", "max-age=1800");
        response.AddHeader("Server", "FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0");

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: HandleServiceDescription
    private async Task HandleServiceDescription(HttpListenerResponse response)
    {
        var xml = _contentDirectoryService.GetServiceDescriptionXml();
        var buffer = Encoding.UTF8.GetBytes(xml);

        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("Cache-Control", "max-age=1800");

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: HandleConnectionManagerDescription
    private async Task HandleConnectionManagerDescription(HttpListenerResponse response)
    {
        var xml = GetConnectionManagerDescriptionXml();
        var buffer = Encoding.UTF8.GetBytes(xml);

        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("Cache-Control", "max-age=1800");

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: GetConnectionManagerDescriptionXml
    private string GetConnectionManagerDescriptionXml()
    {
        return """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
            <specVersion>
                <major>1</major>
                <minor>0</minor>
            </specVersion>
            <actionList>
                <action>
                    <name>GetProtocolInfo</name>
                    <argumentList>
                        <argument>
                            <name>Source</name>
                            <direction>out</direction>
                            <relatedStateVariable>SourceProtocolInfo</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>Sink</name>
                            <direction>out</direction>
                            <relatedStateVariable>SinkProtocolInfo</relatedStateVariable>
                        </argument>
                    </argumentList>
                </action>
                <action>
                    <name>GetCurrentConnectionIDs</name>
                    <argumentList>
                        <argument>
                            <name>ConnectionIDs</name>
                            <direction>out</direction>
                            <relatedStateVariable>CurrentConnectionIDs</relatedStateVariable>
                        </argument>
                    </argumentList>
                </action>
            </actionList>
            <serviceStateTable>
                <stateVariable sendEvents="yes">
                    <name>SourceProtocolInfo</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="yes">
                    <name>SinkProtocolInfo</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="yes">
                    <name>CurrentConnectionIDs</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_ConnectionStatus</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_ConnectionManager</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_Direction</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_ProtocolInfo</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_ConnectionID</name>
                    <dataType>i4</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_AVTransportID</name>
                    <dataType>i4</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_RcsID</name>
                    <dataType>i4</dataType>
                </stateVariable>
            </serviceStateTable>
        </scpd>
        """;
    }

    // MARK: HandleControlRequest
    private async Task HandleControlRequest(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.Close();
            return;
        }

        var soapAction = request.Headers["SOAPAction"]?.Trim('"');
        if (string.IsNullOrEmpty(soapAction))
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        using var reader = new StreamReader(request.InputStream);
        var soapBody = await reader.ReadToEndAsync();

        _logger.LogDebug("SOAP Action: {Action}", soapAction);

        string responseXml;
        if (soapAction.Contains("Browse"))
        {
            responseXml = await _contentDirectoryService.ProcessBrowseRequestAsync(soapBody);
        }
        else if (soapAction.Contains("GetSearchCapabilities"))
        {
            responseXml = _contentDirectoryService.ProcessSearchCapabilitiesRequest();
        }
        else if (soapAction.Contains("GetSortCapabilities"))
        {
            responseXml = _contentDirectoryService.ProcessSortCapabilitiesRequest();
        }
        else
        {
            responseXml = CreateUnsupportedActionResponse();
        }

        var buffer = Encoding.UTF8.GetBytes(responseXml);
        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("EXT", "");
        response.AddHeader("Server", "FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0");

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: HandleConnectionManagerControl
    private async Task HandleConnectionManagerControl(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.Close();
            return;
        }

        var soapAction = request.Headers["SOAPAction"]?.Trim('"');
        if (string.IsNullOrEmpty(soapAction))
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        string responseXml;
        if (soapAction.Contains("GetProtocolInfo"))
        {
            responseXml = CreateGetProtocolInfoResponse();
        }
        else if (soapAction.Contains("GetCurrentConnectionIDs"))
        {
            responseXml = CreateGetCurrentConnectionIDsResponse();
        }
        else
        {
            responseXml = CreateUnsupportedActionResponse();
        }

        var buffer = Encoding.UTF8.GetBytes(responseXml);
        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("EXT", "");

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: CreateGetProtocolInfoResponse
    private string CreateGetProtocolInfoResponse()
    {
        var source = "http-get:*:video/mp4:*,http-get:*:video/avi:*,http-get:*:audio/mpeg:*,http-get:*:audio/mp4:*,http-get:*:image/jpeg:*";
        var sink = "";

        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:GetProtocolInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                        <Source>{source}</Source>
                        <Sink>{sink}</Sink>
                    </u:GetProtocolInfoResponse>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: CreateGetCurrentConnectionIDsResponse
    private string CreateGetCurrentConnectionIDsResponse()
    {
        return """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:GetCurrentConnectionIDsResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                        <ConnectionIDs>0</ConnectionIDs>
                    </u:GetCurrentConnectionIDsResponse>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: HandleEventSubscription
    private async Task HandleEventSubscription(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod == "SUBSCRIBE")
        {
            var sid = $"uuid:{Guid.NewGuid()}";
            response.AddHeader("SID", sid);
            response.AddHeader("TIMEOUT", "Second-1800");
            response.StatusCode = 200;
        }
        else if (request.HttpMethod == "UNSUBSCRIBE")
        {
            response.StatusCode = 200;
        }
        else
        {
            response.StatusCode = 405;
        }

        response.Close();
        await Task.CompletedTask;
    }

    // MARK: CreateUnsupportedActionResponse
    private string CreateUnsupportedActionResponse()
    {
        return """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                <s:Body>
                    <s:Fault>
                        <faultcode>s:Client</faultcode>
                        <faultstring>UPnPError</faultstring>
                        <detail>
                            <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                                <errorCode>401</errorCode>
                                <errorDescription>Invalid Action</errorDescription>
                            </UPnPError>
                        </detail>
                    </s:Fault>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: StopAsync
    public async Task StopAsync()
    {
        _isRunning = false;

        await _ssdpService.StopAsync();

        try
        {
            _httpListener?.Stop();
            _httpListener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _logger.LogInformation("DLNA service stopped");
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _ = StopAsync();
        }
        _ssdpService?.Dispose();
        _httpListener?.Close();
        GC.SuppressFinalize(this);
    }
}