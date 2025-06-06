using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Models;

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

// MARK: ActiveSession
public class ActiveSession
{
    public string SessionId { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public string ClientEndpoint { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}