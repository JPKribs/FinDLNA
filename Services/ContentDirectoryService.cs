using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Models;
using FinDLNA.Utilities;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: ContentDirectoryService
public class ContentDirectoryService
{
    private readonly ILogger<ContentDirectoryService> _logger;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;
    private readonly XmlTemplateService _xmlTemplateService;
    private readonly IConfiguration _configuration;

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Behind The Scenes", "Deleted Scenes", "Interviews", "Scenes", "Samples", "Shorts",
        "Featurettes", "Extras", "Trailers", "Theme Videos", "Theme Songs", "Specials",
        "Collections", "Playlists"
    };

    private static readonly HashSet<BaseItemDto_Type> ContainerTypes = new()
    {
        BaseItemDto_Type.AggregateFolder, BaseItemDto_Type.CollectionFolder, BaseItemDto_Type.BoxSet,
        BaseItemDto_Type.Folder, BaseItemDto_Type.UserView, BaseItemDto_Type.Series,
        BaseItemDto_Type.Season, BaseItemDto_Type.MusicAlbum, BaseItemDto_Type.MusicArtist,
        BaseItemDto_Type.Playlist
    };

    private static readonly HashSet<BaseItemDto_Type> MediaTypes = new()
    {
        BaseItemDto_Type.Movie, BaseItemDto_Type.Episode, BaseItemDto_Type.Audio,
        BaseItemDto_Type.Photo, BaseItemDto_Type.Video, BaseItemDto_Type.MusicVideo,
        BaseItemDto_Type.AudioBook
    };

    public ContentDirectoryService(
        ILogger<ContentDirectoryService> logger,
        JellyfinService jellyfinService,
        DeviceProfileService deviceProfileService,
        XmlTemplateService xmlTemplateService,
        IConfiguration configuration)
    {
        _logger = logger;
        _jellyfinService = jellyfinService;
        _deviceProfileService = deviceProfileService;
        _xmlTemplateService = xmlTemplateService;
        _configuration = configuration;
    }

    // MARK: GetServiceDescriptionXml
    public string GetServiceDescriptionXml()
    {
        return _xmlTemplateService.GetTemplate("ContentDirectoryServiceDescription");
    }

    // MARK: ProcessBrowseRequestAsync
    public async Task<string> ProcessBrowseRequestAsync(string soapBody, string? userAgent)
    {
        try
        {
            var browseParams = ParseBrowseRequest(soapBody);
            if (browseParams == null)
            {
                _logger.LogWarning("Failed to parse browse request");
                return CreateSoapFault("Invalid Browse request");
            }

            var deviceProfile = await _deviceProfileService.GetProfileAsync(userAgent ?? "");
            _logger.LogInformation("Processing browse request for {ObjectId} with {DeviceProfile}", 
                browseParams.ObjectId, deviceProfile?.Name ?? "Generic");

            var result = browseParams.BrowseFlag == "BrowseMetadata" 
                ? await ProcessBrowseMetadataAsync(browseParams.ObjectId, deviceProfile)
                : await ProcessBrowseChildrenAsync(browseParams, deviceProfile);

            _logger.LogInformation("Browse completed: {NumberReturned}/{TotalMatches} items", 
                result.NumberReturned, result.TotalMatches);

            return CreateBrowseResponse(result.DidlXml, result.NumberReturned, result.TotalMatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing browse request");
            return CreateSoapFault("Internal server error");
        }
    }

    // MARK: ParseBrowseRequest
    private BrowseRequestParams? ParseBrowseRequest(string soapBody)
    {
        try
        {
            var doc = XDocument.Parse(soapBody);
            var ns = XNamespace.Get("urn:schemas-upnp-org:service:ContentDirectory:1");
            var browseElement = doc.Descendants(ns + "Browse").FirstOrDefault();

            if (browseElement == null) return null;

            return new BrowseRequestParams
            {
                ObjectId = browseElement.Element("ObjectID")?.Value ?? "0",
                BrowseFlag = browseElement.Element("BrowseFlag")?.Value ?? "BrowseDirectChildren",
                StartingIndex = int.Parse(browseElement.Element("StartingIndex")?.Value ?? "0"),
                RequestedCount = int.Parse(browseElement.Element("RequestedCount")?.Value ?? "0"),
                Filter = browseElement.Element("Filter")?.Value ?? "*",
                SortCriteria = browseElement.Element("SortCriteria")?.Value ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse browse request");
            return null;
        }
    }

    // MARK: ProcessBrowseMetadataAsync
    private async Task<BrowseResult> ProcessBrowseMetadataAsync(string objectId, DeviceProfile? deviceProfile)
    {
        if (objectId == "0")
            return await CreateRootMetadataResponse(deviceProfile);

        if (objectId.StartsWith("library:") && Guid.TryParse(objectId.Substring(8), out var libGuid))
            return await CreateLibraryMetadataResponse(libGuid, objectId, deviceProfile);

        if (Guid.TryParse(objectId, out var itemGuid))
            return await CreateItemMetadataResponse(itemGuid, deviceProfile);

        return CreateEmptyResult();
    }

    // MARK: ProcessBrowseChildrenAsync
    private async Task<BrowseResult> ProcessBrowseChildrenAsync(BrowseRequestParams request, DeviceProfile? deviceProfile)
    {
        if (request.ObjectId == "0")
            return await BrowseRootAsync(deviceProfile);

        if (request.ObjectId.StartsWith("library:") && Guid.TryParse(request.ObjectId.Substring(8), out var libGuid))
            return await BrowseLibraryAsync(libGuid, request, deviceProfile);

        if (Guid.TryParse(request.ObjectId, out var itemGuid))
            return await BrowseItemAsync(itemGuid, request, deviceProfile);

        return CreateEmptyResult();
    }

    // MARK: BrowseRootAsync
    private async Task<BrowseResult> BrowseRootAsync(DeviceProfile? deviceProfile)
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        if (libraries?.Any() != true)
        {
            _logger.LogWarning("No library folders found");
            return CreateEmptyResult();
        }

        var containers = new List<string>();
        foreach (var library in libraries.Where(l => l.Id.HasValue))
        {
            var childCount = await GetLibraryChildCountAsync(library.Id!.Value);
            if (childCount > 0 || IsAlwaysVisibleLibrary(library))
            {
                var container = CreateContainerXml(
                    $"library:{library.Id.Value}",
                    "0",
                    library.Name ?? "Unknown",
                    GetLibraryUpnpClass(library),
                    childCount,
                    library.Id.Value,
                    deviceProfile
                );
                containers.Add(container);
            }
        }

        var didlXml = CreateDidlXml(string.Join("", containers));
        return new BrowseResult { DidlXml = didlXml, NumberReturned = containers.Count, TotalMatches = containers.Count };
    }

    // MARK: BrowseLibraryAsync
    private async Task<BrowseResult> BrowseLibraryAsync(Guid libraryId, BrowseRequestParams request, DeviceProfile? deviceProfile)
    {
        var items = await _jellyfinService.GetLibraryContentAsync(libraryId);
        return await ProcessItemsAndPaginate(items, $"library:{libraryId}", request, deviceProfile);
    }

    // MARK: BrowseItemAsync
    private async Task<BrowseResult> BrowseItemAsync(Guid itemId, BrowseRequestParams request, DeviceProfile? deviceProfile)
    {
        var items = await _jellyfinService.GetItemsAsync(itemId);
        return await ProcessItemsAndPaginate(items, itemId.ToString(), request, deviceProfile);
    }

    // MARK: ProcessItemsAndPaginate
    private async Task<BrowseResult> ProcessItemsAndPaginate(IReadOnlyList<BaseItemDto>? items, string parentId, BrowseRequestParams request, DeviceProfile? deviceProfile)
    {
        if (items?.Any() != true)
            return CreateEmptyResult();

        var allResults = await ProcessItemsForBrowsing(items, parentId, deviceProfile);
        var sortedResults = ApplySorting(allResults, request.SortCriteria, deviceProfile);
        
        var totalMatches = sortedResults.Count;
        var paginatedResults = sortedResults
            .Skip(request.StartingIndex)
            .Take(request.RequestedCount > 0 ? request.RequestedCount : int.MaxValue)
            .Select(x => x.Xml)
            .ToList();

        var didlXml = CreateDidlXml(string.Join("", paginatedResults));
        return new BrowseResult { DidlXml = didlXml, NumberReturned = paginatedResults.Count, TotalMatches = totalMatches };
    }

    // MARK: ProcessItemsForBrowsing
    private async Task<List<BrowseResultItem>> ProcessItemsForBrowsing(IReadOnlyList<BaseItemDto> items, string parentId, DeviceProfile? deviceProfile)
    {
        var results = new List<BrowseResultItem>();

        foreach (var item in items.Where(IsItemIncluded))
        {
            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            if (ContainerTypes.Contains(itemType))
            {
                var childCount = await GetItemChildCountAsync(item.Id!.Value);
                if (childCount > 0 || IsAlwaysVisibleContainer(item))
                {
                    results.Add(new BrowseResultItem
                    {
                        IsContainer = true,
                        Xml = CreateContainerXml(item.Id.Value.ToString(), parentId, itemName, GetUpnpClass(itemType), childCount, item.Id.Value, deviceProfile),
                        Title = itemName,
                        SortIndex = item.IndexNumber ?? int.MaxValue,
                        Item = item
                    });
                }
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, parentId, deviceProfile);
                if (!string.IsNullOrEmpty(itemXml))
                {
                    results.Add(new BrowseResultItem
                    {
                        IsContainer = false,
                        Xml = itemXml,
                        Title = itemName,
                        SortIndex = item.IndexNumber ?? int.MaxValue,
                        Item = item
                    });
                }
            }
        }

        return results;
    }

    // MARK: CreateItemXml
    private string CreateItemXml(BaseItemDto item, string parentId, DeviceProfile? deviceProfile)
    {
        if (!item.Id.HasValue) return "";

        var streamUrl = BuildStreamUrl(item.Id.Value, deviceProfile);
        if (string.IsNullOrEmpty(streamUrl)) return "";

        var mimeType = GetMimeType(item, deviceProfile);
        var protocolInfo = GetProtocolInfo(mimeType, deviceProfile);
        var title = SecurityElement.Escape(GetDisplayTitle(item));
        var additionalMetadata = BuildItemMetadata(item, deviceProfile);
        var attributes = BuildResourceAttributes(item, deviceProfile);
        
        return _xmlTemplateService.GetTemplate("ItemTemplate",
            item.Id.Value,                          // {0} - id
            parentId,                               // {1} - parentID
            title,                                  // {2} - title
            DateTime.UtcNow.ToString("yyyy-MM-dd"), // {3} - date
            GetUpnpClass(item.Type ?? BaseItemDto_Type.Video), // {4} - upnp:class
            additionalMetadata,                     // {5} - additional metadata
            protocolInfo,                           // {6} - protocolInfo
            attributes,                             // {7} - res attributes
            streamUrl                               // {8} - stream URL
        );
    }

    // MARK: BuildStreamUrl
    private string BuildStreamUrl(Guid itemId, DeviceProfile? deviceProfile)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        
        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
        {
            _logger.LogError("Missing Jellyfin server URL or access token");
            return "";
        }

        var queryParams = new List<string>
        {
            $"api_key={accessToken}",
            "Static=true"
        };

        if (deviceProfile?.MaxStreamingBitrate.HasValue == true)
        {
            queryParams.Add($"MaxStreamingBitrate={deviceProfile.MaxStreamingBitrate.Value}");
        }

        // Device-specific optimizations
        if (deviceProfile?.Name != null)
        {
            if (deviceProfile.Name.Contains("Samsung"))
            {
                queryParams.Add("EnableAutoStreamCopy=true");
            }
            else if (deviceProfile.Name.Contains("Xbox"))
            {
                queryParams.Add("VideoCodec=h264");
                queryParams.Add("AudioCodec=aac");
            }
        }

        var queryString = string.Join("&", queryParams);
        return $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
    }

    // MARK: BuildItemMetadata
    private string BuildItemMetadata(BaseItemDto item, DeviceProfile? deviceProfile)
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

        // Type-specific metadata
        if (item.Type == BaseItemDto_Type.Episode)
        {
            if (item.IndexNumber.HasValue)
                metadata.AppendLine($"<upnp:episodeNumber>{item.IndexNumber}</upnp:episodeNumber>");
            if (item.ParentIndexNumber.HasValue)
                metadata.AppendLine($"<upnp:episodeSeason>{item.ParentIndexNumber}</upnp:episodeSeason>");
            if (!string.IsNullOrEmpty(item.SeriesName))
                metadata.AppendLine($"<upnp:seriesTitle>{SecurityElement.Escape(item.SeriesName)}</upnp:seriesTitle>");
        }
        else if (item.Type == BaseItemDto_Type.Audio)
        {
            if (!string.IsNullOrEmpty(item.Album))
                metadata.AppendLine($"<upnp:album>{SecurityElement.Escape(item.Album)}</upnp:album>");
            if (item.Artists?.Any() == true)
            {
                foreach (var artist in item.Artists.Take(3))
                    metadata.AppendLine($"<upnp:artist>{SecurityElement.Escape(artist)}</upnp:artist>");
            }
        }
        
        return metadata.ToString().TrimEnd();
    }

    // MARK: BuildResourceAttributes
    private string BuildResourceAttributes(BaseItemDto item, DeviceProfile? deviceProfile)
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

    // MARK: Helper Methods
    private string GetDurationString(BaseItemDto item)
    {
        var runTimeTicks = item.RunTimeTicks ?? item.MediaSources?.FirstOrDefault()?.RunTimeTicks ?? 0;
        if (runTimeTicks <= 0) return "0:00:00.000";

        try
        {
            var timeSpan = TimeSpan.FromTicks(runTimeTicks);
            return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}.{timeSpan.Milliseconds:000}";
        }
        catch
        {
            return "0:00:00.000";
        }
    }

    private long EstimateFileSize(BaseItemDto item)
    {
        var runTimeTicks = item.RunTimeTicks ?? 0;
        if (runTimeTicks <= 0) return 0;

        var durationSeconds = TimeConversionUtil.TicksToSeconds(runTimeTicks);
        return (long)(durationSeconds * 8000000 / 8);
    }

    private string GetResolution(BaseItemDto item)
    {
        var videoStream = item.MediaSources?.FirstOrDefault()?.MediaStreams?
            .FirstOrDefault(s => s.Type == MediaStream_Type.Video);
        
        if (videoStream?.Width.HasValue == true && videoStream?.Height.HasValue == true)
            return $"{videoStream.Width}x{videoStream.Height}";
        
        return "";
    }

    private long EstimateBitrate(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var actualBitrate = item.MediaSources?.FirstOrDefault()?.Bitrate ?? 8000000;
        var maxBitrate = deviceProfile?.MaxStreamingBitrate ?? 120000000;
        return Math.Min(actualBitrate, maxBitrate);
    }

    private string GetProtocolInfo(string mimeType, DeviceProfile? deviceProfile)
    {
        var dlnaFlags = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        
        if (deviceProfile?.Name?.Contains("Samsung") == true)
            dlnaFlags = "DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        else if (deviceProfile?.Name?.Contains("Xbox") == true)
            dlnaFlags = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01500000000000000000000000000000";
        
        return $"http-get:*:{mimeType}:{dlnaFlags}";
    }

    private string GetMimeType(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Audio => "audio/mpeg",
            BaseItemDto_Type.Photo => "image/jpeg",
            _ => "video/mp4"
        };
    }

    private string GetDisplayTitle(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Episode => $"{item.IndexNumber}. {item.Name ?? "Unknown Episode"}",
            BaseItemDto_Type.Season => $"Season {item.IndexNumber ?? 1}",
            BaseItemDto_Type.Audio when !string.IsNullOrEmpty(item.Album) => $"{item.Name} - {item.Album}",
            _ => item.Name ?? "Unknown"
        };
    }

    // MARK: CreateContainerXml
    private string CreateContainerXml(string id, string parentId, string title, string upnpClass, int childCount, Guid? itemId, DeviceProfile? deviceProfile)
    {
        var escapedTitle = SecurityElement.Escape(title);
        var additionalMetadata = "";
        
        if (itemId.HasValue)
        {
            var albumArtUrl = _jellyfinService.GetImageUrlAsync(itemId.Value, ImageType.Primary);
            if (!string.IsNullOrEmpty(albumArtUrl))
            {
                var metadata = new StringBuilder();
                metadata.AppendLine($"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>");
                
                if (deviceProfile?.Name?.Contains("Samsung") == true)
                {
                    metadata.AppendLine($"<upnp:icon>{albumArtUrl}</upnp:icon>");
                    metadata.AppendLine("<sec:dcmInfo>CREATIONDATE=0,FOLDER=1</sec:dcmInfo>");
                }
                additionalMetadata = metadata.ToString().TrimEnd();
            }
        }

        return _xmlTemplateService.GetTemplate("ContainerTemplate",
            id, parentId, childCount, escapedTitle, upnpClass, additionalMetadata);
    }

    // MARK: Metadata Response Methods
    private async Task<BrowseResult> CreateRootMetadataResponse(DeviceProfile? deviceProfile)
    {
        var childCount = (await _jellyfinService.GetLibraryFoldersAsync())?.Count ?? 0;
        var containerXml = CreateContainerXml("0", "-1", "FinDLNA Media Server", "object.container.storageFolder", childCount, null, deviceProfile);
        var didlXml = CreateDidlXml(containerXml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    private async Task<BrowseResult> CreateLibraryMetadataResponse(Guid libGuid, string objectId, DeviceProfile? deviceProfile)
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        var library = libraries?.FirstOrDefault(l => l.Id == libGuid);
        if (library == null) return CreateEmptyResult();

        var childCount = await GetLibraryChildCountAsync(libGuid);
        var containerXml = CreateContainerXml(objectId, "0", library.Name ?? "Unknown", GetLibraryUpnpClass(library), childCount, library.Id, deviceProfile);
        var didlXml = CreateDidlXml(containerXml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    private async Task<BrowseResult> CreateItemMetadataResponse(Guid itemGuid, DeviceProfile? deviceProfile)
    {
        var item = await _jellyfinService.GetItemAsync(itemGuid);
        if (item == null) return CreateEmptyResult();

        string xml;
        if (ContainerTypes.Contains(item.Type ?? BaseItemDto_Type.Folder))
        {
            var childCount = await GetItemChildCountAsync(itemGuid);
            xml = CreateContainerXml(itemGuid.ToString(), GetParentId(item), item.Name ?? "Unknown", GetUpnpClass(item.Type ?? BaseItemDto_Type.Folder), childCount, item.Id, deviceProfile);
        }
        else
        {
            xml = CreateItemXml(item, GetParentId(item), deviceProfile);
        }
        
        var didlXml = CreateDidlXml(xml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    // MARK: Count Methods
    private async Task<int> GetLibraryChildCountAsync(Guid libraryId)
    {
        try
        {
            var items = await _jellyfinService.GetLibraryContentAsync(libraryId);
            return items?.Count(IsItemIncluded) ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting child count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    private async Task<int> GetItemChildCountAsync(Guid itemId)
    {
        try
        {
            var items = await _jellyfinService.GetItemsAsync(itemId);
            return items?.Count(IsItemIncluded) ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting child count for item {ItemId}", itemId);
            return 0;
        }
    }

    // MARK: Sorting and Filtering
    private List<BrowseResultItem> ApplySorting(List<BrowseResultItem> items, string? sortCriteria, DeviceProfile? deviceProfile)
    {
        if (deviceProfile?.Name?.Contains("Samsung") == true)
        {
            return items.OrderBy(item => item.IsContainer ? 0 : 1)
                       .ThenBy(item => item.Title)
                       .ThenBy(item => item.SortIndex)
                       .ToList();
        }
        
        if (!string.IsNullOrEmpty(sortCriteria))
        {
            if (sortCriteria.Contains("dc:title"))
                return items.OrderBy(item => item.Title).ToList();
            if (sortCriteria.Contains("dc:date"))
                return items.OrderBy(item => item.Item?.DateCreated ?? DateTime.MinValue).ToList();
        }

        return items.OrderBy(item => item.IsContainer ? 0 : 1)
                   .ThenBy(item => item.SortIndex)
                   .ThenBy(item => item.Title)
                   .ToList();
    }

    private bool IsItemIncluded(BaseItemDto item)
    {
        if (!item.Id.HasValue) return false;
        if (ExcludedFolderNames.Contains(item.Name ?? "")) return false;
        var itemType = item.Type ?? BaseItemDto_Type.Folder;
        return ContainerTypes.Contains(itemType) || MediaTypes.Contains(itemType);
    }

    private bool IsAlwaysVisibleLibrary(BaseItemDto library)
    {
        var collectionType = library.CollectionType;
        return collectionType == BaseItemDto_CollectionType.Movies ||
               collectionType == BaseItemDto_CollectionType.Tvshows ||
               collectionType == BaseItemDto_CollectionType.Music ||
               collectionType == BaseItemDto_CollectionType.Photos;
    }

    private bool IsAlwaysVisibleContainer(BaseItemDto item)
    {
        var itemType = item.Type ?? BaseItemDto_Type.Folder;
        return itemType == BaseItemDto_Type.Series ||
               itemType == BaseItemDto_Type.Season ||
               itemType == BaseItemDto_Type.MusicAlbum ||
               itemType == BaseItemDto_Type.MusicArtist;
    }

    private string GetParentId(BaseItemDto item)
    {
        if (!item.ParentId.HasValue) return "0";
        
        var libraries = _jellyfinService.GetLibraryFoldersAsync().Result;
        var isLibraryFolder = libraries?.Any(l => l.Id == item.ParentId.Value) == true;
        
        return isLibraryFolder ? $"library:{item.ParentId.Value}" : item.ParentId.Value.ToString();
    }

    private string GetUpnpClass(BaseItemDto_Type itemType)
    {
        return itemType switch
        {
            BaseItemDto_Type.Movie => "object.item.videoItem.movie",
            BaseItemDto_Type.AudioBook => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.Episode => "object.item.videoItem",
            BaseItemDto_Type.Series => "object.container.album.videoAlbum",
            BaseItemDto_Type.Season => "object.container.album.videoAlbum",
            BaseItemDto_Type.Audio => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.MusicAlbum => "object.container.album.musicAlbum",
            BaseItemDto_Type.MusicArtist => "object.container.person.musicArtist",
            BaseItemDto_Type.MusicVideo => "object.item.videoItem.musicVideoClip",
            BaseItemDto_Type.Photo => "object.item.imageItem.photo",
            BaseItemDto_Type.Video => "object.item.videoItem",
            BaseItemDto_Type.CollectionFolder => "object.container.storageFolder",
            BaseItemDto_Type.Folder => "object.container.storageFolder",
            _ => "object.container.storageFolder"
        };
    }

    private string GetLibraryUpnpClass(BaseItemDto library)
    {
        return library.CollectionType switch
        {
            BaseItemDto_CollectionType.Movies => "object.container.genre.movieGenre",
            BaseItemDto_CollectionType.Tvshows => "object.container.genre.movieGenre", 
            BaseItemDto_CollectionType.Music => "object.container.storageFolder",
            BaseItemDto_CollectionType.Photos => "object.container.album.photoAlbum",
            BaseItemDto_CollectionType.Books => "object.container.storageFolder",
            _ => "object.container.storageFolder"
        };
    }

    private string CreateDidlXml(string content)
    {
        return _xmlTemplateService.GetTemplate("DidlLiteTemplate", content);
    }

    private string CreateBrowseResponse(string result, int numberReturned, int totalMatches)
    {
        var escapedResult = SecurityElement.Escape(result);
        return _xmlTemplateService.GetTemplate("BrowseResponse", escapedResult, numberReturned, totalMatches);
    }

    private string CreateSoapFault(string error)
    {
        var escapedError = SecurityElement.Escape(error);
        return _xmlTemplateService.GetTemplate("SoapFault", escapedError);
    }

    private BrowseResult CreateEmptyResult()
    {
        var didlXml = CreateDidlXml("");
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 0, TotalMatches = 0 };
    }

    // MARK: SOAP Action Handlers
    public string ProcessSearchCapabilitiesRequest()
    {
        return _xmlTemplateService.GetTemplate("SearchCapabilitiesResponse", "");
    }

    public string ProcessSortCapabilitiesRequest()
    {
        return _xmlTemplateService.GetTemplate("SortCapabilitiesResponse", "dc:title,dc:date,upnp:class");
    }

    // MARK: Helper Classes
    private class BrowseRequestParams
    {
        public string ObjectId { get; set; } = "";
        public string BrowseFlag { get; set; } = "";
        public int StartingIndex { get; set; }
        public int RequestedCount { get; set; }
        public string Filter { get; set; } = "";
        public string SortCriteria { get; set; } = "";
    }

    private class BrowseResultItem
    {
        public bool IsContainer { get; set; }
        public string Xml { get; set; } = "";
        public string Title { get; set; } = "";
        public int SortIndex { get; set; }
        public BaseItemDto? Item { get; set; }
    }
}