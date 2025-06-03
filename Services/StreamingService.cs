using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Services;
using FinDLNA.Models;
using FinDLNA.Utilities;
using System.Text.Json;

namespace FinDLNA.Services;

// MARK: StreamingService
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
        string? sessionId = null;
        Guid? itemGuid = null;

        _logger.LogInformation("STREAM REQUEST: Handling stream request for item {ItemId} from {UserAgent} at {RemoteEndPoint}",
            itemId, context.Request.UserAgent, context.Request.RemoteEndPoint);

        try
        {
            if (!Guid.TryParse(itemId, out var guid))
            {
                _logger.LogWarning("Invalid item ID format: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.BadRequest, "Invalid item ID");
                return;
            }

            itemGuid = guid;
            var userAgent = context.Request.UserAgent ?? "";
            var clientEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "";
            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent);

            // Get user's playback position for resume functionality
            var resumePositionTicks = await GetResumePositionAsync(guid);
            var shouldResume = resumePositionTicks > 0;

            // Check if this is a resume/seek request from an existing session
            var existingSession = await _playbackReportingService.GetSessionByItemAsync(guid);
            if (existingSession != null && !string.IsNullOrEmpty(context.Request.Headers["Range"]))
            {
                // This is likely a seek/resume request
                _logger.LogInformation("RESUME/SEEK: Existing session {SessionId} for item {ItemId}",
                    existingSession.SessionId, itemId);
                sessionId = existingSession.SessionId;

                // If session was paused, resume it
                if (existingSession.IsPaused)
                {
                    await _playbackReportingService.ResumePlaybackAsync(sessionId, existingSession.LastPositionTicks);
                }
            }
            else
            {
                _logger.LogInformation("Streaming request for item {ItemId} from {UserAgent} at {ClientEndpoint}", itemId, userAgent, clientEndpoint);

                var itemForSession = await _jellyfinService.GetItemAsync(guid);
                if (itemForSession == null)
                {
                    _logger.LogWarning("Item not found: {ItemId}", itemId);
                    await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                    return;
                }

                _logger.LogInformation("PLAYBACK START: Starting playback session for item {ItemId} - {ItemName}",
                    guid, itemForSession.Name ?? "Unknown");

                if (shouldResume)
                {
                    _logger.LogInformation("RESUME POSITION: Item {ItemId} has resume position at {Position}ms",
                        itemId, TimeConversionUtil.TicksToMilliseconds(resumePositionTicks));
                }

                sessionId = await _playbackReportingService.StartPlaybackAsync(guid, userAgent, clientEndpoint);
                if (sessionId != null)
                {
                    _logger.LogInformation("Started playback session {SessionId} for item {ItemId}", sessionId, itemId);
                }
                else
                {
                    _logger.LogWarning("Failed to start playback session for item {ItemId}", itemId);
                }
            }

            // Get item data for streaming (this works for both new and existing sessions)
            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                _logger.LogError("Item not found for streaming: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var streamRequest = ParseStreamRequest(context, deviceProfile);

            // Add resume position to stream request if needed
            if (shouldResume && existingSession == null) // Only auto-resume for new sessions
            {
                streamRequest.StartTimeTicks = resumePositionTicks;
            }

            var streamInfo = await GetOptimalStreamAsync(item, streamRequest, deviceProfile);

            if (streamInfo == null)
            {
                _logger.LogError("Failed to determine stream info for item {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream unavailable");
                return;
            }

            _logger.LogInformation("STREAMING: Starting media stream for item {ItemId}, session {SessionId}, direct play: {DirectPlay}, resume: {Resume}ms",
                itemId, sessionId, streamInfo.IsDirectPlay, shouldResume && existingSession == null ? TimeConversionUtil.TicksToMilliseconds(resumePositionTicks) : 0);

            await StreamMediaAsync(context, streamInfo, streamRequest, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);

            if (sessionId != null)
            {
                _logger.LogInformation("PLAYBACK ERROR: Stopping session {SessionId} due to error", sessionId);
                await _playbackReportingService.StopPlaybackAsync(sessionId, markAsWatched: false);
            }

            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
    }

    // MARK: GetResumePositionAsync
    private async Task<long> GetResumePositionAsync(Guid itemId)
    {
        try
        {
            var userId = _configuration["Jellyfin:UserId"];
            if (string.IsNullOrEmpty(userId))
            {
                return 0;
            }

            // Get user data for the item to check playback position
            var userDataUrl = $"{_configuration["Jellyfin:ServerUrl"]?.TrimEnd('/')}/Users/{userId}/Items/{itemId}/UserData";
            var accessToken = _configuration["Jellyfin:AccessToken"];

            using var httpClient = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, userDataUrl);
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var jsonContent = await response.Content.ReadAsStringAsync();
                var userData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonContent);

                if (userData.TryGetProperty("PlaybackPositionTicks", out var positionElement) &&
                    positionElement.TryGetInt64(out var positionTicks))
                {
                    // Only resume if not near the beginning (> 2 minutes) and not near the end (< 95%)
                    var twoMinutesInTicks = TimeConversionUtil.MinutesToTicks(2);
                    if (positionTicks > twoMinutesInTicks)
                    {
                        // Check if we're not near the end
                        if (userData.TryGetProperty("Played", out var playedElement) &&
                            playedElement.GetBoolean() == false)
                        {
                            _logger.LogInformation("Found resume position for item {ItemId}: {Position}ms",
                                itemId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
                            return positionTicks;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get resume position for item {ItemId}", itemId);
        }

        return 0;
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
                
                // Build stream URL with start time if needed
                var streamUrl = GetStreamUrl(item.Id.Value, mediaSource.Container, streamRequest.StartTimeTicks);
                
                return new StreamInfo
                {
                    Url = streamUrl,
                    MimeType = GetMimeTypeFromContainer(mediaSource.Container),
                    Size = mediaSource.Size ?? 0,
                    IsDirectPlay = true,
                    Container = mediaSource.Container,
                    VideoCodec = videoStream?.Codec,
                    AudioCodec = audioStream?.Codec,
                    DurationTicks = item.RunTimeTicks ?? mediaSource.RunTimeTicks ?? 0
                };
            }
            else
            {
                _logger.LogInformation("Using transcoding for item {ItemId}", item.Id);
                return await GetTranscodingStreamAsync(item.Id.Value, streamRequest, deviceProfile, mediaType, item.RunTimeTicks ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get optimal stream for item {ItemId}", item.Id);
            return null;
        }
    }

    // MARK: GetStreamUrl
    private string GetStreamUrl(Guid itemId, string? container = null, long? startTimeTicks = null)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];

        var queryParams = new List<string>
        {
            $"X-Emby-Token={accessToken}",
            "EnableRedirection=false",
            "EnableRemoteMedia=false"
        };

        if (!string.IsNullOrEmpty(container))
        {
            queryParams.Add($"Container={container}");
        }

        // Add start time for resume functionality
        if (startTimeTicks.HasValue && startTimeTicks.Value > 0)
        {
            queryParams.Add($"StartTimeTicks={startTimeTicks.Value}");
            _logger.LogInformation("Adding resume position to direct play: {Position}ms", 
                TimeConversionUtil.TicksToMilliseconds(startTimeTicks.Value));
        }

        var queryString = string.Join("&", queryParams);
        return $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
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

        // Check if VLC can handle this format directly
        var container = mediaSource.Container?.ToLowerInvariant();
        var videoCodec = videoStream?.Codec?.ToLowerInvariant();
        var audioCodec = audioStream?.Codec?.ToLowerInvariant();

        // VLC handles these formats well without transcoding
        var vlcCompatibleContainers = new[] { "mp4", "mkv", "avi", "mov" };
        var vlcCompatibleVideoCodecs = new[] { "h264", "h.264", "avc", "hevc", "h265", "h.265" };
        var vlcCompatibleAudioCodecs = new[] { "aac", "mp3", "ac3", "eac3", "dts" };

        if (streamRequest.UserAgent.Contains("VLC", StringComparison.OrdinalIgnoreCase))
        {
            var containerOk = string.IsNullOrEmpty(container) || vlcCompatibleContainers.Contains(container);
            var videoOk = string.IsNullOrEmpty(videoCodec) || vlcCompatibleVideoCodecs.Contains(videoCodec);
            var audioOk = string.IsNullOrEmpty(audioCodec) || vlcCompatibleAudioCodecs.Contains(audioCodec);

            if (containerOk && videoOk && audioOk)
            {
                _logger.LogInformation("VLC direct play: Container={Container}, Video={VideoCodec}, Audio={AudioCodec}",
                    container, videoCodec, audioCodec);
                return Task.FromResult(true);
            }
        }

        return _deviceProfileService.ShouldDirectPlayAsync(
            deviceProfile,
            mediaSource.Container,
            videoStream?.Codec,
            audioStream?.Codec,
            mediaType);
    }

    // MARK: GetTranscodingStreamAsync
    private Task<StreamInfo?> GetTranscodingStreamAsync(Guid itemId, StreamRequest streamRequest, DeviceProfile? deviceProfile, string mediaType, long durationTicks)
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
            $"UserId={userId}",
            $"DeviceId={_configuration["JellyfinClient:DeviceId"]}",
            $"Container=mp4",
            $"VideoCodec=h264",
            $"AudioCodec=aac",
            "TranscodingMaxAudioChannels=2",
            "BreakOnNonKeyFrames=true",
            "EnableRedirection=false",
            "EnableRemoteMedia=false",
            "SegmentContainer=mp4",
            "MinSegments=1",
            "TranscodeSeekInfo=Auto",
            "CopyTimestamps=false",
            "EnableMpegtsM2TsMode=false",
            "EnableSubtitlesInManifest=false"
        };

        // Add start time for resume functionality
        if (streamRequest.StartTimeTicks.HasValue && streamRequest.StartTimeTicks.Value > 0)
        {
            transcodingParams.Add($"StartTimeTicks={streamRequest.StartTimeTicks.Value}");
            _logger.LogInformation("Adding resume position to transcoding: {Position}ms",
                TimeConversionUtil.TicksToMilliseconds(streamRequest.StartTimeTicks.Value));
        }

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
        else
        {
            transcodingParams.Add("VideoBitRate=8000000");
            transcodingParams.Add("AudioBitRate=128000");
        }

        var transcodingUrl = $"{serverUrl}/Videos/{itemId}/stream.mp4?" +
                           string.Join("&", transcodingParams) +
                           $"&X-Emby-Token={accessToken}";

        _logger.LogInformation("Generated fMP4 transcoding URL: {TranscodingUrl}", transcodingUrl);

        var streamInfo = new StreamInfo
        {
            Url = transcodingUrl,
            MimeType = "video/mp4",
            Size = 0, // Unknown for transcoded content
            IsDirectPlay = false,
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac",
            DurationTicks = durationTicks
        };

        return Task.FromResult<StreamInfo?>(streamInfo);
    }

    // MARK: StreamMediaAsync
    private async Task StreamMediaAsync(HttpListenerContext context, StreamInfo streamInfo, StreamRequest streamRequest, string? sessionId)
    {
        HttpClient? httpClient = null;
        Stream? sourceStream = null;
        long totalBytesStreamed = 0;
        var startTime = DateTime.UtcNow;
        long? rangeStart = null;

        try
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, streamInfo.Url);

            // Handle Range requests for seeking
            if (!string.IsNullOrEmpty(streamRequest.AcceptRanges))
            {
                requestMessage.Headers.Add("Range", streamRequest.AcceptRanges);
                _logger.LogInformation("SEEKING: Range request {Range} for session {SessionId}", streamRequest.AcceptRanges, sessionId);

                // Parse range to estimate seek position
                rangeStart = ParseRangeStart(streamRequest.AcceptRanges);
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jellyfin stream request failed with status {StatusCode}", response.StatusCode);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            // Set response headers
            context.Response.ContentType = streamInfo.MimeType;

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

            // Forward Content headers including Content-Range for seeking
            foreach (var header in response.Content.Headers)
            {
                switch (header.Key.ToLowerInvariant())
                {
                    case "content-range":
                        context.Response.AddHeader("Content-Range", string.Join(", ", header.Value));
                        _logger.LogDebug("Forwarding Content-Range: {ContentRange}", string.Join(", ", header.Value));
                        break;
                    case "content-disposition":
                        context.Response.AddHeader("Content-Disposition", string.Join(", ", header.Value));
                        break;
                    case "content-encoding":
                        context.Response.AddHeader("Content-Encoding", string.Join(", ", header.Value));
                        break;
                }
            }

            // Set proper status code for range requests
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                context.Response.StatusCode = 206;
                _logger.LogInformation("SEEKING: Returning partial content (206) for session {SessionId}", sessionId);
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            // Set DLNA headers with cache control
            context.Response.AddHeader("Accept-Ranges", "bytes");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.AddHeader("transferMode.dlna.org", "Streaming");
            context.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            context.Response.AddHeader("Pragma", "no-cache");
            context.Response.AddHeader("Expires", "0");

            if (streamInfo.IsDirectPlay)
            {
                var dlnaFeatures = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                context.Response.AddHeader("contentFeatures.dlna.org", dlnaFeatures);
            }
            else
            {
                context.Response.AddHeader("X-Transcoding", "true");
                // For transcoded content, be more aggressive about no caching
                context.Response.AddHeader("X-Content-Type-Options", "nosniff");
            }

            sourceStream = await response.Content.ReadAsStreamAsync();

            totalBytesStreamed = await CopyStreamWithReportingAsync(
                sourceStream,
                context.Response.OutputStream,
                streamInfo,
                context.Request.RemoteEndPoint,
                sessionId,
                rangeStart);

            var playedDuration = DateTime.UtcNow - startTime;
            var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, rangeStart);

            if (sessionId != null)
            {
                var watchThreshold = TimeConversionUtil.GetWatchedThreshold(streamInfo.DurationTicks);
                var shouldMarkWatched = estimatedPositionTicks >= watchThreshold && watchThreshold > 0;

                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, shouldMarkWatched);

                _logger.LogInformation("Stream session {SessionId} completed - Streamed: {StreamedMB}MB, Duration: {Duration}, Position: {Position}ms, Marked watched: {Watched}",
                    sessionId, totalBytesStreamed / 1024.0 / 1024.0, playedDuration, TimeConversionUtil.TicksToMilliseconds(estimatedPositionTicks), shouldMarkWatched);
            }

            _logger.LogInformation("Stream completed successfully for {Endpoint}", context.Request.RemoteEndPoint);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error during streaming");

            if (sessionId != null)
            {
                var playedDuration = DateTime.UtcNow - startTime;
                var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, rangeStart);
                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: false);
            }

            await SendErrorResponse(context, HttpStatusCode.BadGateway, "Upstream error");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stream cancelled by client {Endpoint}", context.Request.RemoteEndPoint);

            if (sessionId != null)
            {
                var playedDuration = DateTime.UtcNow - startTime;
                var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, rangeStart);
                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: false);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is System.Net.HttpListenerException)
        {
            var playedDuration = DateTime.UtcNow - startTime;
            var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, rangeStart);

            _logger.LogInformation("Stream interrupted after {TotalMB:F1} MB, duration: {Duration} - Client disconnected: {Error}",
                totalBytesStreamed / 1024.0 / 1024.0, playedDuration, ex.Message);

            if (sessionId != null)
            {
                // Better pause detection logic for movies of any length
                var hasSignificantProgress = totalBytesStreamed > 1024 * 1024; // 1MB minimum
                var notImmediateDisconnect = playedDuration.TotalSeconds > 5;
                var notExtremelyLongStreaming = playedDuration.TotalHours < 6; // Allow up to 6 hours

                // Calculate if we're likely in the middle of the movie vs at the end
                var estimatedProgress = streamInfo.DurationTicks > 0 ?
                    (double)estimatedPositionTicks / streamInfo.DurationTicks : 0.0;
                var likelyInMiddle = estimatedProgress < 0.9; // Less than 90% through

                // Treat as pause if we have progress, not immediate disconnect, and likely in middle of content
                var mightBePause = hasSignificantProgress && notImmediateDisconnect &&
                                  notExtremelyLongStreaming && likelyInMiddle;

                if (mightBePause)
                {
                    _logger.LogInformation("PAUSE DETECTED: Session {SessionId} interrupted after {Duration} with {MB:F1}MB transferred at {Progress:F1}% progress, treating as pause",
                        sessionId, playedDuration, totalBytesStreamed / 1024.0 / 1024.0, estimatedProgress * 100);
                    await _playbackReportingService.PausePlaybackAsync(sessionId, estimatedPositionTicks);
                }
                else
                {
                    var shouldMarkWatched = TimeConversionUtil.IsNearEnd(estimatedPositionTicks, streamInfo.DurationTicks) && estimatedPositionTicks > 0;
                    _logger.LogInformation("STREAM END: Session {SessionId} ended after {Duration}, position: {Position}ms, progress: {Progress:F1}%, marking watched: {Watched}",
                        sessionId, playedDuration, TimeConversionUtil.TicksToMilliseconds(estimatedPositionTicks), estimatedProgress * 100, shouldMarkWatched);
                    await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: shouldMarkWatched);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during streaming");

            if (sessionId != null)
            {
                await _playbackReportingService.StopPlaybackAsync(sessionId, markAsWatched: false);
            }

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

    // MARK: ParseRangeStart
    private long? ParseRangeStart(string rangeHeader)
    {
        try
        {
            // Parse "bytes=1234-" or "bytes=1234-5678" format
            if (rangeHeader.StartsWith("bytes="))
            {
                var rangeValue = rangeHeader.Substring(6);
                var parts = rangeValue.Split('-');
                if (parts.Length > 0 && long.TryParse(parts[0], out var start))
                {
                    return start;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse range header: {RangeHeader}", rangeHeader);
        }
        return null;
    }

    // MARK: CopyStreamWithReportingAsync
    private async Task<long> CopyStreamWithReportingAsync(Stream source, Stream destination, StreamInfo streamInfo, System.Net.IPEndPoint? clientEndpoint, string? sessionId, long? rangeStart = null)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var lastLogTime = startTime;
        var lastProgressReport = startTime;

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

                if (sessionId != null && (now - lastProgressReport).TotalSeconds >= 30)
                {
                    var playedDuration = now - startTime;
                    var estimatedPositionTicks = CalculateCurrentPosition(totalBytes, streamInfo, playedDuration, rangeStart);

                    await _playbackReportingService.UpdatePlaybackProgressAsync(sessionId, estimatedPositionTicks, isPaused: false);
                    lastProgressReport = now;
                }
            }

            var finalElapsed = DateTime.UtcNow - startTime;
            var finalAvgSpeed = totalBytes / finalElapsed.TotalSeconds / 1024 / 1024;

            _logger.LogInformation("Stream completed: {TotalMB:F1} MB in {Elapsed:mm\\:ss}, avg speed: {Speed:F1} MB/s, transcoded: {Transcoded}",
                totalBytes / 1024.0 / 1024.0, finalElapsed, finalAvgSpeed, !streamInfo.IsDirectPlay);

            return totalBytes;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            _logger.LogInformation("Stream interrupted after {TotalMB:F1} MB (client disconnected)", totalBytes / 1024.0 / 1024.0);
            throw;
        }
    }

    // MARK: CalculateCurrentPosition
    private long CalculateCurrentPosition(long bytesStreamed, StreamInfo streamInfo, TimeSpan playedDuration, long? rangeStart = null)
    {
        // If we have a range start (seeking), use that as base position
        if (rangeStart.HasValue && streamInfo.Size > 0 && streamInfo.DurationTicks > 0)
        {
            var basePositionRatio = (double)rangeStart.Value / streamInfo.Size;
            var basePositionTicks = (long)(streamInfo.DurationTicks * basePositionRatio);

            // For seeks, don't add time-based progress - return the seek position
            return Math.Min(basePositionTicks, streamInfo.DurationTicks);
        }

        // For transcoded content with unknown size, be conservative with time estimation
        if (!streamInfo.IsDirectPlay || streamInfo.Size <= 0)
        {
            // Use a more conservative time-based calculation for transcoded content
            // Account for the fact that transcoded streams might buffer/pause
            var conservativeProgress = (long)(playedDuration.Ticks * 0.8); // 80% of played time
            return Math.Min(conservativeProgress, streamInfo.DurationTicks);
        }

        // For direct play with known size, use byte-based calculation
        if (streamInfo.Size > 0 && streamInfo.DurationTicks > 0)
        {
            var progressRatio = (double)bytesStreamed / streamInfo.Size;
            var estimatedPositionTicks = (long)(streamInfo.DurationTicks * progressRatio);
            return Math.Min(estimatedPositionTicks, streamInfo.DurationTicks);
        }

        return Math.Min(playedDuration.Ticks, streamInfo.DurationTicks);
    }

    // MARK: CalculateEndPosition
    private long CalculateEndPosition(long totalBytesStreamed, StreamInfo streamInfo, TimeSpan totalPlayedDuration, long? rangeStart = null)
    {
        // If we have a range start (seeking), calculate from there
        if (rangeStart.HasValue && streamInfo.Size > 0 && streamInfo.DurationTicks > 0)
        {
            var basePositionRatio = (double)rangeStart.Value / streamInfo.Size;
            var basePositionTicks = (long)(streamInfo.DurationTicks * basePositionRatio);

            // For seeks that end quickly, just return the seek position
            if (totalPlayedDuration.TotalSeconds < 30)
            {
                return Math.Min(basePositionTicks, streamInfo.DurationTicks);
            }

            // For longer streams after seek, add conservative time progress
            var additionalTicks = (long)(totalPlayedDuration.Ticks * 0.7);
            return Math.Min(basePositionTicks + additionalTicks, streamInfo.DurationTicks);
        }

        // For transcoded content, be much more conservative
        if (!streamInfo.IsDirectPlay || streamInfo.Size <= 0)
        {
            if (totalPlayedDuration.TotalSeconds < 10)
            {
                return 0; // Very short duration suggests immediate disconnect
            }

            // Use conservative time-based calculation for transcoded content
            var conservativePosition = (long)(totalPlayedDuration.Ticks * 0.6); // 60% of played time
            return Math.Min(conservativePosition, streamInfo.DurationTicks);
        }

        // For direct play with known size, use byte-based calculation
        if (streamInfo.Size > 0 && streamInfo.DurationTicks > 0 && totalBytesStreamed > 0)
        {
            var progressRatio = (double)totalBytesStreamed / streamInfo.Size;
            var estimatedPositionTicks = (long)(streamInfo.DurationTicks * progressRatio);
            return Math.Min(estimatedPositionTicks, streamInfo.DurationTicks);
        }

        // Fallback to conservative time-based
        var fallbackPosition = (long)(totalPlayedDuration.Ticks * 0.7);
        return Math.Min(fallbackPosition, streamInfo.DurationTicks);
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