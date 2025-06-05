using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Jellyfin.Sdk.Generated.Models;
using System.Text.RegularExpressions;

namespace FinDLNA.Services;

// MARK: SDK-Based DeviceProfileService
public class DeviceProfileService
{
    private readonly ILogger<DeviceProfileService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, DeviceProfile> _profileCache = new();
    private readonly object _cacheLock = new();

    public DeviceProfileService(ILogger<DeviceProfileService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    // MARK: GetProfileAsync
    public async Task<DeviceProfile?> GetProfileAsync(string userAgent, string? deviceDescription = null, 
        string? manufacturer = null, string? modelName = null, string? friendlyName = null)
    {
        var cacheKey = $"{userAgent}:{manufacturer}:{modelName}:{friendlyName}";
        
        lock (_cacheLock)
        {
            if (_profileCache.TryGetValue(cacheKey, out var cachedProfile))
            {
                _logger.LogDebug("Using cached profile: {ProfileName}", cachedProfile.Name);
                return cachedProfile;
            }
        }

        _logger.LogDebug("Device identification - UserAgent: {UserAgent}, Manufacturer: {Manufacturer}, Model: {ModelName}, Friendly: {FriendlyName}",
            userAgent, manufacturer, modelName, friendlyName);

        var profile = await CreateDeviceProfileAsync(userAgent, manufacturer, modelName, friendlyName);

        if (profile != null)
        {
            lock (_cacheLock)
            {
                _profileCache[cacheKey] = profile;
            }
        }

        return profile;
    }

    // MARK: CreateDeviceProfileAsync
    private async Task<DeviceProfile> CreateDeviceProfileAsync(string userAgent, string? manufacturer, string? modelName, string? friendlyName)
    {
        await Task.CompletedTask;

        if (IsSamsungDevice(userAgent, manufacturer, modelName))
        {
            return CreateSamsungProfile();
        }
        
        if (IsLgDevice(userAgent, manufacturer, modelName))
        {
            return CreateLgProfile();
        }
        
        if (IsXboxDevice(userAgent, manufacturer, modelName))
        {
            return CreateXboxProfile();
        }
        
        if (IsSonyDevice(userAgent, manufacturer, modelName))
        {
            return CreateSonyProfile();
        }
        
        if (IsPanasonicDevice(userAgent, manufacturer, modelName))
        {
            return CreatePanasonicProfile();
        }

        return CreateGenericProfile();
    }

    // MARK: Device Detection Methods
    private bool IsSamsungDevice(string userAgent, string? manufacturer, string? modelName)
    {
        return userAgent.Contains("SEC_HHP", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("Tizen", StringComparison.OrdinalIgnoreCase) ||
               manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool IsLgDevice(string userAgent, string? manufacturer, string? modelName)
    {
        return userAgent.Contains("LG", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("webOS", StringComparison.OrdinalIgnoreCase) ||
               manufacturer?.Contains("LG", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool IsXboxDevice(string userAgent, string? manufacturer, string? modelName)
    {
        return userAgent.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
               manufacturer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true ||
               modelName?.Contains("Xbox", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool IsSonyDevice(string userAgent, string? manufacturer, string? modelName)
    {
        return userAgent.Contains("BRAVIA", StringComparison.OrdinalIgnoreCase) ||
               userAgent.Contains("Sony", StringComparison.OrdinalIgnoreCase) ||
               manufacturer?.Contains("Sony", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool IsPanasonicDevice(string userAgent, string? manufacturer, string? modelName)
    {
        return userAgent.Contains("Panasonic", StringComparison.OrdinalIgnoreCase) ||
               manufacturer?.Contains("Panasonic", StringComparison.OrdinalIgnoreCase) == true;
    }

    // MARK: Samsung Profile
    private DeviceProfile CreateSamsungProfile()
    {
        _logger.LogInformation("Creating Samsung TV profile");
        
        var profile = new DeviceProfile
        {
            Name = "Samsung Smart TV",
            MaxStreamingBitrate = 20000000,
            MaxStaticBitrate = 20000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,mp3,ac3"
            },
            new DirectPlayProfile
            {
                Container = "mkv",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,mp3,ac3"
            },
            new DirectPlayProfile
            {
                Container = "avi",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,mp3,ac3"
            },
            new DirectPlayProfile
            {
                Container = "mp3",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "mp3"
            },
            new DirectPlayProfile
            {
                Container = "flac",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "flac"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "ts",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "ac3",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                EnableMpegtsM2TsMode = false,
                CopyTimestamps = false,
                MaxAudioChannels = "6",
                BreakOnNonKeyFrames = false
            },
            new TranscodingProfile
            {
                Container = "mp3",
                Type = TranscodingProfile_Type.Audio,
                AudioCodec = "mp3",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                MaxAudioChannels = "2"
            }
        };

        return profile;
    }

    // MARK: LG Profile
    private DeviceProfile CreateLgProfile()
    {
        _logger.LogInformation("Creating LG TV profile");
        
        var profile = new DeviceProfile
        {
            Name = "LG Smart TV",
            MaxStreamingBitrate = 15000000,
            MaxStaticBitrate = 15000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac"
            },
            new DirectPlayProfile
            {
                Container = "mkv",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,ac3"
            },
            new DirectPlayProfile
            {
                Container = "mp3",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "mp3"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "mp4",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                MaxAudioChannels = "6"
            }
        };

        return profile;
    }

    // MARK: Xbox Profile
    private DeviceProfile CreateXboxProfile()
    {
        _logger.LogInformation("Creating Xbox profile");
        
        var profile = new DeviceProfile
        {
            Name = "Xbox Console",
            MaxStreamingBitrate = 25000000,
            MaxStaticBitrate = 25000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,ac3"
            },
            new DirectPlayProfile
            {
                Container = "mkv",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,ac3"
            },
            new DirectPlayProfile
            {
                Container = "avi",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,ac3"
            },
            new DirectPlayProfile
            {
                Container = "mp3",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "mp3"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "ts",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "ac3",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                MaxAudioChannels = "6"
            }
        };

        return profile;
    }

    // MARK: Sony Profile
    private DeviceProfile CreateSonyProfile()
    {
        _logger.LogInformation("Creating Sony TV profile");
        
        var profile = new DeviceProfile
        {
            Name = "Sony Bravia TV",
            MaxStreamingBitrate = 15000000,
            MaxStaticBitrate = 15000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac"
            },
            new DirectPlayProfile
            {
                Container = "avi",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,mp3"
            },
            new DirectPlayProfile
            {
                Container = "mp3",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "mp3"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "mp4",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                MaxAudioChannels = "2"
            }
        };

        return profile;
    }

    // MARK: Panasonic Profile
    private DeviceProfile CreatePanasonicProfile()
    {
        _logger.LogInformation("Creating Panasonic TV profile");
        
        var profile = new DeviceProfile
        {
            Name = "Panasonic TV",
            MaxStreamingBitrate = 15000000,
            MaxStaticBitrate = 15000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac"
            },
            new DirectPlayProfile
            {
                Container = "avi",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac,mp3"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "mp4",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                MaxAudioChannels = "2"
            }
        };

        return profile;
    }

    // MARK: Generic Profile
    private DeviceProfile CreateGenericProfile()
    {
        _logger.LogInformation("Creating generic DLNA profile");
        
        var profile = new DeviceProfile
        {
            Name = "Generic DLNA Device",
            MaxStreamingBitrate = 120000000,
            MaxStaticBitrate = 100000000,
            MusicStreamingTranscodingBitrate = 1280000
        };

        profile.DirectPlayProfiles = new List<DirectPlayProfile>
        {
            new DirectPlayProfile
            {
                Container = "mp4",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac"
            },
            new DirectPlayProfile
            {
                Container = "mkv",
                Type = DirectPlayProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac"
            },
            new DirectPlayProfile
            {
                Container = "mp3",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "mp3"
            },
            new DirectPlayProfile
            {
                Container = "flac",
                Type = DirectPlayProfile_Type.Audio,
                AudioCodec = "flac"
            }
        };
        
        profile.TranscodingProfiles = new List<TranscodingProfile>
        {
            new TranscodingProfile
            {
                Container = "mp4",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                EnableMpegtsM2TsMode = false,
                CopyTimestamps = false,
                EnableSubtitlesInManifest = false,
                MaxAudioChannels = "6",
                MinSegments = 0,
                SegmentLength = 0,
                BreakOnNonKeyFrames = false
            },
            new TranscodingProfile
            {
                Container = "mp3",
                Type = TranscodingProfile_Type.Audio,
                AudioCodec = "mp3",
                Protocol = TranscodingProfile_Protocol.Http,
                EstimateContentLength = false,
                EnableMpegtsM2TsMode = false,
                CopyTimestamps = false,
                EnableSubtitlesInManifest = false,
                MaxAudioChannels = "2",
                MinSegments = 0,
                SegmentLength = 0,
                BreakOnNonKeyFrames = false
            }
        };

        return profile;
    }

    // MARK: ShouldDirectPlayAsync
    public async Task<bool> ShouldDirectPlayAsync(DeviceProfile profile, string? container, string? videoCodec, string? audioCodec, string mediaType)
    {
        await Task.CompletedTask;

        if (string.IsNullOrEmpty(container)) return false;

        var profileType = mediaType.Equals("Video", StringComparison.OrdinalIgnoreCase) 
            ? DirectPlayProfile_Type.Video 
            : DirectPlayProfile_Type.Audio;

        return profile.DirectPlayProfiles?.Any(dp =>
            dp.Type == profileType &&
            (string.IsNullOrEmpty(dp.Container) || dp.Container.Split(',').Any(c => c.Trim().Equals(container, StringComparison.OrdinalIgnoreCase))) &&
            (string.IsNullOrEmpty(dp.VideoCodec) || string.IsNullOrEmpty(videoCodec) || dp.VideoCodec.Split(',').Any(c => c.Trim().Equals(videoCodec, StringComparison.OrdinalIgnoreCase))) &&
            (string.IsNullOrEmpty(dp.AudioCodec) || string.IsNullOrEmpty(audioCodec) || dp.AudioCodec.Split(',').Any(c => c.Trim().Equals(audioCodec, StringComparison.OrdinalIgnoreCase)))) == true;
    }

    // MARK: GetTranscodingProfileAsync
    public async Task<TranscodingProfile?> GetTranscodingProfileAsync(DeviceProfile profile, string mediaType)
    {
        await Task.CompletedTask;
        
        var profileType = mediaType.Equals("Video", StringComparison.OrdinalIgnoreCase) 
            ? TranscodingProfile_Type.Video 
            : TranscodingProfile_Type.Audio;
            
        return profile.TranscodingProfiles?.FirstOrDefault(p => p.Type == profileType);
    }

    // MARK: ClearCache
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _profileCache.Clear();
        }
        _logger.LogInformation("Device profile cache cleared");
    }
}