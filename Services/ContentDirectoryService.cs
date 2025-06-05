using System.Text;
using System.Xml.Linq;
using System.Linq;
using System.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Models;
using FinDLNA.Services;
using Jellyfin.Sdk.Generated.Models;
using FinDLNA.Utilities;

namespace FinDLNA.Services;

// MARK: Enhanced ContentDirectoryService
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
        BaseItemDto_Type.AggregateFolder,
        BaseItemDto_Type.CollectionFolder,
        BaseItemDto_Type.BoxSet,
        BaseItemDto_Type.Folder,
        BaseItemDto_Type.UserView,
        BaseItemDto_Type.Series,
        BaseItemDto_Type.Season,
        BaseItemDto_Type.MusicAlbum,
        BaseItemDto_Type.MusicArtist,
        BaseItemDto_Type.Playlist
    };

    private static readonly HashSet<BaseItemDto_Type> MediaTypes = new()
    {
        BaseItemDto_Type.Movie,
        BaseItemDto_Type.Episode,
        BaseItemDto_Type.Audio,
        BaseItemDto_Type.Photo,
        BaseItemDto_Type.Video,
        BaseItemDto_Type.MusicVideo,
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
            
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["ObjectId"] = browseParams.ObjectId,
                ["BrowseFlag"] = browseParams.BrowseFlag,
                ["StartingIndex"] = browseParams.StartingIndex,
                ["RequestedCount"] = browseParams.RequestedCount,
                ["DeviceProfile"] = deviceProfile?.Name ?? "Generic"
            });

            _logger.LogInformation("Processing browse request");

            BrowseResult result;
            
            if (browseParams.BrowseFlag == "BrowseMetadata")
            {
                result = await ProcessBrowseMetadataAsync(browseParams.ObjectId, deviceProfile);
            }
            else
            {
                result = await ProcessBrowseChildrenAsync(browseParams, deviceProfile);
            }

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

            if (browseElement == null)
                return null;

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
        _logger.LogDebug("Processing BrowseMetadata for ObjectID: {ObjectId}", objectId);
        
        if (objectId == "0")
        {
            return await CreateRootMetadataResponse(deviceProfile);
        }

        if (objectId.StartsWith("library:"))
        {
            return await CreateLibraryMetadataResponse(objectId, deviceProfile);
        }

        if (Guid.TryParse(objectId, out var itemGuid))
        {
            return await CreateItemMetadataResponse(itemGuid, deviceProfile);
        }

        return CreateEmptyResult();
    }

    // MARK: ProcessBrowseChildrenAsync
    private async Task<BrowseResult> ProcessBrowseChildrenAsync(BrowseRequestParams request, DeviceProfile? deviceProfile)
    {
        if (request.ObjectId == "0")
        {
            return await BrowseRootAsync(deviceProfile);
        }

        if (request.ObjectId.StartsWith("library:"))
        {
            var libraryId = request.ObjectId.Substring(8);
            if (Guid.TryParse(libraryId, out var libGuid))
            {
                return await BrowseLibraryAsync(libGuid, request, deviceProfile);
            }
        }

        if (Guid.TryParse(request.ObjectId, out var itemGuid))
        {
            return await BrowseItemAsync(itemGuid, request, deviceProfile);
        }

        _logger.LogWarning("Unknown object ID format: {ObjectId}", request.ObjectId);
        return CreateEmptyResult();
    }

    // MARK: CreateRootMetadataResponse
    private async Task<BrowseResult> CreateRootMetadataResponse(DeviceProfile? deviceProfile)
    {
        var childCount = await GetRootChildCountAsync();
        var containerXml = CreateContainerXml(
            "0",
            "-1",
            "FinDLNA Media Server",
            "object.container.storageFolder",
            childCount,
            null,
            deviceProfile
        );
        
        var didlXml = CreateDidlXml(containerXml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    // MARK: CreateLibraryMetadataResponse
    private async Task<BrowseResult> CreateLibraryMetadataResponse(string objectId, DeviceProfile? deviceProfile)
    {
        var libraryId = objectId.Substring(8);
        if (!Guid.TryParse(libraryId, out var libGuid))
        {
            return CreateEmptyResult();
        }

        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        var library = libraries?.FirstOrDefault(l => l.Id == libGuid);
        
        if (library == null)
        {
            return CreateEmptyResult();
        }

        var childCount = await GetLibraryChildCountAsync(libGuid);
        var containerXml = CreateContainerXml(
            objectId,
            "0",
            library.Name ?? "Unknown",
            GetLibraryUpnpClass(library),
            childCount,
            library.Id,
            deviceProfile
        );
        
        var didlXml = CreateDidlXml(containerXml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    // MARK: CreateItemMetadataResponse
    private async Task<BrowseResult> CreateItemMetadataResponse(Guid itemGuid, DeviceProfile? deviceProfile)
    {
        var item = await _jellyfinService.GetItemAsync(itemGuid);
        if (item == null)
        {
            return CreateEmptyResult();
        }

        string xml;
        if (ContainerTypes.Contains(item.Type ?? BaseItemDto_Type.Folder))
        {
            var childCount = await GetItemChildCountAsync(itemGuid);
            xml = CreateContainerXml(
                itemGuid.ToString(),
                GetParentId(item),
                item.Name ?? "Unknown",
                GetUpnpClass(item.Type ?? BaseItemDto_Type.Folder),
                childCount,
                item.Id,
                deviceProfile
            );
        }
        else
        {
            xml = CreateItemXml(item, GetParentId(item), deviceProfile);
        }
        
        var didlXml = CreateDidlXml(xml);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
    }

    // MARK: BrowseRootAsync
    private async Task<BrowseResult> BrowseRootAsync(DeviceProfile? deviceProfile = null)
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        if (libraries == null || !libraries.Any())
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
                
                _logger.LogDebug("Added library {LibraryName} with {ChildCount} items", 
                    library.Name, childCount);
            }
            else
            {
                _logger.LogDebug("Skipped empty library {LibraryName}", library.Name);
            }
        }

        var didlXml = CreateDidlXml(string.Join("", containers));
        _logger.LogInformation("Returning {Count} libraries", containers.Count);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = containers.Count, TotalMatches = containers.Count };
    }

    // MARK: BrowseLibraryAsync
    private async Task<BrowseResult> BrowseLibraryAsync(Guid libraryId, BrowseRequestParams request, DeviceProfile? deviceProfile = null)
    {
        _logger.LogInformation("Browsing library {LibraryId}", libraryId);
        
        var items = await _jellyfinService.GetLibraryContentAsync(libraryId);
        if (items == null || !items.Any())
        {
            _logger.LogWarning("No content found for library {LibraryId}", libraryId);
            return CreateEmptyResult();
        }

        var allResults = await ProcessItemsForBrowsing(items, $"library:{libraryId}", deviceProfile);
        var sortedResults = ApplySorting(allResults, request.SortCriteria, deviceProfile);
        
        return CreatePaginatedResult(sortedResults, request.StartingIndex, request.RequestedCount);
    }

    // MARK: BrowseItemAsync
    private async Task<BrowseResult> BrowseItemAsync(Guid itemId, BrowseRequestParams request, DeviceProfile? deviceProfile = null)
    {
        _logger.LogInformation("Browsing item {ItemId}", itemId);
        
        var items = await _jellyfinService.GetItemsAsync(itemId);
        if (items == null || !items.Any())
        {
            _logger.LogWarning("No content found for item {ItemId}", itemId);
            return CreateEmptyResult();
        }

        var allResults = await ProcessItemsForBrowsing(items, itemId.ToString(), deviceProfile);
        var sortedResults = ApplySorting(allResults, request.SortCriteria, deviceProfile);
        
        return CreatePaginatedResult(sortedResults, request.StartingIndex, request.RequestedCount);
    }

    // MARK: ProcessItemsForBrowsing
    private async Task<List<BrowseResultItem>> ProcessItemsForBrowsing(IReadOnlyList<BaseItemDto> items, string parentId, DeviceProfile? deviceProfile)
    {
        var results = new List<BrowseResultItem>();

        foreach (var item in items)
        {
            if (!IsItemIncluded(item))
                continue;

            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            if (ContainerTypes.Contains(itemType))
            {
                var childCount = await GetItemChildCountAsync(item.Id!.Value);
                if (childCount > 0 || IsAlwaysVisibleContainer(item))
                {
                    var containerXml = CreateContainerXml(
                        item.Id.Value.ToString(),
                        parentId,
                        itemName,
                        GetUpnpClass(itemType),
                        childCount,
                        item.Id.Value,
                        deviceProfile
                    );
                    results.Add(new BrowseResultItem
                    {
                        IsContainer = true,
                        Xml = containerXml,
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

    // MARK: ApplySorting
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
            {
                return items.OrderBy(item => item.Title).ToList();
            }
            if (sortCriteria.Contains("dc:date"))
            {
                return items.OrderBy(item => item.Item?.DateCreated ?? DateTime.MinValue).ToList();
            }
        }

        return items.OrderBy(item => item.IsContainer ? 0 : 1)
                   .ThenBy(item => item.SortIndex)
                   .ThenBy(item => item.Title)
                   .ToList();
    }

    // MARK: CreatePaginatedResult
    private BrowseResult CreatePaginatedResult(List<BrowseResultItem> sortedResults, int startingIndex, int requestedCount)
    {
        var totalMatches = sortedResults.Count;
        var paginatedResults = sortedResults
            .Skip(startingIndex)
            .Take(requestedCount > 0 ? requestedCount : int.MaxValue)
            .Select(x => x.Xml)
            .ToList();

        var didlXml = CreateDidlXml(string.Join("", paginatedResults));
        return new BrowseResult 
        { 
            DidlXml = didlXml, 
            NumberReturned = paginatedResults.Count, 
            TotalMatches = totalMatches 
        };
    }

    // MARK: IsItemIncluded
    private bool IsItemIncluded(BaseItemDto item)
    {
        if (!item.Id.HasValue)
            return false;

        if (ExcludedFolderNames.Contains(item.Name ?? ""))
            return false;

        var itemType = item.Type ?? BaseItemDto_Type.Folder;
        return ContainerTypes.Contains(itemType) || MediaTypes.Contains(itemType);
    }

    // MARK: IsAlwaysVisibleLibrary
    private bool IsAlwaysVisibleLibrary(BaseItemDto library)
    {
        var collectionType = library.CollectionType;
        return collectionType == BaseItemDto_CollectionType.Movies ||
               collectionType == BaseItemDto_CollectionType.Tvshows ||
               collectionType == BaseItemDto_CollectionType.Music ||
               collectionType == BaseItemDto_CollectionType.Photos;
    }

    // MARK: IsAlwaysVisibleContainer
    private bool IsAlwaysVisibleContainer(BaseItemDto item)
    {
        var itemType = item.Type ?? BaseItemDto_Type.Folder;
        return itemType == BaseItemDto_Type.Series ||
               itemType == BaseItemDto_Type.Season ||
               itemType == BaseItemDto_Type.MusicAlbum ||
               itemType == BaseItemDto_Type.MusicArtist;
    }

    // MARK: GetLibraryChildCountAsync
    private async Task<int> GetLibraryChildCountAsync(Guid libraryId)
    {
        try
        {
            var items = await _jellyfinService.GetLibraryContentAsync(libraryId);
            if (items == null) return 0;

            var count = items.Count(IsItemIncluded);
            _logger.LogTrace("Library {LibraryId} has {Count} valid items", libraryId, count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting child count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    // MARK: GetItemChildCountAsync
    private async Task<int> GetItemChildCountAsync(Guid itemId)
    {
        try
        {
            var items = await _jellyfinService.GetItemsAsync(itemId);
            if (items == null) return 0;

            var count = items.Count(IsItemIncluded);
            _logger.LogTrace("Item {ItemId} has {Count} valid children", itemId, count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting child count for item {ItemId}", itemId);
            return 0;
        }
    }

    // MARK: GetRootChildCountAsync
    private async Task<int> GetRootChildCountAsync()
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        return libraries?.Count ?? 0;
    }

    // MARK: CreateItemXml
    private string CreateItemXml(BaseItemDto item, string parentId, DeviceProfile? deviceProfile = null)
    {
        if (!item.Id.HasValue) 
        {
            _logger.LogWarning("CreateItemXml: Item has no ID");
            return "";
        }

        var runTimeTicks = GetItemDuration(item);
        var streamUrl = GetProxyStreamUrl(item.Id.Value);
        
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("CreateItemXml: No stream URL for item {ItemId}", item.Id.Value);
            return "";
        }

        var mimeType = GetMimeTypeFromItem(item, deviceProfile);
        var upnpClass = GetUpnpClass(item.Type ?? BaseItemDto_Type.Video);
        var duration = FormatDurationForDlna(runTimeTicks);
        var size = EstimateFileSize(runTimeTicks);
        var resolution = GetResolution(item);

        var title = SecurityElement.Escape(GetDisplayTitle(item));
        var albumArtUrl = GetAlbumArtUrl(item.Id.Value);
        var additionalMetadata = BuildEnhancedMetadata(item, albumArtUrl, deviceProfile);
        
        var attributes = BuildResourceAttributes(duration, resolution, size, item, deviceProfile);
        var dlnaFlags = GetDlnaFlags(item, deviceProfile);
        var protocolInfo = $"http-get:*:{mimeType}:{dlnaFlags}";
        
        _logger.LogTrace("Creating DLNA item {Title} for {ItemId}", title, item.Id.Value);
        
        return _xmlTemplateService.GetTemplate("ItemTemplate",
            item.Id.Value,           // {0} - id
            parentId,                // {1} - parentID
            title,                   // {2} - title
            DateTime.UtcNow.ToString("yyyy-MM-dd"), // {3} - date
            upnpClass,               // {4} - upnp:class
            additionalMetadata,      // {5} - additional metadata
            protocolInfo,            // {6} - protocolInfo
            attributes,              // {7} - res attributes
            streamUrl                // {8} - stream URL
        );
    }

    // MARK: GetItemDuration
    private long? GetItemDuration(BaseItemDto item)
    {
        var runTimeTicks = item.RunTimeTicks;
        var mediaSource = item.MediaSources?.FirstOrDefault();
        
        if ((!runTimeTicks.HasValue || runTimeTicks.Value == 0) && mediaSource?.RunTimeTicks.HasValue == true)
        {
            runTimeTicks = mediaSource.RunTimeTicks;
            _logger.LogTrace("Using media source duration for item {ItemId}: {Duration} ticks", 
                item.Id?.ToString() ?? "unknown", runTimeTicks);
        }

        return runTimeTicks;
    }

    // MARK: BuildResourceAttributes
    private string BuildResourceAttributes(string? duration, string? resolution, long size, BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var attributes = new List<string>();
        
        if (size > 0)
            attributes.Add($"size=\"{size}\"");
            
        if (!string.IsNullOrEmpty(duration))
            attributes.Add($"duration=\"{duration}\"");
            
        if (!string.IsNullOrEmpty(resolution))
            attributes.Add($"resolution=\"{resolution}\"");
            
        var bitrate = EstimateBitrate(item, deviceProfile);
        attributes.Add($"bitrate=\"{bitrate}\"");
        
        return string.Join(" ", attributes);
    }

    // ... [Rest of the helper methods remain the same as in your original code]

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

    // MARK: GetDisplayTitle
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

    // MARK: EstimateBitrate
    private long EstimateBitrate(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var mediaSource = item.MediaSources?.FirstOrDefault();
        var actualBitrate = mediaSource?.Bitrate ?? 8000000;

        if (deviceProfile == null) return actualBitrate;

        var maxBitrate = deviceProfile.MaxStreamingBitrate ?? 120000000;
        if (actualBitrate > maxBitrate)
        {
            _logger.LogDebug("Capping bitrate from {ActualBitrate} to {MaxBitrate} for device {DeviceName}", 
                actualBitrate, maxBitrate, deviceProfile.Name);
            return maxBitrate;
        }

        return actualBitrate;
    }

    // MARK: GetDlnaFlags
    private string GetDlnaFlags(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var isVideo = item.Type != BaseItemDto_Type.Audio;
        
        var flags = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        
        if (deviceProfile?.Name?.Contains("Samsung") == true)
        {
            flags = isVideo ? 
                "DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000" :
                "DLNA.ORG_PN=MP3;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        }
        else if (deviceProfile?.Name?.Contains("Xbox") == true)
        {
            flags = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01500000000000000000000000000000";
        }
        
        return flags;
    }

    // MARK: GetMimeTypeFromItem
    private string GetMimeTypeFromItem(BaseItemDto item, DeviceProfile? deviceProfile = null)
    {
        var defaultMimeType = item.Type switch
        {
            BaseItemDto_Type.Audio => "audio/mpeg",
            BaseItemDto_Type.Photo => "image/jpeg",
            _ => "video/mp4"
        };

        if (deviceProfile == null) return defaultMimeType;

        if (deviceProfile.Name?.Contains("Samsung") == true && item.Type != BaseItemDto_Type.Audio)
        {
            return "video/mp4";
        }
        
        if (deviceProfile.Name?.Contains("LG") == true && item.Type != BaseItemDto_Type.Audio)
        {
            return "video/mp4";
        }

        return defaultMimeType;
    }

    // MARK: BuildEnhancedMetadata
    private string BuildEnhancedMetadata(BaseItemDto item, string? albumArtUrl, DeviceProfile? deviceProfile)
    {
        var metadata = new StringBuilder();
        
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
        
        if (item.Type == BaseItemDto_Type.Episode)
        {
            if (item.IndexNumber.HasValue)
            {
                metadata.AppendLine($"<upnp:episodeNumber>{item.IndexNumber}</upnp:episodeNumber>");
            }
            if (item.ParentIndexNumber.HasValue)
            {
                metadata.AppendLine($"<upnp:episodeSeason>{item.ParentIndexNumber}</upnp:episodeSeason>");
            }
            if (!string.IsNullOrEmpty(item.SeriesName))
            {
                metadata.AppendLine($"<upnp:seriesTitle>{SecurityElement.Escape(item.SeriesName)}</upnp:seriesTitle>");
            }
        }
        
        if (item.Type == BaseItemDto_Type.Audio)
        {
            if (!string.IsNullOrEmpty(item.Album))
            {
                metadata.AppendLine($"<upnp:album>{SecurityElement.Escape(item.Album)}</upnp:album>");
            }
            if (item.Artists?.Any() == true)
            {
                foreach (var artist in item.Artists.Take(3))
                {
                    metadata.AppendLine($"<upnp:artist>{SecurityElement.Escape(artist)}</upnp:artist>");
                }
            }
            if (item.IndexNumber.HasValue)
            {
                metadata.AppendLine($"<upnp:originalTrackNumber>{item.IndexNumber}</upnp:originalTrackNumber>");
            }
        }
        
        var genres = item.Genres?.Take(2);
        if (genres?.Any() == true)
        {
            foreach (var genre in genres)
            {
                metadata.AppendLine($"<upnp:genre>{SecurityElement.Escape(genre)}</upnp:genre>");
            }
        }
        
        if (item.CommunityRating.HasValue)
        {
            var rating = Math.Round(item.CommunityRating.Value, 1);
            metadata.AppendLine($"<upnp:rating>{rating}</upnp:rating>");
        }
        
        return metadata.ToString().TrimEnd();
    }

    // MARK: FormatDurationForDlna
    private string FormatDurationForDlna(long? runTimeTicks)
    {
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
            return "0:00:00.000";

        var timeSpan = TimeSpan.FromTicks(runTimeTicks.Value);
        return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}.{timeSpan.Milliseconds:000}";
    }

    // MARK: EstimateFileSize
    private long EstimateFileSize(long? runTimeTicks)
    {
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
            return 0;

        var durationSeconds = TimeConversionUtil.TicksToSeconds(runTimeTicks.Value);
        var estimatedBitrate = 8000000;
        return (long)(durationSeconds * estimatedBitrate / 8);
    }

    // MARK: GetProxyStreamUrl
    private string GetProxyStreamUrl(Guid itemId)
    {
        var localIp = GetLocalIPAddress();
        var dlnaPort = _configuration["Dlna:Port"] ?? "8200";
        var streamUrl = $"http://{localIp}:{dlnaPort}/stream/{itemId}";
        
        _logger.LogTrace("Generated proxy stream URL for item {ItemId}: {StreamUrl}", itemId, streamUrl);
        return streamUrl;
    }

    // MARK: GetLocalIPAddress
    private string GetLocalIPAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get local IP address, using loopback");
        }
        
        return "127.0.0.1";
    }

    // MARK: CreateContainerXml
    private string CreateContainerXml(string id, string parentId, string title, string upnpClass, int childCount, Guid? itemId = null, DeviceProfile? deviceProfile = null)
    {
        var escapedTitle = SecurityElement.Escape(title);
        var albumArtUrl = itemId.HasValue ? GetAlbumArtUrl(itemId.Value) : "";
        
        var additionalMetadata = new StringBuilder();
        
        if (!string.IsNullOrEmpty(albumArtUrl))
        {
            additionalMetadata.AppendLine($"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>");
            
            if (deviceProfile?.Name?.Contains("Samsung") == true)
            {
                additionalMetadata.AppendLine($"<upnp:icon>{albumArtUrl}</upnp:icon>");
                additionalMetadata.AppendLine("<sec:dcmInfo>CREATIONDATE=0,FOLDER=1</sec:dcmInfo>");
            }
        }

        _logger.LogTrace("Creating container {Id} with {ChildCount} children", id, childCount);

        return _xmlTemplateService.GetTemplate("ContainerTemplate",
            id,                               // {0} - id
            parentId,                         // {1} - parentID
            childCount,                       // {2} - childCount
            escapedTitle,                     // {3} - title
            upnpClass,                        // {4} - upnp:class
            additionalMetadata.ToString()     // {5} - additional metadata
        );
    }

    // MARK: GetAlbumArtUrl
    private string GetAlbumArtUrl(Guid itemId)
    {
        return _jellyfinService.GetImageUrlAsync(itemId, ImageType.Primary) ?? "";
    }

    // MARK: GetResolution
    private string GetResolution(BaseItemDto item)
    {
        var videoStream = item.MediaSources?.FirstOrDefault()?.MediaStreams?
            .FirstOrDefault(s => s.Type == MediaStream_Type.Video);

        if (videoStream?.Width.HasValue == true && videoStream?.Height.HasValue == true)
        {
            return $"{videoStream.Width}x{videoStream.Height}";
        }

        return "";
    }

    // MARK: CreateDidlXml
    private string CreateDidlXml(string content)
    {
        return _xmlTemplateService.GetTemplate("DidlLiteTemplate", content);
    }

    // MARK: GetUpnpClass
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

    // MARK: GetLibraryUpnpClass
    private string GetLibraryUpnpClass(BaseItemDto library)
    {
        var collectionType = library.CollectionType;
        
        return collectionType switch
        {
            BaseItemDto_CollectionType.Movies => "object.container.genre.movieGenre",
            BaseItemDto_CollectionType.Tvshows => "object.container.genre.movieGenre", 
            BaseItemDto_CollectionType.Music => "object.container.storageFolder",
            BaseItemDto_CollectionType.Photos => "object.container.album.photoAlbum",
            BaseItemDto_CollectionType.Books => "object.container.storageFolder",
            _ => "object.container.storageFolder"
        };
    }

    // MARK: GetParentId
    private string GetParentId(BaseItemDto item)
    {
        if (item.ParentId.HasValue)
        {
            var libraries = _jellyfinService.GetLibraryFoldersAsync().Result;
            var isLibraryFolder = libraries?.Any(l => l.Id == item.ParentId.Value) == true;
            
            if (isLibraryFolder)
            {
                return $"library:{item.ParentId.Value}";
            }
            
            return item.ParentId.Value.ToString();
        }
        return "0";
    }

    // MARK: CreateBrowseResponse
    private string CreateBrowseResponse(string result, int numberReturned, int totalMatches)
    {
        var escapedResult = SecurityElement.Escape(result);
        var response = _xmlTemplateService.GetTemplate("BrowseResponse", escapedResult, numberReturned, totalMatches);
        
        _logger.LogDebug("Created browse response with {NumberReturned} items, {TotalMatches} total", 
            numberReturned, totalMatches);
        
        return response;
    }

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        var escapedError = SecurityElement.Escape(error);
        return _xmlTemplateService.GetTemplate("SoapFault", escapedError);
    }

    // MARK: ProcessSearchCapabilitiesRequest
    public string ProcessSearchCapabilitiesRequest()
    {
        return _xmlTemplateService.GetTemplate("SearchCapabilitiesResponse", "");
    }

    // MARK: ProcessSortCapabilitiesRequest
    public string ProcessSortCapabilitiesRequest()
    {
        return _xmlTemplateService.GetTemplate("SortCapabilitiesResponse", "dc:title,dc:date,upnp:class");
    }

    // MARK: CreateEmptyResult
    private BrowseResult CreateEmptyResult()
    {
        var didlXml = CreateDidlXml("");
        return new BrowseResult { DidlXml = didlXml, NumberReturned = 0, TotalMatches = 0 };
    }
}