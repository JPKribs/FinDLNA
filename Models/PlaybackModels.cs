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

// MARK: StreamProgress
public class StreamProgress
{
    public long CurrentTicks { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LastUpdateTime { get; set; }

    // Enhanced tracking for better position management
    public long InitialPosition { get; set; }
    public bool HasBeenSeeked { get; set; }
    public DateTime? LastSeekTime { get; set; }

    // Rate limiting for position updates
    public DateTime LastReportedTime { get; set; }
    public long LastReportedPosition { get; set; }

    // Statistics
    public long TotalBytesStreamed { get; set; }
    public int ReportCount { get; set; }
}

// MARK: Supporting Classes
public class ActiveSession
{
    public string SessionId { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public string ClientEndpoint { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}