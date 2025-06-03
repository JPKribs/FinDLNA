namespace FinDLNA.Models;

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
    public long? StartTimeTicks { get; set; }
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
    public long DurationTicks { get; set; }
}

// MARK: PlaybackSession
public class PlaybackSession
{
    public string SessionId { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset LastProgressUpdate { get; set; }
    public long LastPositionTicks { get; set; }
    public bool IsPaused { get; set; }
    public DateTimeOffset? PauseTime { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public string ClientEndpoint { get; set; } = string.Empty;
}