using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Services;
using FinDLNA.Models;
using FinDLNA.Utilities;
using System.Text.Json;
using System.Globalization;

namespace FinDLNA.Services;

// MARK: StreamingService
public class StreamingService
{
    private readonly ILogger<StreamingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;
    private readonly PlaybackReportingService _playbackReportingService;
    private readonly Dictionary<string, StreamProgress> _streamProgress = new();

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

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                _logger.LogError("Item not found for streaming: {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var resumePositionTicks = await GetResumePositionAsync(guid);
            var shouldResume = resumePositionTicks > 0;
            var rangeRequest = ParseRangeHeader(context.Request.Headers["Range"]);

            // MARK: Handle range requests for seeking
            long? seekPositionTicks = null;
            if (rangeRequest.HasValue && item.RunTimeTicks.HasValue)
            {
                seekPositionTicks = CalculateSeekPosition(rangeRequest.Value, item.RunTimeTicks.Value, resumePositionTicks);
                _logger.LogInformation("SEEK REQUEST: Range {RangeStart} -> seek position {SeekMs}ms", 
                    rangeRequest.Value, TimeConversionUtil.TicksToMilliseconds(seekPositionTicks ?? 0));
            }

            var existingSession = await _playbackReportingService.GetSessionByItemAsync(guid);
            if (existingSession != null)
            {
                sessionId = existingSession.SessionId;
                _logger.LogInformation("EXISTING SESSION: Using session {SessionId} for item {ItemId}", sessionId, itemId);

                if (seekPositionTicks.HasValue)
                {
                    await _playbackReportingService.UpdatePlaybackProgressAsync(sessionId, seekPositionTicks.Value, isPaused: false);
                    _streamProgress[sessionId] = new StreamProgress 
                    { 
                        CurrentTicks = seekPositionTicks.Value, 
                        StartTime = DateTime.UtcNow,
                        LastUpdateTime = DateTime.UtcNow 
                    };
                    _logger.LogInformation("SEEK UPDATE: Session {SessionId} seeking to {Position}ms", 
                        sessionId, TimeConversionUtil.TicksToMilliseconds(seekPositionTicks.Value));
                }
                else if (existingSession.IsPaused)
                {
                    await _playbackReportingService.ResumePlaybackAsync(sessionId, existingSession.LastPositionTicks);
                    // MARK: Initialize or update progress tracking for resumed session
                    _streamProgress[sessionId] = new StreamProgress 
                    { 
                        CurrentTicks = existingSession.LastPositionTicks, 
                        StartTime = DateTime.UtcNow,
                        LastUpdateTime = DateTime.UtcNow 
                    };
                    _logger.LogInformation("RESUME: Session {SessionId} resuming from {Position}ms", 
                        sessionId, TimeConversionUtil.TicksToMilliseconds(existingSession.LastPositionTicks));
                }
                else if (!_streamProgress.ContainsKey(sessionId))
                {
                    // MARK: Initialize progress tracking for existing session without tracking
                    _streamProgress[sessionId] = new StreamProgress 
                    { 
                        CurrentTicks = existingSession.LastPositionTicks, 
                        StartTime = DateTime.UtcNow,
                        LastUpdateTime = DateTime.UtcNow 
                    };
                }
            }
            else
            {
                var startPosition = seekPositionTicks ?? (shouldResume ? resumePositionTicks : 0);
                sessionId = await _playbackReportingService.StartPlaybackAsync(guid, userAgent, clientEndpoint, startPosition);
                
                if (sessionId != null)
                {
                    _streamProgress[sessionId] = new StreamProgress 
                    { 
                        CurrentTicks = startPosition, 
                        StartTime = DateTime.UtcNow,
                        LastUpdateTime = DateTime.UtcNow 
                    };
                    
                    _logger.LogInformation("NEW SESSION: Started session {SessionId} at position {StartMs}ms", 
                        sessionId, TimeConversionUtil.TicksToMilliseconds(startPosition));
                }
            }

            var streamRequest = ParseStreamRequest(context, deviceProfile);
            
            // MARK: Set start position for transcoding
            if (seekPositionTicks.HasValue)
            {
                streamRequest.StartTimeTicks = seekPositionTicks;
            }
            else if (shouldResume && existingSession == null)
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

            await StreamMediaAsync(context, streamInfo, streamRequest, sessionId, rangeRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);

            if (sessionId != null)
            {
                _streamProgress.Remove(sessionId);
                await _playbackReportingService.StopPlaybackAsync(sessionId, markAsWatched: false);
            }

            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
    }

    // MARK: ParseRangeHeader
    private long? ParseRangeHeader(string? rangeHeader)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes="))
            return null;

        try
        {
            var rangeValue = rangeHeader.Substring(6);
            var parts = rangeValue.Split('-');
            if (parts.Length > 0 && long.TryParse(parts[0], out var start))
            {
                return start;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse range header: {RangeHeader}", rangeHeader);
        }
        return null;
    }

    // MARK: CalculateSeekPosition
    private long CalculateSeekPosition(long byteOffset, long totalDurationTicks, long resumePositionTicks)
    {
        // MARK: For small byte offsets, return the resume position as-is
        if (byteOffset < 1024 * 1024) // Less than 1MB
        {
            return resumePositionTicks;
        }

        // MARK: Calculate absolute seek position based on byte offset percentage
        var estimatedBitrate = 8000000; // 8Mbps default
        var estimatedTotalBytes = TimeConversionUtil.TicksToSeconds(totalDurationTicks) * estimatedBitrate / 8;
        var offsetPercentage = Math.Min(1.0, byteOffset / estimatedTotalBytes);
        
        // MARK: Return ABSOLUTE position, not relative to resume position
        var absoluteSeekPosition = (long)(totalDurationTicks * offsetPercentage);

        _logger.LogDebug("SEEK CALCULATION: Byte offset {ByteOffset} ({PercentF:F1}%) -> ABSOLUTE position {SeekMs}ms", 
            byteOffset, offsetPercentage * 100, TimeConversionUtil.TicksToMilliseconds(absoluteSeekPosition));

        return Math.Max(0, Math.Min(absoluteSeekPosition, totalDurationTicks));
    }

    // MARK: GetResumePositionAsync
    private async Task<long> GetResumePositionAsync(Guid itemId)
    {
        try
        {
            var userId = _configuration["Jellyfin:UserId"];
            if (string.IsNullOrEmpty(userId))
                return 0;

            var userDataUrl = $"{_configuration["Jellyfin:ServerUrl"]?.TrimEnd('/')}/Users/{userId}/Items/{itemId}/UserData";
            var accessToken = _configuration["Jellyfin:AccessToken"];

            using var httpClient = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, userDataUrl);
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var jsonContent = await response.Content.ReadAsStringAsync();
                var userData = JsonSerializer.Deserialize<JsonElement>(jsonContent);

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
            // MARK: Use direct stream first for better compatibility
            var directStreamInfo = await GetDirectStreamAsync(item.Id.Value, streamRequest, item.RunTimeTicks ?? 0);
            if (directStreamInfo != null)
            {
                _logger.LogInformation("DIRECT STREAM: Using direct stream for item {ItemId}", item.Id);
                return directStreamInfo;
            }

            _logger.LogInformation("TRANSCODING: Using fMP4 transcoding for item {ItemId}", item.Id);
            return await GetFmp4StreamAsync(item.Id.Value, streamRequest, deviceProfile, GetMediaType(item), item.RunTimeTicks ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get stream for item {ItemId}", item.Id);
            return null;
        }
    }

    // MARK: GetDirectStreamAsync
    private Task<StreamInfo?> GetDirectStreamAsync(Guid itemId, StreamRequest streamRequest, long durationTicks)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
            return Task.FromResult<StreamInfo?>(null);

        var directParams = new List<string>
        {
            $"Static=true",
            $"MediaSourceId={itemId}",
            $"X-Emby-Token={accessToken}"
        };

        if (streamRequest.StartTimeTicks.HasValue && streamRequest.StartTimeTicks.Value > 0)
        {
            directParams.Add($"StartTimeTicks={streamRequest.StartTimeTicks.Value}");
        }

        var directUrl = $"{serverUrl}/Videos/{itemId}/stream?" + string.Join("&", directParams);

        var streamInfo = new StreamInfo
        {
            Url = directUrl,
            MimeType = "video/mp4",
            Size = 0,
            IsDirectPlay = true,
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac",
            DurationTicks = durationTicks
        };

        return Task.FromResult<StreamInfo?>(streamInfo);
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
            "BreakOnNonKeyFrames=false", // MARK: Changed to improve seeking
            "EnableRedirection=false",
            "EnableRemoteMedia=false",
            "SegmentContainer=mp4",
            "MinSegments=0", // MARK: Disable segmentation for single file
            "TranscodeSeekInfo=Auto",
            "CopyTimestamps=false",
            "EnableMpegtsM2TsMode=false",
            "EnableSubtitlesInManifest=false", // MARK: Disable for compatibility
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
            _logger.LogInformation("TRANSCODING SEEK: Adding start position {Position}ms to transcode request",
                TimeConversionUtil.TicksToMilliseconds(streamRequest.StartTimeTicks.Value));
        }

        if (streamRequest.MaxBitrate.HasValue)
        {
            fmp4Params.Add($"VideoBitRate={streamRequest.MaxBitrate}");
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
    private async Task StreamMediaAsync(HttpListenerContext context, StreamInfo streamInfo, StreamRequest streamRequest, string? sessionId, long? rangeStart)
    {
        HttpClient? httpClient = null;
        Stream? sourceStream = null;
        long totalBytesStreamed = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromHours(2); // MARK: Longer timeout for movies

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, streamInfo.Url);

            if (!string.IsNullOrEmpty(streamRequest.AcceptRanges))
            {
                requestMessage.Headers.Add("Range", streamRequest.AcceptRanges);
                _logger.LogInformation("RANGE REQUEST: Forwarding range {Range} to Jellyfin", streamRequest.AcceptRanges);
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Jellyfin stream request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            // MARK: Set response headers for better DLNA compatibility
            context.Response.ContentType = streamInfo.MimeType;
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.PartialContent ? 206 : 200;

            // MARK: Forward content headers
            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            foreach (var header in response.Content.Headers)
            {
                switch (header.Key.ToLowerInvariant())
                {
                    case "content-range":
                        context.Response.AddHeader("Content-Range", string.Join(", ", header.Value));
                        break;
                    case "accept-ranges":
                        context.Response.AddHeader("Accept-Ranges", string.Join(", ", header.Value));
                        break;
                }
            }

            // MARK: Add comprehensive DLNA headers
            var durationSeconds = TimeConversionUtil.TicksToSeconds(streamInfo.DurationTicks);
            
            context.Response.AddHeader("Accept-Ranges", "bytes");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.AddHeader("transferMode.dlna.org", "Streaming");
            context.Response.AddHeader("contentFeatures.dlna.org", "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000");
            
            if (streamInfo.DurationTicks > 0)
            {
                context.Response.AddHeader("TimeSeekRange.dlna.org", $"npt=0-{durationSeconds:F3}");
                context.Response.AddHeader("X-Content-Duration", durationSeconds.ToString("F3", CultureInfo.InvariantCulture));
                context.Response.AddHeader("Content-Duration", durationSeconds.ToString("F0", CultureInfo.InvariantCulture));
                
                _logger.LogInformation("DURATION HEADERS: Set duration to {Duration}s for better seeking", durationSeconds);
            }

            sourceStream = await response.Content.ReadAsStreamAsync();

            totalBytesStreamed = await CopyStreamWithProgressAsync(
                sourceStream,
                context.Response.OutputStream,
                streamInfo,
                sessionId);

            var playedDuration = DateTime.UtcNow - startTime;
            var finalPosition = CalculateFinalPosition(sessionId, totalBytesStreamed, streamInfo, playedDuration);

            if (sessionId != null)
            {
                var watchThreshold = TimeConversionUtil.GetWatchedThreshold(streamInfo.DurationTicks);
                var shouldMarkWatched = finalPosition >= watchThreshold && watchThreshold > 0;

                await _playbackReportingService.StopPlaybackAsync(sessionId, finalPosition, shouldMarkWatched);
                _streamProgress.Remove(sessionId);

                _logger.LogInformation("STREAM COMPLETE: Session {SessionId} finished at {Position}ms, watched: {Watched}",
                    sessionId, TimeConversionUtil.TicksToMilliseconds(finalPosition), shouldMarkWatched);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is HttpListenerException)
        {
            var playedDuration = DateTime.UtcNow - startTime;
            var finalPosition = CalculateFinalPosition(sessionId, totalBytesStreamed, streamInfo, playedDuration);

            if (sessionId != null)
            {
                // MARK: Enhanced pause detection logic
                var hasSignificantData = totalBytesStreamed > 2 * 1024 * 1024; // 2MB threshold
                var hasPlayedForAwhile = playedDuration.TotalSeconds > 15;
                var notAtEnd = finalPosition < streamInfo.DurationTicks * 0.95; // Not in last 5%
                var connectionLostGracefully = ex.Message.Contains("Broken pipe") || 
                                            ex.Message.Contains("connection was closed") ||
                                            ex.Message.Contains("remote host closed");

                var isLikelyPause = hasSignificantData && hasPlayedForAwhile && notAtEnd && connectionLostGracefully;
                
                if (isLikelyPause)
                {
                    _logger.LogInformation("PAUSE DETECTED: Session {SessionId} likely paused at {Position}ms (streamed {MB}MB, played {Duration}s)",
                        sessionId, TimeConversionUtil.TicksToMilliseconds(finalPosition), 
                        totalBytesStreamed / 1024.0 / 1024.0, playedDuration.TotalSeconds);
                        
                    await _playbackReportingService.PausePlaybackAsync(sessionId, finalPosition);
                    // MARK: Keep session alive for pause, don't remove from _streamProgress
                }
                else
                {
                    _logger.LogInformation("STREAM ENDED: Session {SessionId} ended at {Position}ms (streamed {MB}MB, played {Duration}s)",
                        sessionId, TimeConversionUtil.TicksToMilliseconds(finalPosition),
                        totalBytesStreamed / 1024.0 / 1024.0, playedDuration.TotalSeconds);
                        
                    await _playbackReportingService.StopPlaybackAsync(sessionId, finalPosition, markAsWatched: false);
                    _streamProgress.Remove(sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during streaming");

            if (sessionId != null)
            {
                await _playbackReportingService.StopPlaybackAsync(sessionId, markAsWatched: false);
                _streamProgress.Remove(sessionId);
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

    // MARK: CopyStreamWithProgressAsync
    private async Task<long> CopyStreamWithProgressAsync(Stream source, Stream destination, StreamInfo streamInfo, string? sessionId)
    {
        const int bufferSize = 131072; // 128KB buffer
        var buffer = new byte[bufferSize];
        long totalBytes = 0;
        var streamStartTime = DateTime.UtcNow;
        var lastProgressReport = streamStartTime;
        var lastLogTime = streamStartTime;
        var lastDataTime = streamStartTime; // Track when we last received data
        var progressIncrement = TimeConversionUtil.SecondsToTicks(2); // 2 second increments

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, 0, bufferSize);
                if (bytesRead == 0) break;

                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();

                totalBytes += bytesRead;
                lastDataTime = DateTime.UtcNow; // Update data timestamp
                var now = DateTime.UtcNow;

                // MARK: Conservative progress tracking with fixed increments
                if (sessionId != null && _streamProgress.TryGetValue(sessionId, out var progress))
                {
                    var timeSinceLastUpdate = now - progress.LastUpdateTime;
                    
                    // MARK: Only advance by small, predictable increments
                    if (timeSinceLastUpdate.TotalSeconds >= 2.0)
                    {
                        // MARK: Cap advancement to prevent wild jumps
                        var maxAdvancement = TimeConversionUtil.SecondsToTicks(Math.Min(5.0, timeSinceLastUpdate.TotalSeconds));
                        var newPosition = progress.CurrentTicks + maxAdvancement;
                        
                        // MARK: Never exceed total duration
                        progress.CurrentTicks = Math.Min(newPosition, streamInfo.DurationTicks);
                        progress.LastUpdateTime = now;

                        _logger.LogDebug("CONTROLLED PROGRESS: Session {SessionId} advanced {AdvanceMs}ms to {Position}ms (elapsed: {Elapsed}s)", 
                            sessionId, 
                            TimeConversionUtil.TicksToMilliseconds(maxAdvancement),
                            TimeConversionUtil.TicksToMilliseconds(progress.CurrentTicks),
                            timeSinceLastUpdate.TotalSeconds);
                    }

                    // MARK: Report to Jellyfin every 15 seconds
                    if ((now - lastProgressReport).TotalSeconds >= 15)
                    {
                        await _playbackReportingService.UpdatePlaybackProgressAsync(sessionId, progress.CurrentTicks, isPaused: false);
                        lastProgressReport = now;
                        
                        _logger.LogInformation("PROGRESS REPORT: Session {SessionId} reported at {Position}ms", 
                            sessionId, TimeConversionUtil.TicksToMilliseconds(progress.CurrentTicks));
                    }
                }

                if ((now - lastLogTime).TotalSeconds >= 30)
                {
                    var elapsed = now - streamStartTime;
                    var avgSpeed = totalBytes / elapsed.TotalSeconds / 1024 / 1024;
                    _logger.LogDebug("Streaming: {TotalMB:F1} MB, speed: {Speed:F1} MB/s",
                        totalBytes / 1024.0 / 1024.0, avgSpeed);
                    lastLogTime = now;
                }
            }

            return totalBytes;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            _logger.LogInformation("Stream interrupted after {TotalMB:F1} MB", totalBytes / 1024.0 / 1024.0);
            throw;
        }
    }

    // MARK: CalculateFinalPosition
    private long CalculateFinalPosition(string? sessionId, long bytesStreamed, StreamInfo streamInfo, TimeSpan playedDuration)
    {
        if (sessionId != null && _streamProgress.TryGetValue(sessionId, out var progress))
        {
            // MARK: Use tracked position, but validate it's reasonable
            var trackedPosition = progress.CurrentTicks;
            
            // MARK: Sanity check - don't let it exceed what's reasonable for the time played
            var maxReasonablePosition = progress.StartTime == DateTime.MinValue ? 
                (long)(playedDuration.Ticks * 1.2) : // 20% buffer for initial streams
                progress.CurrentTicks; // Trust tracked position for ongoing streams
            
            var finalPosition = Math.Min(trackedPosition, Math.Min(maxReasonablePosition, streamInfo.DurationTicks));
            
            _logger.LogDebug("FINAL POSITION: Tracked={TrackedMs}ms, MaxReasonable={MaxMs}ms, Final={FinalMs}ms", 
                TimeConversionUtil.TicksToMilliseconds(trackedPosition),
                TimeConversionUtil.TicksToMilliseconds(maxReasonablePosition),
                TimeConversionUtil.TicksToMilliseconds(finalPosition));
            
            return finalPosition;
        }

        // MARK: Fallback - very conservative estimate
        if (playedDuration.TotalSeconds < 5)
            return 0;

        var conservativePosition = (long)(playedDuration.Ticks * 0.5); // Even more conservative
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