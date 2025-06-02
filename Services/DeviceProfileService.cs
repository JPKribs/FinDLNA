using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FinDLNA.Models;
using FinDLNA.Data;

namespace FinDLNA.Services;

// MARK: DeviceProfileService
public class DeviceProfileService
{
    private readonly ILogger<DeviceProfileService> _logger;
    private readonly DlnaContext _context;

    public DeviceProfileService(ILogger<DeviceProfileService> logger, DlnaContext context)
    {
        _logger = logger;
        _context = context;
    }

    // MARK: InitializeDefaultProfilesAsync
    public async Task InitializeDefaultProfilesAsync()
    {
        var existingProfiles = await _context.DeviceProfiles.CountAsync();
        if (existingProfiles > 0)
        {
            _logger.LogInformation("Device profiles already exist, skipping initialization");
            return;
        }

        _logger.LogInformation("Creating default device profile");

        var defaultProfile = new DeviceProfile
        {
            Name = "Default Universal Profile",
            UserAgent = "*",
            Manufacturer = "FinDLNA",
            ModelName = "Universal Media Server",
            ModelNumber = "1.0",
            FriendlyName = "Default Profile",
            MaxStreamingBitrate = 120000000,
            MaxStaticBitrate = 100000000,
            MusicStreamingTranscodingBitrate = 1280000,
            RequiresPlainVideoItems = false,
            RequiresPlainFolders = false,
            EnableMSMediaReceiverRegistrar = false,
            IgnoreTranscodeByteRangeRequests = false,
            DirectPlayProfiles = new List<DirectPlayProfile>
            {
                new()
                {
                    Container = "mp4",
                    Type = "Video",
                    VideoCodec = "h264",
                    AudioCodec = "aac"
                },
                new()
                {
                    Container = "mkv",
                    Type = "Video",
                    VideoCodec = "h264",
                    AudioCodec = "aac"
                },
                new()
                {
                    Container = "mp3",
                    Type = "Audio",
                    AudioCodec = "mp3"
                },
                new()
                {
                    Container = "flac",
                    Type = "Audio",
                    AudioCodec = "flac"
                }
            },
            TranscodingProfiles = new List<TranscodingProfile>
            {
                new()
                {
                    Container = "mp4",
                    Type = "Video",
                    VideoCodec = "h264",
                    AudioCodec = "aac",
                    Protocol = "http",
                    EstimateContentLength = false,
                    EnableMpegtsM2TsMode = false,
                    TranscodeSeekInfo = "Auto",
                    CopyTimestamps = false,
                    Context = "Streaming",
                    EnableSubtitlesInManifest = false,
                    MaxAudioChannels = 6,
                    MinSegments = 0,
                    SegmentLength = 0,
                    BreakOnNonKeyFrames = false
                },
                new()
                {
                    Container = "mp3",
                    Type = "Audio",
                    AudioCodec = "mp3",
                    Protocol = "http",
                    EstimateContentLength = false,
                    EnableMpegtsM2TsMode = false,
                    TranscodeSeekInfo = "Auto",
                    CopyTimestamps = false,
                    Context = "Streaming",
                    EnableSubtitlesInManifest = false,
                    MaxAudioChannels = 2,
                    MinSegments = 0,
                    SegmentLength = 0,
                    BreakOnNonKeyFrames = false
                }
            }
        };

        _context.DeviceProfiles.Add(defaultProfile);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Default device profile created with ID {ProfileId}", defaultProfile.Id);
    }

    // MARK: GetProfileAsync
    public async Task<DeviceProfile?> GetProfileAsync(string userAgent, string? manufacturer = null, string? modelName = null)
    {
        _logger.LogDebug("Looking for device profile - UserAgent: {UserAgent}, Manufacturer: {Manufacturer}, Model: {ModelName}",
            userAgent, manufacturer, modelName);

        var profiles = await _context.DeviceProfiles
            .Include(p => p.DirectPlayProfiles)
            .Include(p => p.TranscodingProfiles)
            .Include(p => p.ContainerProfiles)
            .Include(p => p.CodecProfiles)
            .Where(p => p.IsActive)
            .ToListAsync();

        foreach (var profile in profiles)
        {
            if (MatchesProfile(profile, userAgent, manufacturer, modelName))
            {
                _logger.LogInformation("Matched device profile: {ProfileName}", profile.Name);
                return profile;
            }
        }

        var defaultProfile = profiles.FirstOrDefault(p => p.UserAgent == "*");
        if (defaultProfile != null)
        {
            _logger.LogInformation("Using default device profile: {ProfileName}", defaultProfile.Name);
            return defaultProfile;
        }

        _logger.LogWarning("No matching device profile found, creating fallback profile");
        return await CreateFallbackProfileAsync();
    }

    // MARK: MatchesProfile
    private bool MatchesProfile(DeviceProfile profile, string userAgent, string? manufacturer, string? modelName)
    {
        if (profile.UserAgent == "*") return true;

        if (!string.IsNullOrEmpty(profile.UserAgent) &&
            userAgent.Contains(profile.UserAgent, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(profile.Manufacturer) &&
            manufacturer.Equals(profile.Manufacturer, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(modelName) && !string.IsNullOrEmpty(profile.ModelName) &&
            modelName.Equals(profile.ModelName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // MARK: CreateFallbackProfileAsync
    private async Task<DeviceProfile> CreateFallbackProfileAsync()
    {
        var fallbackProfile = new DeviceProfile
        {
            Name = "Fallback Profile",
            UserAgent = "*",
            Manufacturer = "Unknown",
            ModelName = "Unknown Device",
            ModelNumber = "1.0",
            FriendlyName = "Fallback Profile",
            TranscodingProfiles = new List<TranscodingProfile>
            {
                new()
                {
                    Container = "mp4",
                    Type = "Video",
                    VideoCodec = "h264",
                    AudioCodec = "aac",
                    Protocol = "http"
                }
            }
        };

        _context.DeviceProfiles.Add(fallbackProfile);
        await _context.SaveChangesAsync();

        return fallbackProfile;
    }

    // MARK: GetTranscodingProfileAsync
    public async Task<TranscodingProfile?> GetTranscodingProfileAsync(DeviceProfile profile, string mediaType)
    {
        await Task.CompletedTask;
        return profile.TranscodingProfiles.FirstOrDefault(p =>
            p.Type.Equals(mediaType, StringComparison.OrdinalIgnoreCase));
    }

    // MARK: ShouldDirectPlayAsync
    public async Task<bool> ShouldDirectPlayAsync(DeviceProfile profile, string? container, string? videoCodec, string? audioCodec, string mediaType)
    {
        await Task.CompletedTask;

        if (string.IsNullOrEmpty(container)) return false;

        return profile.DirectPlayProfiles.Any(dp =>
            dp.Type.Equals(mediaType, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(dp.Container) || dp.Container.Contains(container, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(dp.VideoCodec) || string.IsNullOrEmpty(videoCodec) || dp.VideoCodec.Contains(videoCodec, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(dp.AudioCodec) || string.IsNullOrEmpty(audioCodec) || dp.AudioCodec.Contains(audioCodec, StringComparison.OrdinalIgnoreCase)));
    }

    // MARK: GetAllProfilesAsync
    public async Task<List<DeviceProfile>> GetAllProfilesAsync()
    {
        return await _context.DeviceProfiles
            .Include(p => p.DirectPlayProfiles)
            .Include(p => p.TranscodingProfiles)
            .Include(p => p.ContainerProfiles)
            .Include(p => p.CodecProfiles)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    // MARK: CreateProfileAsync
    public async Task<DeviceProfile> CreateProfileAsync(DeviceProfile profile)
    {
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = DateTime.UtcNow;

        _context.DeviceProfiles.Add(profile);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created new device profile: {ProfileName} with ID {ProfileId}", profile.Name, profile.Id);
        return profile;
    }

    // MARK: UpdateProfileAsync
    public async Task<DeviceProfile?> UpdateProfileAsync(int id, DeviceProfile updatedProfile)
    {
        var existingProfile = await _context.DeviceProfiles
            .Include(p => p.DirectPlayProfiles)
            .Include(p => p.TranscodingProfiles)
            .Include(p => p.ContainerProfiles)
            .Include(p => p.CodecProfiles)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (existingProfile == null) return null;

        existingProfile.Name = updatedProfile.Name;
        existingProfile.UserAgent = updatedProfile.UserAgent;
        existingProfile.Manufacturer = updatedProfile.Manufacturer;
        existingProfile.ModelName = updatedProfile.ModelName;
        existingProfile.ModelNumber = updatedProfile.ModelNumber;
        existingProfile.FriendlyName = updatedProfile.FriendlyName;
        existingProfile.MaxStreamingBitrate = updatedProfile.MaxStreamingBitrate;
        existingProfile.MaxStaticBitrate = updatedProfile.MaxStaticBitrate;
        existingProfile.UpdatedAt = DateTime.UtcNow;

        _context.DirectPlayProfiles.RemoveRange(existingProfile.DirectPlayProfiles);
        _context.TranscodingProfiles.RemoveRange(existingProfile.TranscodingProfiles);

        existingProfile.DirectPlayProfiles = updatedProfile.DirectPlayProfiles;
        existingProfile.TranscodingProfiles = updatedProfile.TranscodingProfiles;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated device profile: {ProfileName}", existingProfile.Name);
        return existingProfile;
    }

    // MARK: DeleteProfileAsync
    public async Task<bool> DeleteProfileAsync(int id)
    {
        var profile = await _context.DeviceProfiles.FindAsync(id);
        if (profile == null) return false;

        profile.IsActive = false;
        profile.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Deactivated device profile: {ProfileName}", profile.Name);
        return true;
    }
}