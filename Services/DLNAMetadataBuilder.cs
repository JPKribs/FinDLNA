using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk.Generated.Models;
using FinDLNA.Utilities;

namespace FinDLNA.Services;

// MARK: DlnaMetadataBuilder
public class DlnaMetadataBuilder
{
    private readonly ILogger<DlnaMetadataBuilder> _logger;
    private readonly JellyfinService _jellyfinService;

    public DlnaMetadataBuilder(ILogger<DlnaMetadataBuilder> logger, JellyfinService jellyfinService)
    {
        _logger = logger;
        _jellyfinService = jellyfinService;
    }

    // MARK: BuildItemMetadata
    public string BuildItemMetadata(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var metadata = new StringBuilder();
        
        var albumArtUrl = item.Id.HasValue ? _jellyfinService.GetImageUrlAsync(item.Id.Value, ImageType.Primary) : null;
        if (!string.IsNullOrEmpty(albumArtUrl))
        {
            metadata.AppendLine($"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>");
            
            if (deviceProfile?.Name?.Contains("Samsung") == true)
            {
                metadata.AppendLine($"<upnp:icon>{albumArtUrl}</upnp:icon>");
                metadata.AppendLine("<sec:dcmInfo>CREATIONDATE=0,FOLDER=0,BM=0</sec:dcmInfo>");
            }
        }
        
        if (!string.IsNullOrEmpty(item.Overview))
        {
            var description = item.Overview.Length > 200 ? item.Overview.Substring(0, 200) + "..." : item.Overview;
            metadata.AppendLine($"<dc:description>{SecurityElement.Escape(description)}</dc:description>");
        }
        
        if (item.ProductionYear.HasValue)
        {
            metadata.AppendLine($"<dc:date>{item.ProductionYear}</dc:date>");
        }

        AddTypeSpecificMetadata(metadata, item);
        AddGenresAndRating(metadata, item);
        
        return metadata.ToString().TrimEnd();
    }

    // MARK: BuildContainerMetadata
    public string BuildContainerMetadata(Guid? itemId, DeviceProfile? deviceProfile)
    {
        var metadata = new StringBuilder();
        
        if (itemId.HasValue)
        {
            var albumArtUrl = _jellyfinService.GetImageUrlAsync(itemId.Value, ImageType.Primary);
            if (!string.IsNullOrEmpty(albumArtUrl))
            {
                metadata.AppendLine($"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>");
                
                if (deviceProfile?.Name?.Contains("Samsung") == true)
                {
                    metadata.AppendLine($"<upnp:icon>{albumArtUrl}</upnp:icon>");
                    metadata.AppendLine("<sec:dcmInfo>CREATIONDATE=0,FOLDER=1</sec:dcmInfo>");
                }
            }
        }

        return metadata.ToString().TrimEnd();
    }

    // MARK: AddTypeSpecificMetadata
    private void AddTypeSpecificMetadata(StringBuilder metadata, BaseItemDto item)
    {
        switch (item.Type)
        {
            case BaseItemDto_Type.Episode:
                if (item.IndexNumber.HasValue)
                    metadata.AppendLine($"<upnp:episodeNumber>{item.IndexNumber}</upnp:episodeNumber>");
                if (item.ParentIndexNumber.HasValue)
                    metadata.AppendLine($"<upnp:episodeSeason>{item.ParentIndexNumber}</upnp:episodeSeason>");
                if (!string.IsNullOrEmpty(item.SeriesName))
                    metadata.AppendLine($"<upnp:seriesTitle>{SecurityElement.Escape(item.SeriesName)}</upnp:seriesTitle>");
                break;

            case BaseItemDto_Type.Audio:
                if (!string.IsNullOrEmpty(item.Album))
                    metadata.AppendLine($"<upnp:album>{SecurityElement.Escape(item.Album)}</upnp:album>");
                if (item.Artists?.Any() == true)
                {
                    foreach (var artist in item.Artists.Take(3))
                        metadata.AppendLine($"<upnp:artist>{SecurityElement.Escape(artist)}</upnp:artist>");
                }
                if (item.IndexNumber.HasValue)
                    metadata.AppendLine($"<upnp:originalTrackNumber>{item.IndexNumber}</upnp:originalTrackNumber>");
                break;
        }
    }

    // MARK: AddGenresAndRating
    private void AddGenresAndRating(StringBuilder metadata, BaseItemDto item)
    {
        var genres = item.Genres?.Take(2);
        if (genres?.Any() == true)
        {
            foreach (var genre in genres)
                metadata.AppendLine($"<upnp:genre>{SecurityElement.Escape(genre)}</upnp:genre>");
        }
        
        if (item.CommunityRating.HasValue)
        {
            var rating = Math.Round(item.CommunityRating.Value, 1);
            metadata.AppendLine($"<upnp:rating>{rating}</upnp:rating>");
        }
    }

    // MARK: GetDisplayTitle
    public string GetDisplayTitle(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Episode => $"{item.IndexNumber}. {item.Name ?? "Unknown Episode"}",
            BaseItemDto_Type.Season => $"Season {item.IndexNumber ?? 1}",
            BaseItemDto_Type.Audio when !string.IsNullOrEmpty(item.Album) => $"{item.Name} - {item.Album}",
            _ => item.Name ?? "Unknown"
        };
    }

    // MARK: BuildResourceAttributes
    public string BuildResourceAttributes(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var attributes = new List<string>();
        
        var duration = GetDurationString(item);
        var size = EstimateFileSize(item);
        var resolution = GetResolution(item);
        var bitrate = EstimateBitrate(item, deviceProfile);
        
        if (size > 0) attributes.Add($"size=\"{size}\"");
        if (!string.IsNullOrEmpty(duration) && duration != "0:00:00.000") attributes.Add($"duration=\"{duration}\"");
        if (!string.IsNullOrEmpty(resolution)) attributes.Add($"resolution=\"{resolution}\"");
        attributes.Add($"bitrate=\"{bitrate}\"");

        if (item.Type == BaseItemDto_Type.Audio)
        {
            var audioStream = item.MediaSources?.FirstOrDefault()?.MediaStreams?
                .FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
            if (audioStream?.SampleRate.HasValue == true)
                attributes.Add($"sampleFrequency=\"{audioStream.SampleRate.Value}\"");
            if (audioStream?.Channels.HasValue == true)
                attributes.Add($"nrAudioChannels=\"{audioStream.Channels.Value}\"");
        }
        
        return string.Join(" ", attributes);
    }

    // MARK: GetDurationString
    private string GetDurationString(BaseItemDto item)
    {
        var runTimeTicks = GetItemDuration(item);
        if (runTimeTicks <= 0) return "0:00:00.000";

        try
        {
            var timeSpan = TimeSpan.FromTicks(runTimeTicks);
            return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}.{timeSpan.Milliseconds:000}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error formatting duration for {Ticks} ticks", runTimeTicks);
            return "0:00:00.000";
        }
    }

    // MARK: GetItemDuration
    private long GetItemDuration(BaseItemDto item)
    {
        return item.RunTimeTicks ?? 
               item.MediaSources?.FirstOrDefault()?.RunTimeTicks ?? 
               item.CumulativeRunTimeTicks ?? 
               0;
    }

    // MARK: EstimateFileSize
    private long EstimateFileSize(BaseItemDto item)
    {
        var runTimeTicks = GetItemDuration(item);
        if (runTimeTicks <= 0) return 0;

        var durationSeconds = TimeConversionUtil.TicksToSeconds(runTimeTicks);
        var estimatedBitrate = 8000000;
        return (long)(durationSeconds * estimatedBitrate / 8);
    }

    // MARK: GetResolution
    private string GetResolution(BaseItemDto item)
    {
        var videoStream = item.MediaSources?.FirstOrDefault()?.MediaStreams?
            .FirstOrDefault(s => s.Type == MediaStream_Type.Video);

        if (videoStream?.Width.HasValue == true && videoStream?.Height.HasValue == true)
            return $"{videoStream.Width}x{videoStream.Height}";

        return "";
    }

    // MARK: EstimateBitrate
    private long EstimateBitrate(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var actualBitrate = item.MediaSources?.FirstOrDefault()?.Bitrate ?? 8000000;
        var maxBitrate = deviceProfile?.MaxStreamingBitrate ?? 120000000;
        
        return Math.Min(actualBitrate, maxBitrate);
    }
}