using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using Jellyfin.Sdk.Generated.Models;
using FinDLNA.Utilities;
using FinDLNA.Models;

namespace FinDLNA.Services;

// MARK: PlaybackReportingService
public class PlaybackReportingService
{
    private readonly ILogger<PlaybackReportingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, PlaybackSession> _activeSessions = new();

    public PlaybackReportingService(
        ILogger<PlaybackReportingService> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    // MARK: IsConfigured
    private bool IsConfigured => !string.IsNullOrEmpty(_configuration["Jellyfin:AccessToken"]) &&
                               !string.IsNullOrEmpty(_configuration["Jellyfin:ServerUrl"]);

    // MARK: StartPlaybackAsync
    public async Task<string?> StartPlaybackAsync(Guid itemId, string? userAgent = null, string? clientEndpoint = null, long? startPositionTicks = null)
    {
        try
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("Jellyfin not configured for playback reporting");
                return null;
            }

            var sessionId = Guid.NewGuid().ToString();
            var userId = _configuration["Jellyfin:UserId"];
            var accessToken = _configuration["Jellyfin:AccessToken"];
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("Missing Jellyfin UserId for playback reporting");
                return null;
            }

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
                PositionTicks = actualStartPosition, // MARK: Also set current position
                VolumeLevel = 100,
                Brightness = 100,
                AspectRatio = "16:9",
                PlayMethod = startPositionTicks.HasValue ? "Transcode" : "DirectPlay",
                PlaySessionId = sessionId,
                PlaylistItemId = sessionId,
                MediaStreams = new object[0],
                PlaybackStartTimeTicks = DateTimeOffset.UtcNow.Ticks,
                SubtitleStreamIndex = (int?)null,
                AudioStreamIndex = (int?)null,
                LiveStreamId = (string?)null,
                EventName = "playbackstart"
            };

            var json = JsonSerializer.Serialize(playbackStartInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            _logger.LogDebug("PLAYBACK START REQUEST: {Json}", json);

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

                _logger.LogInformation("PLAYBACK STARTED: Session {SessionId} for item {ItemId} at position {Position}ms from {Client}", 
                    sessionId, itemId, TimeConversionUtil.TicksToMilliseconds(actualStartPosition), clientEndpoint);

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
        try
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogDebug("Session {SessionId} not found for progress update", sessionId);
                return;
            }

            if (!IsConfigured)
            {
                _logger.LogWarning("Jellyfin not configured for progress update");
                return;
            }

            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            var progressInfo = new
            {
                UserId = session.UserId,
                ItemId = session.ItemId,
                SessionId = session.SessionId,
                MediaSourceId = session.ItemId.ToString(),
                PositionTicks = positionTicks,
                IsPaused = isPaused,
                IsMuted = false,
                VolumeLevel = 100,
                CanSeek = true,
                RepeatMode = "RepeatNone",
                PlayMethod = "DirectPlay",
                PlaySessionId = session.SessionId,
                PlaylistItemId = session.SessionId,
                EventName = isPaused ? "pause" : "timeupdate",
                LiveStreamId = (string?)null
            };

            var json = JsonSerializer.Serialize(progressInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing/Progress";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            _logger.LogDebug("PROGRESS UPDATE: {Json}", json);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                session.LastProgressUpdate = DateTimeOffset.UtcNow;
                session.LastPositionTicks = positionTicks;
                session.IsPaused = isPaused;

                _logger.LogDebug("PROGRESS UPDATED: Session {SessionId} at {Position}ms, paused: {IsPaused}", 
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
        
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.IsPaused = true;
            session.PauseTime = DateTimeOffset.UtcNow;
            _logger.LogInformation("Paused playback session {SessionId} at position {Position}ms", 
                sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
        }
    }

    // MARK: ResumePlaybackAsync
    public async Task ResumePlaybackAsync(string sessionId, long positionTicks)
    {
        await UpdatePlaybackProgressAsync(sessionId, positionTicks, isPaused: false);
        
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.IsPaused = false;
            session.PauseTime = null;
            _logger.LogInformation("Resumed playback session {SessionId} from position {Position}ms", 
                sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
        }
    }

    // MARK: StopPlaybackAsync
    public async Task StopPlaybackAsync(string sessionId, long? finalPositionTicks = null, bool markAsWatched = false)
    {
        try
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("Session {SessionId} not found for stop", sessionId);
                return;
            }

            if (!IsConfigured)
            {
                _logger.LogWarning("Jellyfin not configured for stop playback");
                return;
            }

            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            var positionTicks = finalPositionTicks ?? session.LastPositionTicks;
            var playedDuration = DateTimeOffset.UtcNow - session.StartTime;

            var stopInfo = new
            {
                UserId = session.UserId,
                ItemId = session.ItemId,
                SessionId = session.SessionId,
                MediaSourceId = session.ItemId.ToString(),
                PositionTicks = positionTicks,
                PlaySessionId = session.SessionId,
                PlaylistItemId = session.SessionId,
                Failed = false,
                NextMediaType = "Unknown",
                PlayMethod = "DirectPlay",
                LiveStreamId = (string?)null
            };

            var json = JsonSerializer.Serialize(stopInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{serverUrl}/Sessions/Playing/Stopped";
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Emby-Token", accessToken);

            _logger.LogDebug("PLAYBACK STOP REQUEST: {Json}", json);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("PLAYBACK STOPPED: Session {SessionId} for item {ItemId} - Duration: {Duration}, Final Position: {Position}ms, Client: {Client}", 
                    sessionId, session.ItemId, playedDuration, TimeConversionUtil.TicksToMilliseconds(positionTicks), session.ClientEndpoint);

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

            _activeSessions.Remove(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping playback session {SessionId}", sessionId);
        }
    }

    // MARK: MarkAsWatchedAsync
    public async Task MarkAsWatchedAsync(Guid itemId, string userId)
    {
        try
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("Jellyfin not configured for mark as watched");
                return;
            }

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
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to mark item as watched: {StatusCode} - {Content}", 
                    response.StatusCode, errorContent);
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
        var staleThreshold = TimeSpan.FromMinutes(15); // Increased for better long movie support
        var pausedThreshold = TimeSpan.FromHours(2);   // Allow 2 hour paused sessions for long movies
        var now = DateTimeOffset.UtcNow;
        var staleSessions = new List<string>();

        foreach (var kvp in _activeSessions)
        {
            var session = kvp.Value;
            var timeSinceUpdate = now - session.LastProgressUpdate;
            
            // If session is paused, use much longer threshold for long movies
            var threshold = session.IsPaused ? pausedThreshold : staleThreshold;
            
            if (timeSinceUpdate > threshold)
            {
                staleSessions.Add(kvp.Key);
                _logger.LogInformation("Session {SessionId} stale for {Duration} (paused: {IsPaused})", 
                    kvp.Key, timeSinceUpdate, session.IsPaused);
            }
        }

        foreach (var sessionId in staleSessions)
        {
            _logger.LogInformation("Cleaning up stale session {SessionId}", sessionId);
            await StopPlaybackAsync(sessionId, markAsWatched: false);
        }
    }

    // MARK: GetSessionByItemAsync
    public Task<PlaybackSession?> GetSessionByItemAsync(Guid itemId)
    {
        var session = _activeSessions.Values.FirstOrDefault(s => s.ItemId == itemId);
        return Task.FromResult(session);
    }
}