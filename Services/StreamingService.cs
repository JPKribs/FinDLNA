using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FinDLNA.Models;
using FinDLNA.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: Simplified StreamingService
public class StreamingService
{
    private readonly ILogger<StreamingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;
    private readonly PlaybackReportingService _playbackReportingService;

    public StreamingService(
        ILogger<StreamingService> logger,
        IConfiguration configuration,
        JellyfinService jellyfinService,
        DeviceProfileService deviceProfileService,
        PlaybackReportingService playbackReportingService)
    {
        _logger = logger;
        _configuration = configuration;
        _jellyfinService = jellyfinService;
        _deviceProfileService = deviceProfileService;
        _playbackReportingService = playbackReportingService;
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

            _logger.LogInformation("STREAM REQUEST: Item {ItemId} from {UserAgent}", itemId, userAgent);

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent);
            var streamUrl = GetStreamUrl(guid, deviceProfile, context.Request.Headers["Range"]);

            var sessionId = await _playbackReportingService.StartPlaybackAsync(guid, userAgent, clientEndpoint);

            await ProxyStreamAsync(context, streamUrl, sessionId, guid);
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

        var queryString = string.Join("&", queryParams);
        return $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
    }

    // MARK: ProxyStreamAsync
    private async Task ProxyStreamAsync(HttpListenerContext context, string streamUrl, string? sessionId, Guid itemId)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            
            if (context.Request.Headers["Range"] != null)
            {
                request.Headers.Add("Range", context.Request.Headers["Range"]);
            }

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
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

            using var sourceStream = await response.Content.ReadAsStreamAsync();
            await sourceStream.CopyToAsync(context.Response.OutputStream);

            if (sessionId != null)
            {
                await _playbackReportingService.StopPlaybackAsync(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream interrupted for item {ItemId}", itemId);
            if (sessionId != null)
            {
                await _playbackReportingService.StopPlaybackAsync(sessionId);
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
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
        catch { }
    }
}