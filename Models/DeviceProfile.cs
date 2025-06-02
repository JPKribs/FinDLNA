using System.ComponentModel.DataAnnotations;

namespace FinDLNA.Models;

public class DeviceProfile
{
    [Key]
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    
    public int MaxStreamingBitrate { get; set; } = 120000000;
    public int MaxStaticBitrate { get; set; } = 100000000;
    public int MusicStreamingTranscodingBitrate { get; set; } = 1280000;
    
    public bool RequiresPlainVideoItems { get; set; }
    public bool RequiresPlainFolders { get; set; }
    public bool EnableMSMediaReceiverRegistrar { get; set; }
    public bool IgnoreTranscodeByteRangeRequests { get; set; }
    
    public string MaxAlbumArtWidth { get; set; } = "480";
    public string MaxAlbumArtHeight { get; set; } = "480";
    public string MaxIconWidth { get; set; } = "48";
    public string MaxIconHeight { get; set; } = "48";
    
    public string TimelineOffsetSeconds { get; set; } = "0";
    
    public List<DirectPlayProfile> DirectPlayProfiles { get; set; } = new();
    public List<TranscodingProfile> TranscodingProfiles { get; set; } = new();
    public List<ContainerProfile> ContainerProfiles { get; set; } = new();
    public List<CodecProfile> CodecProfiles { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

public class DirectPlayProfile
{
    [Key]
    public int Id { get; set; }
    
    public int DeviceProfileId { get; set; }
    public DeviceProfile DeviceProfile { get; set; } = null!;
    
    public string Container { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public string VideoCodec { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class TranscodingProfile
{
    [Key]
    public int Id { get; set; }
    
    public int DeviceProfileId { get; set; }
    public DeviceProfile DeviceProfile { get; set; } = null!;
    
    public string Container { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public bool EstimateContentLength { get; set; } = false;
    public bool EnableMpegtsM2TsMode { get; set; } = false;
    public string TranscodeSeekInfo { get; set; } = string.Empty;
    public bool CopyTimestamps { get; set; } = false;
    public string Context { get; set; } = string.Empty;
    public bool EnableSubtitlesInManifest { get; set; } = false;
    public int MaxAudioChannels { get; set; } = 6;
    public int MinSegments { get; set; } = 0;
    public int SegmentLength { get; set; } = 0;
    public bool BreakOnNonKeyFrames { get; set; } = false;
}

public class ContainerProfile
{
    [Key]
    public int Id { get; set; }
    
    public int DeviceProfileId { get; set; }
    public DeviceProfile DeviceProfile { get; set; } = null!;
    
    public string Type { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
}

public class CodecProfile
{
    [Key]
    public int Id { get; set; }
    
    public int DeviceProfileId { get; set; }
    public DeviceProfile DeviceProfile { get; set; } = null!;
    
    public string Type { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
}