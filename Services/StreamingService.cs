using System.Net;
using Microsoft.Extensions.Logging;
using FinDLNA.Services;

namespace FinDLNA.Services;

// MARK: StreamingService
public class StreamingService
{
    private readonly ILogger<StreamingService> _logger;
    private readonly JellyfinService _jellyfinService;

    public StreamingService(ILogger<StreamingService> logger, JellyfinService jellyfinService)
    {
        _logger = logger;
        _jellyfinService = jellyfinService;
    }

    // MARK: HandleStreamRequest
    public async Task HandleStreamRequest(HttpListenerContext context, string itemId)
    {
        try
        {
            if (!Guid.TryParse(itemId, out var guid))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var streamUrl = _jellyfinService.GetStreamUrlAsync(guid);
            if (string.IsNullOrEmpty(streamUrl))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            _logger.LogInformation("Streaming item {ItemId} from {StreamUrl}", itemId, streamUrl);

            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead);
            
            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            
            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            context.Response.StatusCode = 200;

            using var sourceStream = await response.Content.ReadAsStreamAsync();
            await sourceStream.CopyToAsync(context.Response.OutputStream);
            
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming item {ItemId}", itemId);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }
}