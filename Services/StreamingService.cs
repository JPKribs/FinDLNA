using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FinDLNA.Models;
using FinDLNA.Utilities;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FinDLNA.Services;

public enum TranscodeReason
{
    DirectPlay,
    ContainerNotSupported,
    VideoCodecNotSupported,
    AudioCodecNotSupported,
    VideoLevelNotSupported,
    VideoBitrateNotSupported,
    AudioBitrateNotSupported,
    AudioChannelsNotSupported,
    VideoResolutionNotSupported,
    InterlacedVideoNotSupported,
    SecondaryAudioNotSupported,
    VideoFramerateNotSupported,
    Unknown
}

public class StreamDecision
{
    public bool IsDirectPlay { get; set; }
    public string? StreamUrl { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public long EstimatedBitrate { get; set; }
    public TranscodeReason Reason { get; set; }
    public string? MimeType { get; set; }
}

// MARK: Enhanced StreamingService
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

            _logger.LogInformation("STREAM REQUEST: Item {ItemId} from {UserAgent} at {EndPoint}", 
                itemId, userAgent, clientEndpoint);

            var item = await _jellyfinService.GetItemAsync(guid);
            if (item == null)
            {
                _logger.LogWarning("Item {ItemId} not found", itemId);
                await SendErrorResponse(context, HttpStatusCode.NotFound, "Item not found");
                return;
            }

            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent);
            if (deviceProfile == null)
            {
                _logger.LogWarning("No device profile found for {UserAgent}", userAgent);
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Device not supported");
                return;
            }

            _logger.LogInformation("Using device profile: {ProfileName} for {UserAgent}", 
                deviceProfile.Name, userAgent);

            var resumePosition = await GetResumePositionAsync(guid);
            var rangeRequest = ParseRangeHeader(context.Request.Headers["Range"]);

            sessionId = await GetOrCreateSessionAsync(guid, userAgent, clientEndpoint, resumePosition, rangeRequest, item.RunTimeTicks ?? 0);

            if (sessionId == null)
            {
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Session creation failed");
                return;
            }

            var streamRequest = CreateStreamRequest(context, deviceProfile, rangeRequest, resumePosition);
            var streamDecision = await MakeStreamingDecision(item, deviceProfile, streamRequest);

            if (streamDecision?.StreamUrl == null)
            {
                _logger.LogError("No stream URL available for item {ItemId}", itemId);
                await SendErrorResponse(context, HttpStatusCode.InternalServerError, "Stream unavailable");
                return;
            }

            _logger.LogInformation("STREAM DECISION: {Decision} for item {ItemId} - Reason: {Reason}", 
                streamDecision.IsDirectPlay ? "DirectPlay" : "Transcode", itemId, streamDecision.Reason);

            await StreamMediaAsync(context, streamDecision, sessionId, rangeRequest, item.RunTimeTicks ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stream request for item {ItemId}", itemId);
            if (sessionId != null && itemGuid.HasValue)
            {
                await CleanupSession(sessionId, itemGuid.Value);
            }
            if (!context.Response.OutputStream.CanWrite) return;
            await SendErrorResponse(context, HttpStatusCode.InternalServerError, "An unexpected error occurred during streaming.");
        }
    }

    // MARK: MakeStreamingDecision
    private async Task<StreamDecision?> MakeStreamingDecision(BaseItemDto item, DeviceProfile deviceProfile, StreamRequest request)
    {
        if (!item.Id.HasValue)
        {
            _logger.LogWarning("Item has no ID for streaming decision");
            return null;
        }

        var mediaSource = item.MediaSources?.FirstOrDefault();
        if (mediaSource == null)
        {
            _logger.LogWarning("No media source found for item {ItemId}", item.Id.Value);
            return null;
        }
        
        var mediaType = item.Type == BaseItemDto_Type.Audio ? "Audio" : "Video";
        var videoStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
        var audioStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);

        var decision = new StreamDecision
        {
            Container = mediaSource.Container,
            VideoCodec = videoStream?.Codec,
            AudioCodec = audioStream?.Codec,
            EstimatedBitrate = mediaSource.Bitrate ?? 20000000 
        };

        var transcodeReason = AnalyzeTranscodeRequirements(deviceProfile, mediaSource, videoStream, audioStream);
        decision.Reason = transcodeReason;
        decision.IsDirectPlay = transcodeReason == TranscodeReason.DirectPlay;

        if (decision.IsDirectPlay)
        {
            decision.StreamUrl = GetDirectPlayUrl(item.Id.Value, request.StartTimeTicks);
            decision.MimeType = GetMimeTypeFromContainer(decision.Container);
            _logger.LogInformation("DirectPlay decision for {ItemId}: Container={Container}, Video={VideoCodec}, Audio={AudioCodec}", 
                item.Id.Value, decision.Container, decision.VideoCodec, decision.AudioCodec);
        }
        else
        {
            var transcodingProfile = await _deviceProfileService.GetTranscodingProfileAsync(deviceProfile, mediaType);
            
            if (transcodingProfile != null)
            {
                long targetBitrate = Math.Min(decision.EstimatedBitrate, deviceProfile.MaxStreamingBitrate ?? 120000000);
                
                decision.StreamUrl = GetTranscodeUrl(item.Id.Value, transcodingProfile, request.StartTimeTicks, targetBitrate);
                decision.Container = transcodingProfile.Container;
                decision.VideoCodec = transcodingProfile.VideoCodec;
                decision.AudioCodec = transcodingProfile.AudioCodec;
                decision.MimeType = GetMimeTypeFromContainer(transcodingProfile.Container);
                decision.EstimatedBitrate = targetBitrate;
                
                _logger.LogInformation("Transcode decision for {ItemId}: Reason={Reason} -> Container={Container}, Video={VideoCodec}, Audio={AudioCodec} at {Bitrate}bps", 
                    item.Id.Value, transcodeReason, decision.Container, decision.VideoCodec, decision.AudioCodec, decision.EstimatedBitrate);
            }
            else
            {
                _logger.LogError("No transcoding profile found for device {DeviceName} and media type {MediaType}", 
                    deviceProfile.Name, mediaType);
                return null;
            }
        }

        return decision;
    }

    // MARK: AnalyzeTranscodeRequirements
    private TranscodeReason AnalyzeTranscodeRequirements(DeviceProfile deviceProfile, 
        MediaSourceInfo mediaSource, MediaStream? videoStream, MediaStream? audioStream)
    {
        var mediaType = videoStream != null ? "Video" : "Audio";

        if (!IsContainerSupported(deviceProfile, mediaSource.Container, mediaType))
            return TranscodeReason.ContainerNotSupported;
        
        if (videoStream != null && !IsVideoCodecSupported(deviceProfile, videoStream.Codec))
            return TranscodeReason.VideoCodecNotSupported;
            
        if (audioStream != null && !IsAudioCodecSupported(deviceProfile, audioStream.Codec, mediaType))
            return TranscodeReason.AudioCodecNotSupported;

        if (videoStream != null)
        {
            if (videoStream.BitRate.HasValue && deviceProfile.MaxStreamingBitrate.HasValue && videoStream.BitRate.Value > deviceProfile.MaxStreamingBitrate.Value)
                return TranscodeReason.VideoBitrateNotSupported;

            // CS0119 Fix: Compare condition to an enum member (ProfileConditionType.IsInterlaced), not the type itself.
            if (videoStream.IsInterlaced == true && !(deviceProfile.CodecProfiles?.Any(p => p.Type == CodecProfile_Type.Video && p.Conditions?.Any(c => c.Property == ProfileCondition_Property.IsInterlaced && c.Value == "true") == true) ?? false))
                return TranscodeReason.InterlacedVideoNotSupported;
        }
        
        if (audioStream != null)
        {
            // CS1061 Fix: The 'MaxAudioChannels' property might not exist on your custom DeviceProfile model.
            // This logic provides a fallback. For best results, add 'public int? MaxAudioChannels { get; set; }'
            // to your FinDLNA.Models.DeviceProfile class.
            var maxChannels = 6; // Fallback to 6 channels (5.1 surround)
            if (audioStream.Channels.HasValue && audioStream.Channels.Value > maxChannels)
            {
                if(!IsAudioCodecSupported(deviceProfile, audioStream.Codec, mediaType, true))
                {
                   return TranscodeReason.AudioChannelsNotSupported;
                }
            }
        }
        
        return TranscodeReason.DirectPlay;
    }

    // MARK: IsContainerSupported
    private bool IsContainerSupported(DeviceProfile profile, string? container, string mediaType)
    {
        if (string.IsNullOrEmpty(container)) return false;

        var profileType = mediaType.Equals("Video", StringComparison.OrdinalIgnoreCase) 
            ? DirectPlayProfile_Type.Video 
            : DirectPlayProfile_Type.Audio;

        // CS8602 Fix: Changed `dp.Type.ToString().Equals(...)` to a direct, null-safe comparison `dp.Type == profileType`.
        return profile.DirectPlayProfiles?.Any(dp => 
            dp.Type == profileType &&
            (string.IsNullOrEmpty(dp.Container) || 
             dp.Container.Split(',').Any(c => c.Trim().Equals(container, StringComparison.OrdinalIgnoreCase)))) == true;
    }

    // MARK: IsVideoCodecSupported
    private bool IsVideoCodecSupported(DeviceProfile profile, string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return false;

        return profile.DirectPlayProfiles?.Any(dp => 
            dp.Type == DirectPlayProfile_Type.Video &&
            (string.IsNullOrEmpty(dp.VideoCodec) || 
             dp.VideoCodec.Split(',').Any(c => c.Trim().Equals(codec, StringComparison.OrdinalIgnoreCase)))) == true;
    }

    // MARK: IsAudioCodecSupported
    private bool IsAudioCodecSupported(DeviceProfile profile, string? codec, string mediaType, bool checkDownmix = false)
    {
        if (string.IsNullOrEmpty(codec)) return false;
        
        var profileType = mediaType.Equals("Video", StringComparison.OrdinalIgnoreCase) 
            ? DirectPlayProfile_Type.Video
            : DirectPlayProfile_Type.Audio;

        if (checkDownmix)
        {
             return profile.TranscodingProfiles?.Any(tp => 
                (string.IsNullOrEmpty(tp.AudioCodec) || 
                tp.AudioCodec.Split(',').Any(c => c.Trim().Equals(codec, StringComparison.OrdinalIgnoreCase)))) == true;
        }
        
        // CS8602 Fix: Also applied null-safe check here.
        return profile.DirectPlayProfiles?.Any(dp => 
            dp.Type == profileType &&
            (string.IsNullOrEmpty(dp.AudioCodec) || 
             dp.AudioCodec.Split(',').Any(c => c.Trim().Equals(codec, StringComparison.OrdinalIgnoreCase)))) == true;
    }

    // MARK: GetDirectPlayUrl
    private string GetDirectPlayUrl(Guid itemId, long? startTimeTicks)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        var mediaSourceId = GetMediaSourceId(itemId); 

        var queryParams = new List<string>
        {
            $"Static=true",
            $"MediaSourceId={mediaSourceId}",
            $"api_key={accessToken}"
        };

        if (startTimeTicks.HasValue && startTimeTicks.Value > 0)
        {
            queryParams.Add($"StartTimeTicks={startTimeTicks.Value}");
        }

        var queryString = string.Join("&", queryParams);
        return $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
    }

    // MARK: GetTranscodeUrl
    private string GetTranscodeUrl(Guid itemId, TranscodingProfile profile, long? startTimeTicks, long maxBitrate)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        var userId = _configuration["Jellyfin:UserId"];
        var mediaSourceId = GetMediaSourceId(itemId);

        var queryParams = new List<string>
        {
            $"UserId={userId}",
            $"MediaSourceId={mediaSourceId}",
            $"Container={profile.Container}",
            $"VideoCodec={profile.VideoCodec ?? "h264"}",
            $"AudioCodec={profile.AudioCodec ?? "aac"}",
            $"AudioStreamIndex=1",
            $"VideoBitrate={maxBitrate}",
            $"AudioBitrate={Math.Min(384000, maxBitrate)}",
            $"api_key={accessToken}"
        };

        if (startTimeTicks.HasValue && startTimeTicks.Value > 0)
        {
            queryParams.Add($"StartTimeTicks={startTimeTicks.Value}");
        }

        var queryString = string.Join("&", queryParams);
        return $"{serverUrl}/Videos/{itemId}/stream.{profile.Container}?{queryString}";
    }

    // MARK: GetMimeTypeFromContainer
    private string GetMimeTypeFromContainer(string? container)
    {
        return container?.ToLowerInvariant() switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "ts" => "video/mp2t",
            "webm" => "video/webm",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "ogg" => "audio/ogg",
            _ => "video/mp4"
        };
    }

    // MARK: CreateStreamRequest
    private StreamRequest CreateStreamRequest(HttpListenerContext context, DeviceProfile deviceProfile, long? rangeStart, long resumePosition)
    {
        return new StreamRequest
        {
            UserAgent = context.Request.UserAgent ?? "",
            AcceptRanges = context.Request.Headers["Range"],
            DeviceProfile = deviceProfile,
            StartTimeTicks = rangeStart.HasValue && rangeStart > 1024 * 1024 ? 
                CalculateSeekPosition(rangeStart.Value, resumePosition) : resumePosition
        };
    }

    // MARK: GetOrCreateSessionAsync
    private async Task<string?> GetOrCreateSessionAsync(Guid itemId, string userAgent, string clientEndpoint, long resumePosition, long? rangeRequest, long totalDuration)
    {
        if (_itemSessions.TryGetValue(itemId, out var existingSession))
        {
            if (_streamProgress.TryGetValue(existingSession.SessionId, out var existingProgress))
            {
                var timeSinceLastActivity = DateTime.UtcNow - existingProgress.LastUpdateTime;
                
                if (timeSinceLastActivity.TotalSeconds < 30)
                {
                    _logger.LogInformation("REUSING SESSION: {SessionId} for item {ItemId}", 
                        existingSession.SessionId, itemId);
                    return existingSession.SessionId;
                }
            }
        }

        if (_itemSessions.TryGetValue(itemId, out var oldSession))
        {
            await CleanupSession(oldSession.SessionId, itemId);
        }

        var startPosition = resumePosition;
        if (rangeRequest.HasValue && totalDuration > 0)
        {
            var calculatedSeek = CalculateSeekPosition(rangeRequest.Value, resumePosition);
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

    // MARK: StreamMediaAsync
    private async Task StreamMediaAsync(HttpListenerContext context, StreamDecision decision, string sessionId, long? rangeStart, long totalDurationTicks)
    {
        HttpClient? httpClient = null;
        Stream? sourceStream = null;
        bool sessionCleaned = false;
        var itemId = GetItemIdFromSession(sessionId);

        try
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, decision.StreamUrl);
            if (rangeStart.HasValue)
            {
                requestMessage.Headers.Add("Range", $"bytes={rangeStart}-");
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Stream request failed: {StatusCode} from {Url}", response.StatusCode, decision.StreamUrl);
                await SendErrorResponse(context, response.StatusCode, "Stream unavailable");
                return;
            }

            context.Response.ContentType = decision.MimeType ?? "video/mp4";
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.PartialContent ? 206 : 200;
            
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

            _logger.LogInformation("STREAMING: {Decision} stream for session {SessionId} - {MimeType}", 
                decision.IsDirectPlay ? "DirectPlay" : "Transcode", sessionId, decision.MimeType);

            sourceStream = await response.Content.ReadAsStreamAsync();
            await CopyStreamWithProgressAsync(sourceStream, context.Response.OutputStream, sessionId, totalDurationTicks);

            if (itemId.HasValue)
            {
                await CleanupSession(sessionId, itemId.Value);
                sessionCleaned = true;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is HttpListenerException)
        {
            if (!sessionCleaned && _streamProgress.TryGetValue(sessionId, out var progress))
            {
                var streamDuration = DateTime.UtcNow - progress.StartTime;
                var hasStreamedSignificantData = progress.TotalBytesStreamed > 5 * 1024 * 1024;
                var hasPlayedForAwhile = streamDuration.TotalSeconds > 30;

                if (hasStreamedSignificantData && hasPlayedForAwhile)
                {
                    _logger.LogInformation("PAUSE DETECTED: Session {SessionId} at {Position}ms", 
                        sessionId, TimeConversionUtil.TicksToMilliseconds(progress.CurrentTicks));
                    
                    await _playbackReportingService.PausePlaybackAsync(sessionId, progress.CurrentTicks);
                }
                else if (itemId.HasValue)
                {
                    await CleanupSession(sessionId, itemId.Value);
                }
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
    private async Task CopyStreamWithProgressAsync(Stream source, Stream destination, string sessionId, long totalDurationTicks)
    {
        var buffer = new byte[131072];
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

                if (_streamProgress.TryGetValue(sessionId, out var progress))
                {
                    lock (_sessionLock)
                    {
                        progress.TotalBytesStreamed = totalBytes;
                        progress.LastUpdateTime = now;
                        
                        if (totalDurationTicks > 0)
                        {
                            var estimatedPosition = progress.InitialPosition + 
                                (long)((double)totalBytes / (8000000.0 / 8.0)) * TimeConversionUtil.SecondsToTicks(1);
                            estimatedPosition = Math.Min(estimatedPosition, totalDurationTicks);
                            progress.CurrentTicks = estimatedPosition;
                        }
                    }

                    if ((now - lastProgressReport).TotalSeconds >= 15.0)
                    {
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

    // MARK: ReportPositionChange
    private async Task ReportPositionChange(string sessionId, long positionTicks, bool isPaused)
    {
        if (!_streamProgress.TryGetValue(sessionId, out var progress)) return;

        var now = DateTime.UtcNow;
        var timeSinceLastReport = now - progress.LastReportedTime;
        
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

            _logger.LogDebug("PROGRESS REPORT #{Count}: Session {SessionId} at {Position}ms", 
                progress?.ReportCount ?? 0, sessionId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
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

    // MARK: Helper Methods
    private Guid? GetItemIdFromSession(string sessionId)
    {
        lock (_sessionLock)
        {
            return _itemSessions.Values.FirstOrDefault(s => s.SessionId == sessionId)?.ItemId;
        }
    }
    
    private string GetMediaSourceId(Guid itemId)
    {
        return itemId.ToString("N");
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

    private long CalculateSeekPosition(long byteOffset, long resumePositionTicks)
    {
        if (byteOffset < 1024 * 1024) return resumePositionTicks;

        var estimatedSeconds = byteOffset / (8000000.0 / 8.0);
        var estimatedTicks = TimeConversionUtil.SecondsToTicks(estimatedSeconds);
        
        return Math.Max(0, estimatedTicks);
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
                        _logger.LogInformation("Resume position for {ItemId}: {Position}ms", 
                            itemId, TimeConversionUtil.TicksToMilliseconds(positionTicks));
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

    private Task SendErrorResponse(HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        try
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = (int)statusCode;
                context.Response.ContentType = "text/plain";
                var buffer = System.Text.Encoding.UTF8.GetBytes(message);
                context.Response.OutputStream.Write(buffer);
                context.Response.Close();
            }
        }
        catch (ObjectDisposedException) { /* Response was likely already closed, which is fine. */ }
        catch (HttpListenerException) { /* Client may have disconnected; ignore. */ }
        
        return Task.CompletedTask;
    }
}