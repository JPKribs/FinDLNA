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

        _logger.LogInformation("STREAMING SERVICE: HandleStreamRequest called for item {ItemId} from {UserAgent} at {RemoteEndPoint}",
            itemId, context.Request.UserAgent, context.Request.RemoteEndPoint);
        
        _logger.LogInformation("STREAMING SERVICE: Request headers: {Headers}", 
            string.Join(", ", context.Request.Headers.AllKeys.Select(k => $"{k}={context.Request.Headers[k]}")));

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

            var resumePositionTicks = await GetResumePositionAsync(guid);
            var shouldResume = resumePositionTicks > 0;

            var existingSession = await _playbackReportingService.GetSessionByItemAsync(guid);
            if (existingSession != null && !string.IsNullOrEmpty(context.Request.Headers["Range"]))
            {
                _logger.LogInformation("RESUME/SEEK: Existing session {SessionId} for item {ItemId}",
                    existingSession.SessionId, itemId);
                sessionId = existingSession.SessionId;

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

                sessionId = await _playbackReportingService.StartPlaybackAsync(guid, userAgent, clientEndpoint, shouldResume ? resumePositionTicks : null);
                if (sessionId != null)
                {
                    _logger.LogInformation("Started playback session {SessionId} for item {ItemId} with resume position {ResumeMs}ms", 
                        sessionId, itemId, shouldResume ? TimeConversionUtil.TicksToMilliseconds(resumePositionTicks) : 0);
                }
                else
                {
                    _logger.LogWarning("Failed to start playback session for item {ItemId}", itemId);
                }
            }

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                _logger.LogError("Item not found for streaming: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var streamRequest = ParseStreamRequest(context, deviceProfile);

            if (shouldResume && existingSession == null)
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

            _logger.LogInformation("STREAMING: Starting fMP4 stream for item {ItemId}, session {SessionId}, resume: {Resume}ms",
                itemId, sessionId, shouldResume && existingSession == null ? TimeConversionUtil.TicksToMilliseconds(resumePositionTicks) : 0);

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
                    var twoMinutesInTicks = TimeConversionUtil.MinutesToTicks(2);
                    if (positionTicks > twoMinutesInTicks)
                    {
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
            _logger.LogInformation("FORCING fMP4: Always using fMP4 transcoding with all tracks for item {ItemId}", item.Id);
            
            return await GetFmp4StreamAsync(item.Id.Value, streamRequest, deviceProfile, GetMediaType(item), item.RunTimeTicks ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get fMP4 stream for item {ItemId}", item.Id);
            return null;
        }
    }

    // MARK: GetFmp4StreamAsync
    private Task<StreamInfo?> GetFmp4StreamAsync(Guid itemId, StreamRequest streamRequest, DeviceProfile? deviceProfile, string mediaType, long durationTicks)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        var userId = _configuration["Jellyfin:UserId"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId))
        {
            _logger.LogError("Missing Jellyfin configuration for fMP4 streaming");
            return Task.FromResult<StreamInfo?>(null);
        }

        var fmp4Params = new List<string>
        {
            $"UserId={userId}",
            $"DeviceId={_configuration["JellyfinClient:DeviceId"]}",
            "Container=mp4",
            "VideoCodec=h264",
            "AudioCodec=aac",
            "TranscodingMaxAudioChannels=8",
            "BreakOnNonKeyFrames=true",
            "EnableRedirection=false",
            "EnableRemoteMedia=false",
            "SegmentContainer=mp4",
            "MinSegments=1",
            "TranscodeSeekInfo=Auto",
            "CopyTimestamps=false",
            "EnableMpegtsM2TsMode=false",
            "EnableSubtitlesInManifest=true",
            "AllowVideoStreamCopy=false",
            "AllowAudioStreamCopy=false",
            "RequireAvc=true",
            "RequireNonAnamorphic=false",
            "EnableAudioVbrEncoding=true",
            "Context=Streaming",
            "StreamOptions=",
            "EnableAdaptiveBitrateStreaming=false",
            "EnableAutoStreamCopy=false",
            "SubtitleMethod=Encode"
        };

        if (streamRequest.StartTimeTicks.HasValue && streamRequest.StartTimeTicks.Value > 0)
        {
            fmp4Params.Add($"StartTimeTicks={streamRequest.StartTimeTicks.Value}");
            _logger.LogInformation("RESUME: Adding resume position to fMP4 stream: {Position}ms (ticks: {Ticks})",
                TimeConversionUtil.TicksToMilliseconds(streamRequest.StartTimeTicks.Value), streamRequest.StartTimeTicks.Value);
        }

        if (streamRequest.MaxBitrate.HasValue)
        {
            fmp4Params.Add($"VideoBitRate={streamRequest.MaxBitrate}");
            fmp4Params.Add($"AudioBitRate=256000");
        }
        else if (deviceProfile?.MaxStreamingBitrate > 0)
        {
            fmp4Params.Add($"VideoBitRate={deviceProfile.MaxStreamingBitrate}");
            fmp4Params.Add($"AudioBitRate=256000");
        }
        else
        {
            fmp4Params.Add("VideoBitRate=8000000");
            fmp4Params.Add("AudioBitRate=256000");
        }

        var fmp4Url = $"{serverUrl}/Videos/{itemId}/stream.mp4?" +
                      string.Join("&", fmp4Params) +
                      $"&X-Emby-Token={accessToken}";

        _logger.LogInformation("Generated fMP4 stream URL: {Fmp4Url}", fmp4Url);
        _logger.LogDebug("fMP4 parameters: {Parameters}", string.Join(", ", fmp4Params));

        var streamInfo = new StreamInfo
        {
            Url = fmp4Url,
            MimeType = "video/mp4",
            Size = 0,
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
        long? resumePositionTicks = streamRequest.StartTimeTicks;

        try
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, streamInfo.Url);

            if (!string.IsNullOrEmpty(streamRequest.AcceptRanges))
            {
                requestMessage.Headers.Add("Range", streamRequest.AcceptRanges);
                _logger.LogInformation("SEEKING: Range request {Range} for session {SessionId}", streamRequest.AcceptRanges, sessionId);

                rangeStart = ParseRangeStart(streamRequest.AcceptRanges);
                
                if (rangeStart.HasValue && resumePositionTicks.HasValue)
                {
                    var seekPositionMs = TimeConversionUtil.TicksToMilliseconds(resumePositionTicks.Value);
                    _logger.LogInformation("SEEK WITH RESUME: Byte range {ByteRange} with resume position {ResumeMs}ms", 
                        rangeStart.Value, seekPositionMs);
                }
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            _logger.LogInformation("JELLYFIN RESPONSE: Status {StatusCode} for fMP4 request to {Url}", 
                response.StatusCode, streamInfo.Url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = "";
                try
                {
                    errorContent = await response.Content.ReadAsStringAsync();
                }
                catch { }
                
                _logger.LogError("Jellyfin fMP4 stream request failed with status {StatusCode}: {ErrorContent}", 
                    response.StatusCode, errorContent);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            context.Response.ContentType = streamInfo.MimeType;

            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
                _logger.LogDebug("Forwarding Content-Length: {Length}", response.Content.Headers.ContentLength.Value);
            }

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

            // Add comprehensive duration headers for DLNA clients
            if (streamInfo.DurationTicks > 0)
            {
                var durationSeconds = TimeConversionUtil.TicksToSeconds(streamInfo.DurationTicks);
                var durationMilliseconds = TimeConversionUtil.TicksToMilliseconds(streamInfo.DurationTicks);
                
                // Multiple duration headers for different client compatibility
                context.Response.AddHeader("X-Content-Duration", durationSeconds.ToString("F3"));
                context.Response.AddHeader("Content-Duration", durationSeconds.ToString("F0"));
                context.Response.AddHeader("TimeSeekRange.dlna.org", $"npt=0-{durationSeconds:F3}");
                context.Response.AddHeader("X-Content-Length", streamInfo.Size.ToString());
                
                // MediaInfo headers that some clients use
                context.Response.AddHeader("X-Media-Duration", durationMilliseconds.ToString());
                context.Response.AddHeader("X-Duration", durationSeconds.ToString("F3"));
                
                _logger.LogInformation("Added duration headers: {DurationSeconds}s ({DurationTicks} ticks, {DurationMs}ms)", 
                    durationSeconds, streamInfo.DurationTicks, durationMilliseconds);
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                context.Response.StatusCode = 206;
                _logger.LogInformation("SEEKING: Returning partial content (206) for session {SessionId}", sessionId);
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            context.Response.AddHeader("Accept-Ranges", "bytes");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.AddHeader("transferMode.dlna.org", "Streaming");
            context.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            context.Response.AddHeader("Pragma", "no-cache");
            context.Response.AddHeader("Expires", "0");
            context.Response.AddHeader("X-Transcoding", "fMP4");
            context.Response.AddHeader("X-Content-Type-Options", "nosniff");

            var dlnaFeatures = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
            context.Response.AddHeader("contentFeatures.dlna.org", dlnaFeatures);

            sourceStream = await response.Content.ReadAsStreamAsync();

            totalBytesStreamed = await CopyStreamWithReportingAsync(
                sourceStream,
                context.Response.OutputStream,
                streamInfo,
                context.Request.RemoteEndPoint,
                sessionId,
                resumePositionTicks);

            var playedDuration = DateTime.UtcNow - startTime;
            var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, resumePositionTicks);

            if (sessionId != null)
            {
                var watchThreshold = TimeConversionUtil.GetWatchedThreshold(streamInfo.DurationTicks);
                var shouldMarkWatched = estimatedPositionTicks >= watchThreshold && watchThreshold > 0;

                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, shouldMarkWatched);

                _logger.LogInformation("fMP4 stream session {SessionId} completed - Streamed: {StreamedMB}MB, Duration: {Duration}, Position: {Position}ms, Marked watched: {Watched}",
                    sessionId, totalBytesStreamed / 1024.0 / 1024.0, playedDuration, TimeConversionUtil.TicksToMilliseconds(estimatedPositionTicks), shouldMarkWatched);
            }

            _logger.LogInformation("fMP4 stream completed successfully for {Endpoint}", context.Request.RemoteEndPoint);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error during fMP4 streaming");

            if (sessionId != null)
            {
                var playedDuration = DateTime.UtcNow - startTime;
                var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, resumePositionTicks);
                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: false);
            }

            await SendErrorResponse(context, HttpStatusCode.BadGateway, "Upstream error");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("fMP4 stream cancelled by client {Endpoint}", context.Request.RemoteEndPoint);

            if (sessionId != null)
            {
                var playedDuration = DateTime.UtcNow - startTime;
                var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, resumePositionTicks);
                await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: false);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is System.Net.HttpListenerException)
        {
            var playedDuration = DateTime.UtcNow - startTime;
            var estimatedPositionTicks = CalculateEndPosition(totalBytesStreamed, streamInfo, playedDuration, resumePositionTicks);

            // Determine if this was a normal client disconnect vs an error
            var isNormalDisconnect = ex.Message.Contains("Broken pipe") || 
                                   ex.Message.Contains("connection was closed") ||
                                   ex.Message.Contains("transport connection");

            if (isNormalDisconnect)
            {
                _logger.LogInformation("Client disconnected from fMP4 stream after {TotalMB:F1} MB, duration: {Duration}",
                    totalBytesStreamed / 1024.0 / 1024.0, playedDuration);
            }
            else
            {
                _logger.LogWarning("fMP4 stream interrupted after {TotalMB:F1} MB, duration: {Duration} - {Error}",
                    totalBytesStreamed / 1024.0 / 1024.0, playedDuration, ex.Message);
            }

            if (sessionId != null)
            {
                var hasSignificantProgress = totalBytesStreamed > 1024 * 1024;
                var notImmediateDisconnect = playedDuration.TotalSeconds > 5;
                var notExtremelyLongStreaming = playedDuration.TotalHours < 6;

                var estimatedProgress = streamInfo.DurationTicks > 0 ?
                    (double)estimatedPositionTicks / streamInfo.DurationTicks : 0.0;
                var likelyInMiddle = estimatedProgress < 0.9;

                var mightBePause = hasSignificantProgress && notImmediateDisconnect &&
                                  notExtremelyLongStreaming && likelyInMiddle && isNormalDisconnect;

                if (mightBePause)
                {
                    _logger.LogInformation("PAUSE DETECTED: fMP4 session {SessionId} paused after {Duration} at {Progress:F1}% progress",
                        sessionId, playedDuration, estimatedProgress * 100);
                    await _playbackReportingService.PausePlaybackAsync(sessionId, estimatedPositionTicks);
                }
                else
                {
                    var shouldMarkWatched = TimeConversionUtil.IsNearEnd(estimatedPositionTicks, streamInfo.DurationTicks) && estimatedPositionTicks > 0;
                    _logger.LogInformation("STREAM END: fMP4 session {SessionId} ended after {Duration}, position: {Position}ms, progress: {Progress:F1}%, marking watched: {Watched}",
                        sessionId, playedDuration, TimeConversionUtil.TicksToMilliseconds(estimatedPositionTicks), estimatedProgress * 100, shouldMarkWatched);
                    await _playbackReportingService.StopPlaybackAsync(sessionId, estimatedPositionTicks, markAsWatched: shouldMarkWatched);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during fMP4 streaming");

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
    private async Task<long> CopyStreamWithReportingAsync(Stream source, Stream destination, StreamInfo streamInfo, System.Net.IPEndPoint? clientEndpoint, string? sessionId, long? resumePositionTicks = null)
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

                    _logger.LogDebug("fMP4 streaming progress: {TotalMB:F1} MB in {Elapsed:mm\\:ss}, avg speed: {Speed:F1} MB/s to {Client}",
                        totalBytes / 1024.0 / 1024.0, elapsed, avgSpeed, clientEndpoint);

                    lastLogTime = now;
                }

                if (sessionId != null && (now - lastProgressReport).TotalSeconds >= 30)
                {
                    // Check if session still exists before reporting progress
                    var activeSessions = await _playbackReportingService.GetActiveSessionsAsync();
                    if (activeSessions.ContainsKey(sessionId))
                    {
                        var playedDuration = now - startTime;
                        var estimatedPositionTicks = CalculateCurrentPosition(totalBytes, streamInfo, playedDuration, resumePositionTicks);

                        await _playbackReportingService.UpdatePlaybackProgressAsync(sessionId, estimatedPositionTicks, isPaused: false);
                        lastProgressReport = now;
                    }
                }
            }

            var finalElapsed = DateTime.UtcNow - startTime;
            var finalAvgSpeed = totalBytes / finalElapsed.TotalSeconds / 1024 / 1024;

            _logger.LogInformation("fMP4 stream completed: {TotalMB:F1} MB in {Elapsed:mm\\:ss}, avg speed: {Speed:F1} MB/s",
                totalBytes / 1024.0 / 1024.0, finalElapsed, finalAvgSpeed);

            return totalBytes;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            _logger.LogInformation("fMP4 stream interrupted after {TotalMB:F1} MB (client disconnected)", totalBytes / 1024.0 / 1024.0);
            throw;
        }
    }

    // MARK: CalculateCurrentPosition
    private long CalculateCurrentPosition(long bytesStreamed, StreamInfo streamInfo, TimeSpan playedDuration, long? resumePositionTicks = null)
    {
        if (resumePositionTicks.HasValue)
        {
            var resumeBaseTicks = resumePositionTicks.Value;
            var additionalPlayedTicks = (long)(playedDuration.Ticks * 0.9);
            var currentPositionTicks = resumeBaseTicks + additionalPlayedTicks;
            
            var result = Math.Min(currentPositionTicks, streamInfo.DurationTicks);
            
            _logger.LogDebug("RESUME POSITION CALC: Resume base: {ResumeMs}ms, Additional played: {AdditionalMs}ms, Current: {CurrentMs}ms",
                TimeConversionUtil.TicksToMilliseconds(resumeBaseTicks),
                TimeConversionUtil.TicksToMilliseconds(additionalPlayedTicks),
                TimeConversionUtil.TicksToMilliseconds(result));
            
            return result;
        }

        var conservativeProgress = (long)(playedDuration.Ticks * 0.8);
        return Math.Min(conservativeProgress, streamInfo.DurationTicks);
    }

    // MARK: CalculateEndPosition
    private long CalculateEndPosition(long totalBytesStreamed, StreamInfo streamInfo, TimeSpan totalPlayedDuration, long? resumePositionTicks = null)
    {
        if (resumePositionTicks.HasValue)
        {
            var resumeBaseTicks = resumePositionTicks.Value;
            
            if (totalPlayedDuration.TotalSeconds < 30)
            {
                _logger.LogDebug("RESUME END CALC: Short duration ({Duration}s), returning resume position {ResumeMs}ms",
                    totalPlayedDuration.TotalSeconds, TimeConversionUtil.TicksToMilliseconds(resumeBaseTicks));
                return Math.Min(resumeBaseTicks, streamInfo.DurationTicks);
            }

            var additionalTicks = (long)(totalPlayedDuration.Ticks * 0.7);
            var endPositionTicks = resumeBaseTicks + additionalTicks;
            var result = Math.Min(endPositionTicks, streamInfo.DurationTicks);
            
            _logger.LogDebug("RESUME END CALC: Resume base: {ResumeMs}ms, Additional: {AdditionalMs}ms, End: {EndMs}ms",
                TimeConversionUtil.TicksToMilliseconds(resumeBaseTicks),
                TimeConversionUtil.TicksToMilliseconds(additionalTicks),
                TimeConversionUtil.TicksToMilliseconds(result));
            
            return result;
        }

        if (totalPlayedDuration.TotalSeconds < 10)
        {
            return 0;
        }

        var conservativePosition = (long)(totalPlayedDuration.Ticks * 0.6);
        return Math.Min(conservativePosition, streamInfo.DurationTicks);
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
}