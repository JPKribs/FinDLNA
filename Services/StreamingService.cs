using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

public class StreamingService
{
    private readonly ILogger<StreamingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;

    public StreamingService(
        ILogger<StreamingService> logger,
        IConfiguration configuration,
        JellyfinService jellyfinService,
        DeviceProfileService deviceProfileService)
    {
        _logger = logger;
        _configuration = configuration;
        _jellyfinService = jellyfinService;
        _deviceProfileService = deviceProfileService;
    }

    // MARK: HandleStreamRequest
    public async Task HandleStreamRequest(HttpListenerContext context, string itemId)
    {
        try
        {
            if (!Guid.TryParse(itemId, out var guid))
            {
                await SendErrorResponse(context, HttpStatusCode.BadRequest, "Invalid item ID");
                return;
            }

            var userAgent = context.Request.UserAgent ?? "";
            var clientEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "";

            _logger.LogInformation("STREAM REQUEST: Item {ItemId} from {UserAgent} at {ClientEndpoint}", 
                itemId, userAgent, clientEndpoint);

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent);
            var streamUrl = GetStreamUrl(guid, deviceProfile, context.Request.Headers["Range"]);

            _logger.LogDebug("Streaming {ItemName} ({ItemType}) to {UserAgent}", 
                item.Name, item.Type, userAgent);

            await ProxyStreamAsync(context, streamUrl, guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);
            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
    }

    // MARK: GetStreamUrl
    private string GetStreamUrl(Guid itemId, DeviceProfile? deviceProfile, string? rangeHeader)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];

        var queryParams = new List<string>
        {
            $"api_key={accessToken}",
            "Static=true"
        };

        if (deviceProfile?.MaxStreamingBitrate.HasValue == true)
        {
            queryParams.Add($"MaxStreamingBitrate={deviceProfile.MaxStreamingBitrate.Value}");
        }

        if (deviceProfile?.Name != null)
        {
            if (deviceProfile.Name.Contains("Samsung"))
            {
                queryParams.Add("EnableAutoStreamCopy=true");
            }
            else if (deviceProfile.Name.Contains("Xbox"))
            {
                queryParams.Add("VideoCodec=h264");
                queryParams.Add("AudioCodec=aac");
            }
        }

        var queryString = string.Join("&", queryParams);
        var url = $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
        
        _logger.LogDebug("Generated stream URL for {ItemId} with profile {ProfileName}", 
            itemId, deviceProfile?.Name ?? "Generic");
        
        return url;
    }

    // MARK: ProxyStreamAsync
    private async Task ProxyStreamAsync(HttpListenerContext context, string streamUrl, Guid itemId)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            
            if (context.Request.Headers["Range"] != null)
            {
                request.Headers.Add("Range", context.Request.Headers["Range"]);
                _logger.LogDebug("Range request: {Range}", context.Request.Headers["Range"]);
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jellyfin returned {StatusCode} for stream {ItemId}", 
                    response.StatusCode, itemId);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/mp4";
            context.Response.StatusCode = (int)response.StatusCode;

            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            foreach (var header in response.Content.Headers)
            {
                if (header.Key.Equals("content-range", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.AddHeader("Content-Range", string.Join(", ", header.Value));
                }
            }

            context.Response.AddHeader("Accept-Ranges", "bytes");

            using var sourceStream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[65536];
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                totalBytes += bytesRead;
            }

            _logger.LogInformation("Stream completed for {ItemId}: {TotalBytes} bytes transferred", 
                itemId, totalBytes);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error streaming {ItemId}", itemId);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Stream cancelled for {ItemId} (client disconnected)", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream interrupted for {ItemId}", itemId);
        }
        finally
        {
            try 
            { 
                context.Response.Close(); 
            } 
            catch 
            { 
            }
        }
    }

    // MARK: SendErrorResponse
    private async Task SendErrorResponse(HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        try
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "text/plain";
            var buffer = System.Text.Encoding.UTF8.GetBytes(message);
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error sending error response");
        }
    }
}