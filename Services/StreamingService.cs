using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Services;
using FinDLNA.Models;

namespace FinDLNA.Services;

// MARK: StreamingService
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
                _logger.LogWarning("Invalid item ID format: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.BadRequest, "Invalid item ID");
                return;
            }

            var userAgent = context.Request.UserAgent ?? "";
            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent);
            
            _logger.LogInformation("Streaming request for item {ItemId} from {UserAgent}", itemId, userAgent);

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                _logger.LogWarning("Item not found: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var streamRequest = ParseStreamRequest(context, deviceProfile);
            var streamInfo = await GetOptimalStreamAsync(item, streamRequest, deviceProfile);
            
            if (streamInfo == null)
            {
                _logger.LogError("Failed to determine stream info for item {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream unavailable");
                return;
            }

            await StreamMediaAsync(context, streamInfo, streamRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);
            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
    }

    // MARK: ParseStreamRequest
    private StreamRequest ParseStreamRequest(HttpListenerContext context, DeviceProfile? deviceProfile)
    {
        var request = context.Request;
        var query = request.Url?.Query ?? "";
        
        var streamRequest = new StreamRequest
        {
            UserAgent = request.UserAgent ?? "",
            AcceptRanges = request.Headers["Range"],
            DeviceProfile = deviceProfile
        };

        if (!string.IsNullOrEmpty(query))
        {
            var queryParams = System.Web.HttpUtility.ParseQueryString(query);
            streamRequest.Container = queryParams["container"];
            streamRequest.VideoCodec = queryParams["videoCodec"];
            streamRequest.AudioCodec = queryParams["audioCodec"];
            
            if (int.TryParse(queryParams["maxBitrate"], out var maxBitrate))
                streamRequest.MaxBitrate = maxBitrate;
        }

        return streamRequest;
    }

    // MARK: GetOptimalStreamAsync
    private async Task<StreamInfo?> GetOptimalStreamAsync(Jellyfin.Sdk.Generated.Models.BaseItemDto item, StreamRequest streamRequest, DeviceProfile? deviceProfile)
    {
        if (!item.Id.HasValue) return null;

        try
        {
            var mediaSource = item.MediaSources?.FirstOrDefault();
            if (mediaSource == null)
            {
                _logger.LogWarning("No media sources found for item {ItemId}", item.Id);
                return null;
            }

            var videoStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Video);
            var audioStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Audio);

            var mediaType = GetMediaType(item);
            var shouldDirectPlay = await ShouldDirectPlayAsync(mediaSource, streamRequest, deviceProfile, mediaType);

            if (shouldDirectPlay)
            {
                _logger.LogInformation("Using direct play for item {ItemId}", item.Id);
                return new StreamInfo
                {
                    Url = _jellyfinService.GetStreamUrlAsync(item.Id.Value) ?? "",
                    MimeType = GetMimeTypeFromContainer(mediaSource.Container),
                    Size = mediaSource.Size ?? 0,
                    IsDirectPlay = true,
                    Container = mediaSource.Container,
                    VideoCodec = videoStream?.Codec,
                    AudioCodec = audioStream?.Codec
                };
            }
            else
            {
                _logger.LogInformation("Using transcoding for item {ItemId}", item.Id);
                return await GetTranscodingStreamAsync(item.Id.Value, streamRequest, deviceProfile, mediaType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get optimal stream for item {ItemId}", item.Id);
            return null;
        }
    }

    // MARK: ShouldDirectPlayAsync
    private Task<bool> ShouldDirectPlayAsync(Jellyfin.Sdk.Generated.Models.MediaSourceInfo mediaSource, StreamRequest streamRequest, DeviceProfile? deviceProfile, string mediaType)
    {
        if (deviceProfile == null || string.IsNullOrEmpty(mediaSource.Container))
        {
            return Task.FromResult(false);
        }

        var videoStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Video);
        var audioStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Audio);

        return _deviceProfileService.ShouldDirectPlayAsync(
            deviceProfile, 
            mediaSource.Container, 
            videoStream?.Codec, 
            audioStream?.Codec, 
            mediaType);
    }

    // MARK: GetTranscodingStreamAsync
    private Task<StreamInfo?> GetTranscodingStreamAsync(Guid itemId, StreamRequest streamRequest, DeviceProfile? deviceProfile, string mediaType)
    {
        var transcodingProfile = deviceProfile?.TranscodingProfiles?.FirstOrDefault(p => 
            p.Type.Equals(mediaType, StringComparison.OrdinalIgnoreCase));

        if (transcodingProfile == null)
        {
            transcodingProfile = new TranscodingProfile
            {
                Container = "mp4",
                VideoCodec = "h264",
                AudioCodec = "aac",
                Type = mediaType
            };
        }

        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        var userId = _configuration["Jellyfin:UserId"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId))
        {
            _logger.LogError("Missing Jellyfin configuration for transcoding");
            return Task.FromResult<StreamInfo?>(null);
        }

        var transcodingParams = new List<string>
        {
            $"DeviceId={_configuration["JellyfinClient:DeviceId"]}",
            $"UserId={userId}",
            $"Container={transcodingProfile.Container}",
            $"VideoCodec={transcodingProfile.VideoCodec}",
            $"AudioCodec={transcodingProfile.AudioCodec}",
            "TranscodingMaxAudioChannels=2",
            "BreakOnNonKeyFrames=true"
        };

        if (streamRequest.MaxBitrate.HasValue)
        {
            transcodingParams.Add($"VideoBitRate={streamRequest.MaxBitrate}");
            transcodingParams.Add($"AudioBitRate=128000");
        }
        else if (deviceProfile?.MaxStreamingBitrate > 0)
        {
            transcodingParams.Add($"VideoBitRate={deviceProfile.MaxStreamingBitrate}");
            transcodingParams.Add($"AudioBitRate=128000");
        }

        var transcodingUrl = $"{serverUrl}/Videos/{itemId}/stream.{transcodingProfile.Container}?" +
                           string.Join("&", transcodingParams) +
                           $"&X-Emby-Token={accessToken}";

        var streamInfo = new StreamInfo
        {
            Url = transcodingUrl,
            MimeType = GetMimeTypeFromContainer(transcodingProfile.Container),
            Size = 0,
            IsDirectPlay = false,
            Container = transcodingProfile.Container,
            VideoCodec = transcodingProfile.VideoCodec,
            AudioCodec = transcodingProfile.AudioCodec
        };

        return Task.FromResult<StreamInfo?>(streamInfo);
    }

    // MARK: StreamMediaAsync
    private async Task StreamMediaAsync(HttpListenerContext context, StreamInfo streamInfo, StreamRequest streamRequest)
    {
        HttpClient? httpClient = null;
        Stream? sourceStream = null;
        
        try
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, streamInfo.Url);
            
            if (!string.IsNullOrEmpty(streamRequest.AcceptRanges))
            {
                requestMessage.Headers.Add("Range", streamRequest.AcceptRanges);
                _logger.LogDebug("Adding Range header: {Range}", streamRequest.AcceptRanges);
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jellyfin stream request failed with status {StatusCode}", response.StatusCode);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            // Set basic content type
            context.Response.ContentType = streamInfo.MimeType;
            
            // Forward Content-Length if available
            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
                _logger.LogDebug("Forwarding Content-Length: {Length}", response.Content.Headers.ContentLength.Value);
            }

            // Forward all relevant headers from Jellyfin response
            foreach (var header in response.Headers)
            {
                switch (header.Key.ToLowerInvariant())
                {
                    case "accept-ranges":
                        context.Response.AddHeader("Accept-Ranges", string.Join(", ", header.Value));
                        break;
                    case "cache-control":
                        context.Response.AddHeader("Cache-Control", string.Join(", ", header.Value));
                        break;
                    case "last-modified":
                        context.Response.AddHeader("Last-Modified", string.Join(", ", header.Value));
                        break;
                    case "etag":
                        context.Response.AddHeader("ETag", string.Join(", ", header.Value));
                        break;
                }
            }

            // Forward Content headers
            foreach (var header in response.Content.Headers)
            {
                switch (header.Key.ToLowerInvariant())
                {
                    case "content-range":
                        context.Response.AddHeader("Content-Range", string.Join(", ", header.Value));
                        break;
                    case "content-disposition":
                        context.Response.AddHeader("Content-Disposition", string.Join(", ", header.Value));
                        break;
                    case "content-encoding":
                        context.Response.AddHeader("Content-Encoding", string.Join(", ", header.Value));
                        break;
                }
            }

            // Set status code
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                context.Response.StatusCode = 206;
                _logger.LogDebug("Returning partial content (206)");
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            // Set DLNA-specific headers
            context.Response.AddHeader("Accept-Ranges", "bytes");
            context.Response.AddHeader("Connection", "keep-alive");
            
            // Add transferMode.dlna.org header for better DLNA compatibility
            context.Response.AddHeader("transferMode.dlna.org", "Streaming");
            
            // Add DLNA content features if it's a direct play
            if (streamInfo.IsDirectPlay)
            {
                var dlnaFeatures = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                context.Response.AddHeader("contentFeatures.dlna.org", dlnaFeatures);
            }
            else
            {
                context.Response.AddHeader("X-Transcoding", "true");
            }

            sourceStream = await response.Content.ReadAsStreamAsync();
            
            await CopyStreamWithLoggingAsync(sourceStream, context.Response.OutputStream, streamInfo, context.Request.RemoteEndPoint);
            
            _logger.LogInformation("Stream completed successfully for {Endpoint}", context.Request.RemoteEndPoint);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error during streaming");
            await SendErrorResponse(context, HttpStatusCode.BadGateway, "Upstream error");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stream cancelled by client {Endpoint}", context.Request.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during streaming");
            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
        finally
        {
            try
            {
                sourceStream?.Dispose();
                httpClient?.Dispose();
                context.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing stream resources");
            }
        }
    }

    // MARK: CopyStreamWithLoggingAsync
    private async Task CopyStreamWithLoggingAsync(Stream source, Stream destination, StreamInfo streamInfo, System.Net.IPEndPoint? clientEndpoint)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var lastLogTime = startTime;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, 0, bufferSize);
                if (bytesRead == 0) break;

                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
                
                totalBytes += bytesRead;

                var now = DateTime.UtcNow;
                if ((now - lastLogTime).TotalSeconds >= 10)
                {
                    var elapsed = now - startTime;
                    var avgSpeed = totalBytes / elapsed.TotalSeconds / 1024 / 1024;
                    
                    _logger.LogDebug("Streaming progress: {TotalMB:F1} MB in {Elapsed:mm\\:ss}, avg speed: {Speed:F1} MB/s to {Client}",
                        totalBytes / 1024.0 / 1024.0, elapsed, avgSpeed, clientEndpoint);
                    
                    lastLogTime = now;
                }
            }

            var finalElapsed = DateTime.UtcNow - startTime;
            var finalAvgSpeed = totalBytes / finalElapsed.TotalSeconds / 1024 / 1024;
            
            _logger.LogInformation("Stream completed: {TotalMB:F1} MB in {Elapsed:mm\\:ss}, avg speed: {Speed:F1} MB/s, transcoded: {Transcoded}",
                totalBytes / 1024.0 / 1024.0, finalElapsed, finalAvgSpeed, !streamInfo.IsDirectPlay);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            _logger.LogInformation("Stream interrupted after {TotalMB:F1} MB (client disconnected)", totalBytes / 1024.0 / 1024.0);
            throw;
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
            context.Response.ContentLength64 = buffer.Length;
            
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error sending error response");
        }
    }

    // MARK: GetMediaType
    private string GetMediaType(Jellyfin.Sdk.Generated.Models.BaseItemDto item)
    {
        return item.Type switch
        {
            Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie or 
            Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode or 
            Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Video => "Video",
            Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Audio => "Audio",
            Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Photo => "Photo",
            _ => "Video"
        };
    }

    // MARK: GetMimeTypeFromContainer
    private string GetMimeTypeFromContainer(string? container)
    {
        return container?.ToLowerInvariant() switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "wmv" => "video/x-ms-wmv",
            "webm" => "video/webm",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "m4a" => "audio/mp4",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }
}

// MARK: StreamRequest
public class StreamRequest
{
    public string UserAgent { get; set; } = string.Empty;
    public string? AcceptRanges { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? MaxBitrate { get; set; }
    public DeviceProfile? DeviceProfile { get; set; }
}

// MARK: StreamInfo
public class StreamInfo
{
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDirectPlay { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
}