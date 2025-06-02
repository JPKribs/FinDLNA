using System.Net;
using System.Text;
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
            catch (HttpListenerException ex) when (ex.ErrorCode == 995)
            {
                _logger.LogDebug("HTTP listener was stopped");
                break;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("HTTP listener was disposed");
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
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "";
        var userAgent = request.UserAgent ?? "";

        _logger.LogDebug("DLNA request: {Method} {Path} from {UserAgent}", request.HttpMethod, path, userAgent);

        try
        {
            switch (path)
            {
                case "/device.xml":
                    await HandleDeviceDescription(response);
                    break;

                case "/ContentDirectory/scpd.xml":
                    await HandleServiceDescription(response);
                    break;

                case "/ContentDirectory/control":
                    await HandleContentDirectoryControl(request, response);
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
                        var itemId = path.Substring(8);
                        await _streamingService.HandleStreamRequest(context, itemId);
                        return;
                    }
                    else
                    {
                        _logger.LogDebug("Unknown path requested: {Path}", path);
                        response.StatusCode = 404;
                        response.Close();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DLNA request for {Path}", path);
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch (Exception closeEx)
            {
                _logger.LogDebug(closeEx, "Error closing response after exception");
            }
        }
    }

    // MARK: HandleDeviceDescription
    private async Task HandleDeviceDescription(HttpListenerResponse response)
    {
        var xml = _ssdpService.GetDeviceDescriptionXml();
        await WriteXmlResponse(response, xml, "max-age=1800");
    }

    // MARK: HandleServiceDescription
    private async Task HandleServiceDescription(HttpListenerResponse response)
    {
        var xml = _contentDirectoryService.GetServiceDescriptionXml();
        await WriteXmlResponse(response, xml, "max-age=1800");
    }

    // MARK: HandleContentDirectoryControl
    private async Task HandleContentDirectoryControl(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.AddHeader("Allow", "POST");
            response.Close();
            return;
        }

        var soapAction = request.Headers["SOAPAction"]?.Trim('"');
        if (string.IsNullOrEmpty(soapAction))
        {
            _logger.LogWarning("Missing SOAPAction header");
            response.StatusCode = 400;
            response.Close();
            return;
        }

        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var soapBody = await reader.ReadToEndAsync();

            _logger.LogDebug("SOAP Action: {Action}", soapAction);

            string responseXml;
            if (soapAction.Contains("Browse"))
            {
                responseXml = await _contentDirectoryService.ProcessBrowseRequestAsync(soapBody, request.UserAgent);
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
                _logger.LogWarning("Unsupported SOAP action: {Action}", soapAction);
                responseXml = CreateUnsupportedActionResponse();
            }

            await WriteSoapResponse(response, responseXml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ContentDirectory control request");
            var faultResponse = CreateSoapFault("Internal server error");
            await WriteSoapResponse(response, faultResponse);
        }
    }

    // MARK: HandleConnectionManagerControl
    private async Task HandleConnectionManagerControl(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.AddHeader("Allow", "POST");
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

        try
        {
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
                _logger.LogWarning("Unsupported ConnectionManager action: {Action}", soapAction);
                responseXml = CreateUnsupportedActionResponse();
            }

            await WriteSoapResponse(response, responseXml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ConnectionManager control request");
            var faultResponse = CreateSoapFault("Internal server error");
            await WriteSoapResponse(response, faultResponse);
        }
    }

    // MARK: HandleConnectionManagerDescription
    private async Task HandleConnectionManagerDescription(HttpListenerResponse response)
    {
        var xml = GetConnectionManagerDescriptionXml();
        await WriteXmlResponse(response, xml, "max-age=1800");
    }

    // MARK: HandleEventSubscription
    private Task HandleEventSubscription(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            if (request.HttpMethod == "SUBSCRIBE")
            {
                var callback = request.Headers["CALLBACK"];
                var timeout = request.Headers["TIMEOUT"] ?? "Second-1800";
                
                if (!string.IsNullOrEmpty(callback))
                {
                    var sid = $"uuid:{Guid.NewGuid()}";
                    response.AddHeader("SID", sid);
                    response.AddHeader("TIMEOUT", timeout);
                    response.StatusCode = 200;
                    _logger.LogDebug("Created subscription {SID} for {Callback}", sid, callback);
                }
                else
                {
                    response.StatusCode = 412;
                    _logger.LogWarning("SUBSCRIBE request missing CALLBACK header");
                }
            }
            else if (request.HttpMethod == "UNSUBSCRIBE")
            {
                var sid = request.Headers["SID"];
                response.StatusCode = 200;
                _logger.LogDebug("Unsubscribed {SID}", sid);
            }
            else
            {
                response.StatusCode = 405;
                response.AddHeader("Allow", "SUBSCRIBE, UNSUBSCRIBE");
            }

            response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling event subscription");
            response.StatusCode = 500;
            response.Close();
        }

        return Task.CompletedTask;
    }

    // MARK: WriteXmlResponse
    private async Task WriteXmlResponse(HttpListenerResponse response, string xml, string? cacheControl = null)
    {
        var buffer = Encoding.UTF8.GetBytes(xml);
        
        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("Server", "FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0");
        
        if (!string.IsNullOrEmpty(cacheControl))
        {
            response.AddHeader("Cache-Control", cacheControl);
        }

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    // MARK: WriteSoapResponse
    private async Task WriteSoapResponse(HttpListenerResponse response, string xml)
    {
        var buffer = Encoding.UTF8.GetBytes(xml);
        
        response.ContentType = "text/xml; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.AddHeader("EXT", "");
        response.AddHeader("Server", "FinDLNA/1.0 UPnP/1.0 FinDLNA/1.0");

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

    // MARK: CreateGetProtocolInfoResponse
    private string CreateGetProtocolInfoResponse()
    {
        var source = string.Join(",", new[]
        {
            "http-get:*:video/mp4:DLNA.ORG_PN=AVC_MP4_MP_SD_AAC_MULT5;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            "http-get:*:video/mp4:DLNA.ORG_PN=AVC_MP4_MP_HD_720p_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            "http-get:*:video/mp4:DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            "http-get:*:video/x-matroska:*",
            "http-get:*:video/avi:*",
            "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            "http-get:*:audio/mp4:DLNA.ORG_PN=AAC_ISO_320;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            "http-get:*:audio/flac:*",
            "http-get:*:image/jpeg:DLNA.ORG_PN=JPEG_SM;DLNA.ORG_OP=00;DLNA.ORG_FLAGS=00D00000000000000000000000000000",
            "http-get:*:image/jpeg:DLNA.ORG_PN=JPEG_MED;DLNA.ORG_OP=00;DLNA.ORG_FLAGS=00D00000000000000000000000000000",
            "http-get:*:image/jpeg:DLNA.ORG_PN=JPEG_LRG;DLNA.ORG_OP=00;DLNA.ORG_FLAGS=00D00000000000000000000000000000"
        });

        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:GetProtocolInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                        <Source>{System.Security.SecurityElement.Escape(source)}</Source>
                        <Sink></Sink>
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

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                <s:Body>
                    <s:Fault>
                        <faultcode>s:Server</faultcode>
                        <faultstring>{System.Security.SecurityElement.Escape(error)}</faultstring>
                    </s:Fault>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: StopAsync
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping DLNA service");
        _isRunning = false;

        try
        {
            await _ssdpService.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping SSDP service");
        }

        try
        {
            _httpListener?.Stop();
            _httpListener?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping HTTP listener");
        }

        _logger.LogInformation("DLNA service stopped");
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _ = StopAsync();
        }
        
        try
        {
            _ssdpService?.Dispose();
            _httpListener?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during disposal");
        }
        
        GC.SuppressFinalize(this);
    }
}