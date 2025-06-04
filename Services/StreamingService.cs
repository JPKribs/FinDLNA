using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Services;
using FinDLNA.Models;
using FinDLNA.Utilities;
using System.Text.Json;
using System.Collections.Concurrent;

namespace FinDLNA.Services;

// MARK: StreamingService
public class StreamingService
{
    private readonly ILogger<StreamingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;
    private readonly PlaybackReportingService _playbackReportingService;
    
    private readonly ConcurrentDictionary<Guid, ActiveSession> _itemSessions = new();
    private readonly ConcurrentDictionary<string, StreamProgress> _streamProgress = new();
    private readonly object _sessionLock = new object();

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

        try
        {
            if (!Guid.TryParse(itemId, out var guid))
            {
                await SendErrorResponse(context, HttpStatusCode.BadRequest, "Invalid item ID");
                return;
            }

            itemGuid = guid;
            var userAgent = context.Request.UserAgent ?? "";
            var clientEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "";

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var resumePosition = await GetResumePositionAsync(guid);
            var rangeRequest = ParseRangeHeader(context.Request.Headers["Range"]);

            sessionId = await GetOrCreateSessionAsync(guid, userAgent, clientEndpoint, resumePosition, rangeRequest, item.RunTimeTicks ?? 0);

            if (sessionId == null)
            {
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Session creation failed");
                return;
            }

            var streamRequest = ParseStreamRequest(context);
            
            // Handle seeking
            if (rangeRequest.HasValue && item.RunTimeTicks.HasValue)
            {
                var seekPosition = CalculateSeekPosition(rangeRequest.Value, item.RunTimeTicks.Value, resumePosition);
                if (rangeRequest.Value > 1024 * 1024) // > 1MB indicates seek
                {
                    streamRequest.StartTimeTicks = seekPosition;
                    await UpdateSessionPosition(sessionId, seekPosition, isSeek: true);
                }
            }

            var streamInfo = await GetOptimalStreamAsync(item, streamRequest);
            
            if (streamInfo == null)
            {
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream unavailable");
                return;
            }

            await StreamMediaAsync(context, streamInfo, sessionId, rangeRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);
            if (sessionId != null && itemGuid.HasValue)
            {
                await CleanupSession(sessionId, itemGuid.Value);
            }
            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream error");
        }
    }

    // MARK: GetOrCreateSessionAsync
    private async Task<string?> GetOrCreateSessionAsync(Guid itemId, string userAgent, string clientEndpoint, long resumePosition, long? rangeRequest, long totalDuration)
    {
        // Check if we have a recent session for this item
        if (_itemSessions.TryGetValue(itemId, out var existingSession))
        {
            if (_streamProgress.TryGetValue(existingSession.SessionId, out var existingProgress))
            {
                var timeSinceLastActivity = DateTime.UtcNow - existingProgress.LastUpdateTime;
                
                // If session is very recent (< 30 seconds), reuse it instead of creating spam
                if (timeSinceLastActivity.TotalSeconds < 30)
                {
                    _logger.LogInformation("REUSING RECENT SESSION: {SessionId} for item {ItemId} (last activity: {Seconds}s ago)", 
                        existingSession.SessionId, itemId, timeSinceLastActivity.TotalSeconds);
                    
                    // Update position if this looks like a seek
                    if (rangeRequest.HasValue && totalDuration > 0)
                    {
                        var seekPosition = CalculateSeekPosition(rangeRequest.Value, totalDuration, resumePosition);
                        if (Math.Abs(seekPosition - existingProgress.CurrentTicks) > TimeConversionUtil.SecondsToTicks(10))
                        {
                            await UpdateSessionPosition(existingSession.SessionId, seekPosition, isSeek: true);
                        }
                    }
                    
                    return existingSession.SessionId;
                }
            }
        }

        // Clean up old session
        if (_itemSessions.TryGetValue(itemId, out var oldSession))
        {
            await CleanupSession(oldSession.SessionId, itemId);
        }

        // Determine start position - prefer actual resume position over range calculations
        var startPosition = resumePosition;
        if (rangeRequest.HasValue && totalDuration > 0)
        {
            var calculatedSeek = CalculateSeekPosition(rangeRequest.Value, totalDuration, resumePosition);
            // Only use calculated position if it's significantly different from resume position
            if (Math.Abs(calculatedSeek - resumePosition) > TimeConversionUtil.MinutesToTicks(1))
            {
                startPosition = calculatedSeek;
            }
        }

        var sessionId = await _playbackReportingService.StartPlaybackAsync(itemId, userAgent, clientEndpoint, startPosition);
        
        if (sessionId != null)
        {
            var session = new ActiveSession
            {
                SessionId = sessionId,
                ItemId = itemId,
                UserAgent = userAgent,
                ClientEndpoint = clientEndpoint,
                StartTime = DateTime.UtcNow
            };

            var progress = new StreamProgress
            {
                CurrentTicks = startPosition,
                StartTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow,
                LastReportedPosition = startPosition,
                LastReportedTime = DateTime.UtcNow,
                InitialPosition = startPosition
            };

            lock (_sessionLock)
            {
                _itemSessions[itemId] = session;
                _streamProgress[sessionId] = progress;
            }
            
            _logger.LogInformation("NEW SESSION: {SessionId} for item {ItemId} at {Position}ms", 
                sessionId, itemId, TimeConversionUtil.TicksToMilliseconds(startPosition));
        }

        return sessionId;
    }

    // MARK: UpdateSessionPosition
    private async Task UpdateSessionPosition(string sessionId, long newPositionTicks, bool isSeek = false)
    {
        if (!_streamProgress.TryGetValue(sessionId, out var progress)) return;

        var now = DateTime.UtcNow;
        var positionChanged = false;

        lock (_sessionLock)
        {
            if (!_streamProgress.TryGetValue(sessionId, out progress)) return;

            if (isSeek)
            {
                progress.CurrentTicks = newPositionTicks;
                progress.LastUpdateTime = now;
                progress.HasBeenSeeked = true;
                progress.LastSeekTime = now;
                positionChanged = true;
                
                _logger.LogInformation("SEEK: Session {SessionId} to {Position}ms", 
                    sessionId, TimeConversionUtil.TicksToMilliseconds(newPositionTicks));
            }
            else
            {
                // Only update if position actually advanced
                if (newPositionTicks > progress.CurrentTicks)
                {
                    progress.CurrentTicks = newPositionTicks;
                    progress.LastUpdateTime = now;
                    positionChanged = true;
                }
            }
        }

        // Only report if position actually changed
        if (positionChanged)
        {
            await ReportPositionChange(sessionId, newPositionTicks, isPaused: false);
        }
    }

    // MARK: ReportPositionChange
    private async Task ReportPositionChange(string sessionId, long positionTicks, bool isPaused)
    {
        if (!_streamProgress.TryGetValue(sessionId, out var progress)) return;

        var now = DateTime.UtcNow;
        var timeSinceLastReport = now - progress.LastReportedTime;
        
        // Always report seeks and pause states immediately
        // For regular progress, only report every 2+ seconds to avoid spam
        var shouldReport = isPaused || 
                          Math.Abs(positionTicks - progress.LastReportedPosition) > TimeConversionUtil.SecondsToTicks(30) ||
                          timeSinceLastReport.TotalSeconds >= 2.0;

        if (shouldReport)
        {
            await _playbackReportingService.UpdatePlaybackProgressAsync(sessionId, positionTicks, isPaused);
            
            lock (_sessionLock)
            {
                if (_streamProgress.TryGetValue(sessionId, out progress))
                {
                    progress.LastReportedPosition = positionTicks;
                    progress.LastReportedTime = now;
                    progress.ReportCount++;
                }
            }

            _logger.LogInformation("PROGRESS REPORT #{Count}: Session {SessionId} at {Position}ms (Paused: {IsPaused})", 
                progress?.ReportCount ?? 0, sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks), isPaused);
        }
        else
        {
            _logger.LogTrace("Skipped report for session {SessionId} - too soon since last report", sessionId);
        }
    }

    // MARK: CleanupSession
    private async Task CleanupSession(string sessionId, Guid itemId)
    {
        try
        {
            var finalPosition = 0L;
            if (_streamProgress.TryGetValue(sessionId, out var progress))
            {
                finalPosition = progress.CurrentTicks;
            }

            lock (_sessionLock)
            {
                _itemSessions.TryRemove(itemId, out _);
                _streamProgress.TryRemove(sessionId, out _);
            }

            await _playbackReportingService.StopPlaybackAsync(sessionId, finalPosition, false);
            
            _logger.LogInformation("SESSION CLEANUP: {SessionId} stopped at {Position}ms", 
                sessionId, TimeConversionUtil.TicksToMilliseconds(finalPosition));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up session {SessionId}", sessionId);
        }
    }

    // MARK: StreamMediaAsync
    private async Task StreamMediaAsync(HttpListenerContext context, StreamInfo streamInfo, string sessionId, long? rangeStart)
    {
        HttpClient? httpClient = null;
        Stream? sourceStream = null;
        bool sessionCleaned = false;
        var itemId = GetItemIdFromSession(sessionId);

        try
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, streamInfo.Url);
            if (rangeStart.HasValue)
            {
                requestMessage.Headers.Add("Range", $"bytes={rangeStart}-");
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            // Set response headers
            context.Response.ContentType = streamInfo.MimeType;
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.PartialContent ? 206 : 200;
            
            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            // Forward content headers
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

            sourceStream = await response.Content.ReadAsStreamAsync();
            await CopyStreamWithProgressAsync(sourceStream, context.Response.OutputStream, streamInfo, sessionId);

            // Normal completion
            if (itemId.HasValue)
            {
                await CleanupSession(sessionId, itemId.Value);
                sessionCleaned = true;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is HttpListenerException)
        {
            if (!sessionCleaned)
            {
                // Check if this looks like a real pause vs a quick disconnect
                if (_streamProgress.TryGetValue(sessionId, out var progress))
                {
                    var streamDuration = DateTime.UtcNow - progress.StartTime;
                    var hasStreamedSignificantData = progress.TotalBytesStreamed > 5 * 1024 * 1024; // 5MB
                    var hasPlayedForAwhile = streamDuration.TotalSeconds > 30; // 30 seconds
                    var notAtEnd = progress.CurrentTicks < streamInfo.DurationTicks * 0.95;

                    if (hasStreamedSignificantData && hasPlayedForAwhile && notAtEnd)
                    {
                        _logger.LogInformation("PAUSE DETECTED: Session {SessionId} at {Position}ms (streamed {MB}MB for {Seconds}s)", 
                            sessionId, TimeConversionUtil.TicksToMilliseconds(progress.CurrentTicks),
                            progress.TotalBytesStreamed / 1024.0 / 1024.0, streamDuration.TotalSeconds);
                        
                        // Report pause but don't cleanup session - it might resume
                        await _playbackReportingService.PausePlaybackAsync(sessionId, progress.CurrentTicks);
                    }
                    else
                    {
                        _logger.LogInformation("STREAM ENDED: Session {SessionId} (quick disconnect - {MB}MB, {Seconds}s)", 
                            sessionId, progress.TotalBytesStreamed / 1024.0 / 1024.0, streamDuration.TotalSeconds);
                        
                        if (itemId.HasValue)
                        {
                            await CleanupSession(sessionId, itemId.Value);
                        }
                    }
                }
                sessionCleaned = true;
            }
        }
        finally
        {
            sourceStream?.Dispose();
            httpClient?.Dispose();
            try { context.Response.Close(); } catch { }
        }
    }

    // MARK: CopyStreamWithProgressAsync
    private async Task CopyStreamWithProgressAsync(Stream source, Stream destination, StreamInfo streamInfo, string sessionId)
    {
        var buffer = new byte[131072]; // 128KB buffer
        long totalBytes = 0;
        var lastProgressReport = DateTime.UtcNow;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();

                totalBytes += bytesRead;
                var now = DateTime.UtcNow;

                // Update activity tracking
                if (_streamProgress.TryGetValue(sessionId, out var progress))
                {
                    lock (_sessionLock)
                    {
                        progress.TotalBytesStreamed = totalBytes;
                        progress.LastUpdateTime = now;
                    }

                    // Report progress every 15 seconds during active streaming
                    // This keeps Jellyfin updated that we're still playing
                    if ((now - lastProgressReport).TotalSeconds >= 15.0)
                    {
                        // Use the CURRENT position (don't advance it artificially)
                        await ReportPositionChange(sessionId, progress.CurrentTicks, isPaused: false);
                        lastProgressReport = now;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            _logger.LogInformation("Stream copy interrupted after {MB:F1} MB", totalBytes / 1024.0 / 1024.0);
            throw;
        }
    }

    // MARK: Helper Methods
    private Guid? GetItemIdFromSession(string sessionId)
    {
        lock (_sessionLock)
        {
            return _itemSessions.Values.FirstOrDefault(s => s.SessionId == sessionId)?.ItemId;
        }
    }

    private long? ParseRangeHeader(string? rangeHeader)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=")) return null;

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

    private long CalculateSeekPosition(long byteOffset, long totalDurationTicks, long resumePositionTicks)
    {
        if (byteOffset < 1024 * 1024) return resumePositionTicks;

        var estimatedBitrate = 8000000; // 8Mbps
        var estimatedTotalBytes = TimeConversionUtil.TicksToSeconds(totalDurationTicks) * estimatedBitrate / 8;
        var offsetPercentage = Math.Min(1.0, byteOffset / estimatedTotalBytes);
        var absoluteSeekPosition = (long)(totalDurationTicks * offsetPercentage);

        return Math.Max(0, Math.Min(absoluteSeekPosition, totalDurationTicks));
    }

    private async Task<long> GetResumePositionAsync(Guid itemId)
    {
        try
        {
            var userId = _configuration["Jellyfin:UserId"];
            if (string.IsNullOrEmpty(userId)) return 0;

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
                    positionElement.TryGetInt64(out var positionTicks) &&
                    positionTicks > TimeConversionUtil.MinutesToTicks(2))
                {
                    if (userData.TryGetProperty("Played", out var playedElement) && !playedElement.GetBoolean())
                    {
                        return positionTicks;
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

    private StreamRequest ParseStreamRequest(HttpListenerContext context)
    {
        return new StreamRequest
        {
            UserAgent = context.Request.UserAgent ?? "",
            AcceptRanges = context.Request.Headers["Range"]
        };
    }

    private async Task<StreamInfo?> GetOptimalStreamAsync(Jellyfin.Sdk.Generated.Models.BaseItemDto item, StreamRequest request)
    {
        if (!item.Id.HasValue) return null;

        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
            return null;

        var streamParams = new List<string>
        {
            $"Static=true",
            $"MediaSourceId={item.Id.Value}",
            $"X-Emby-Token={accessToken}"
        };

        if (request.StartTimeTicks.HasValue && request.StartTimeTicks.Value > 0)
        {
            streamParams.Add($"StartTimeTicks={request.StartTimeTicks.Value}");
        }

        var streamUrl = $"{serverUrl}/Videos/{item.Id.Value}/stream?" + string.Join("&", streamParams);

        return new StreamInfo
        {
            Url = streamUrl,
            MimeType = "video/mp4",
            IsDirectPlay = true,
            DurationTicks = item.RunTimeTicks ?? 0
        };
    }

    private Task SendErrorResponse(HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        try
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "text/plain";
            var buffer = System.Text.Encoding.UTF8.GetBytes(message);
            context.Response.OutputStream.Write(buffer);
            context.Response.Close();
        }
        catch { }
        
        return Task.CompletedTask;
    }
}