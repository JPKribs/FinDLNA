using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using FinDLNA.Utilities;
using FinDLNA.Models;
using System.Collections.Concurrent;

namespace FinDLNA.Services;

// MARK: PlaybackReportingService
public class PlaybackReportingService
{
    private readonly ILogger<PlaybackReportingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, PlaybackSession> _activeSessions = new();
    private readonly ConcurrentDictionary<string, bool> _stopInProgress = new();

    public PlaybackReportingService(
        ILogger<PlaybackReportingService> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    private bool IsConfigured => !string.IsNullOrEmpty(_configuration["Jellyfin:AccessToken"]) &&
                               !string.IsNullOrEmpty(_configuration["Jellyfin:ServerUrl"]);

    // MARK: StartPlaybackAsync
    public async Task<string?> StartPlaybackAsync(Guid itemId, string? userAgent = null, string? clientEndpoint = null, long? startPositionTicks = null)
    {
        if (!IsConfigured) return null;

        try
        {
            var sessionId = Guid.NewGuid().ToString();
            var userId = _configuration["Jellyfin:UserId"];
            var accessToken = _configuration["Jellyfin:AccessToken"];
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');

            if (string.IsNullOrEmpty(userId)) return null;

            var actualStartPosition = startPositionTicks ?? 0L;
            
            var playbackStartInfo = new
            {
                UserId = userId,
                ItemId = itemId,
                SessionId = sessionId,
                MediaSourceId = itemId.ToString(),
                CanSeek = true,
                IsMuted = false,
                IsPaused = false,
                RepeatMode = "RepeatNone",
                MaxStreamingBitrate = 120000000,
                StartTimeTicks = actualStartPosition,
                PositionTicks = actualStartPosition,
                VolumeLevel = 100,
                PlayMethod = "DirectPlay",
                PlaySessionId = sessionId,
                EventName = "playbackstart"
            };

            var json = JsonSerializer.Serialize(playbackStartInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var session = new PlaybackSession
                {
                    SessionId = sessionId,
                    ItemId = itemId,
                    UserId = userId,
                    StartTime = DateTimeOffset.UtcNow,
                    UserAgent = userAgent ?? "Unknown",
                    ClientEndpoint = clientEndpoint ?? "Unknown",
                    LastProgressUpdate = DateTimeOffset.UtcNow,
                    LastPositionTicks = actualStartPosition
                };

                _activeSessions[sessionId] = session;

                _logger.LogInformation("PLAYBACK STARTED: Session {SessionId} for item {ItemId} at {Position}ms", 
                    sessionId, itemId, TimeConversionUtil.TicksToMilliseconds(actualStartPosition));

                return sessionId;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to start playback session: {StatusCode} - {Content}", 
                    response.StatusCode, errorContent);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting playback session for item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: UpdatePlaybackProgressAsync
    public async Task UpdatePlaybackProgressAsync(string sessionId, long positionTicks, bool isPaused = false)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogTrace("Session {SessionId} not found for progress update", sessionId);
            return;
        }

        if (!IsConfigured) return;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            var progressInfo = new
            {
                UserId = session.UserId,
                ItemId = session.ItemId,
                SessionId = sessionId,
                MediaSourceId = session.ItemId.ToString(),
                PositionTicks = positionTicks,
                IsPaused = isPaused,
                IsMuted = false,
                VolumeLevel = 100,
                CanSeek = true,
                RepeatMode = "RepeatNone",
                PlayMethod = "DirectPlay",
                PlaySessionId = sessionId,
                EventName = isPaused ? "pause" : "timeupdate"
            };

            var json = JsonSerializer.Serialize(progressInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing/Progress";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                // Update session data
                session.LastProgressUpdate = DateTimeOffset.UtcNow;
                session.LastPositionTicks = positionTicks;
                session.IsPaused = isPaused;

                _logger.LogDebug("PROGRESS REPORTED: Session {SessionId} at {Position}ms (Paused: {IsPaused})", 
                    sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks), isPaused);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to update playback progress: {StatusCode} - {Content}", 
                    response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating playback progress for session {SessionId}", sessionId);
        }
    }

    // MARK: PausePlaybackAsync
    public async Task PausePlaybackAsync(string sessionId, long positionTicks)
    {
        await UpdatePlaybackProgressAsync(sessionId, positionTicks, isPaused: true);
        _logger.LogInformation("PAUSED: Session {SessionId} at {Position}ms", 
            sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
    }

    // MARK: ResumePlaybackAsync
    public async Task ResumePlaybackAsync(string sessionId, long positionTicks)
    {
        await UpdatePlaybackProgressAsync(sessionId, positionTicks, isPaused: false);
        _logger.LogInformation("RESUMED: Session {SessionId} at {Position}ms", 
            sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
    }

    // MARK: StopPlaybackAsync
    public async Task StopPlaybackAsync(string sessionId, long? finalPositionTicks = null, bool markAsWatched = false)
    {
        // Prevent duplicate stop calls
        if (!_stopInProgress.TryAdd(sessionId, true))
        {
            _logger.LogTrace("Stop already in progress for session {SessionId}", sessionId);
            return;
        }

        try
        {
            if (!_activeSessions.TryRemove(sessionId, out var session))
            {
                _logger.LogTrace("Session {SessionId} not found for stop", sessionId);
                return;
            }

            if (!IsConfigured) return;

            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];
            var positionTicks = finalPositionTicks ?? session.LastPositionTicks;

            var stopInfo = new
            {
                UserId = session.UserId,
                ItemId = session.ItemId,
                SessionId = sessionId,
                MediaSourceId = session.ItemId.ToString(),
                PositionTicks = positionTicks,
                PlaySessionId = sessionId,
                Failed = false,
                PlayMethod = "DirectPlay"
            };

            var json = JsonSerializer.Serialize(stopInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing/Stopped";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var playedDuration = DateTimeOffset.UtcNow - session.StartTime;
                _logger.LogInformation("PLAYBACK STOPPED: Session {SessionId} for item {ItemId} - Duration: {Duration}, Final Position: {Position}ms", 
                    sessionId, session.ItemId, playedDuration, TimeConversionUtil.TicksToMilliseconds(positionTicks));

                if (markAsWatched)
                {
                    await MarkAsWatchedAsync(session.ItemId, session.UserId);
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to stop playback session: {StatusCode} - {Content}", 
                    response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping playback session {SessionId}", sessionId);
        }
        finally
        {
            _stopInProgress.TryRemove(sessionId, out _);
        }
    }

    // MARK: MarkAsWatchedAsync
    public async Task MarkAsWatchedAsync(Guid itemId, string userId)
    {
        try
        {
            if (!IsConfigured) return;

            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            var url = $"{serverUrl}/Users/{userId}/PlayedItems/{itemId}";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Emby-Token", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Marked item {ItemId} as watched for user {UserId}", itemId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking item {ItemId} as watched", itemId);
        }
    }

    // MARK: GetActiveSessionsAsync
    public Task<IReadOnlyDictionary<string, PlaybackSession>> GetActiveSessionsAsync()
    {
        return Task.FromResult<IReadOnlyDictionary<string, PlaybackSession>>(_activeSessions);
    }

    // MARK: CleanupStaleSessionsAsync
    public async Task CleanupStaleSessionsAsync()
    {
        var staleThreshold = TimeSpan.FromHours(2);
        var now = DateTimeOffset.UtcNow;
        var staleSessions = new List<string>();

        foreach (var kvp in _activeSessions)
        {
            var session = kvp.Value;
            var timeSinceUpdate = now - session.LastProgressUpdate;
            
            if (timeSinceUpdate > staleThreshold)
            {
                staleSessions.Add(kvp.Key);
            }
        }

        foreach (var sessionId in staleSessions)
        {
            _logger.LogInformation("Cleaning up stale session {SessionId}", sessionId);
            await StopPlaybackAsync(sessionId, markAsWatched: false);
        }
    }

    public bool SessionExists(string sessionId) => _activeSessions.ContainsKey(sessionId);
    
    public Task<PlaybackSession?> GetSessionAsync(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }
}