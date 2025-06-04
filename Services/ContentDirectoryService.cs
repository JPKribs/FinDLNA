using System.Text;
using System.Xml.Linq;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Models;
using FinDLNA.Services;
using Jellyfin.Sdk.Generated.Models;
using FinDLNA.Utilities;

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
        "Featurettes", "Extras", "Trailers", "Theme Videos", "Theme Songs", "Specials"
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
            var doc = XDocument.Parse(soapBody);
            var ns = XNamespace.Get("urn:schemas-upnp-org:service:ContentDirectory:1");
            var browseElement = doc.Descendants(ns + "Browse").FirstOrDefault();

            if (browseElement == null)
            {
                _logger.LogWarning("No Browse element found in SOAP request");
                return CreateSoapFault("Invalid Browse request");
            }

            var objectId = browseElement.Element("ObjectID")?.Value ?? "0";
            var browseFlag = browseElement.Element("BrowseFlag")?.Value ?? "BrowseDirectChildren";
            var startingIndex = int.Parse(browseElement.Element("StartingIndex")?.Value ?? "0");
            var requestedCount = int.Parse(browseElement.Element("RequestedCount")?.Value ?? "0");

            _logger.LogInformation("Browse request: ObjectID={ObjectId}, Flag={Flag}, Start={Start}, Count={Count}, UserAgent={UserAgent}",
                objectId, browseFlag, startingIndex, requestedCount, userAgent);

            BrowseResult result;
            
            if (browseFlag == "BrowseMetadata")
            {
                result = await ProcessBrowseMetadataAsync(objectId, userAgent);
            }
            else
            {
                result = await ProcessBrowseAsync(objectId, browseFlag, startingIndex, requestedCount, userAgent);
            }

            _logger.LogInformation("Browse result: {NumberReturned} items returned, {TotalMatches} total matches for ObjectID={ObjectId}",
                result.NumberReturned, result.TotalMatches, objectId);

            return CreateBrowseResponse(result.DidlXml, result.NumberReturned, result.TotalMatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing browse request.");
            return CreateSoapFault("Internal server error");
        }
    }

    // MARK: ProcessBrowseMetadataAsync
    private async Task<BrowseResult> ProcessBrowseMetadataAsync(string objectId, string? userAgent)
    {
        _logger.LogInformation("Processing BrowseMetadata for ObjectID: {ObjectId}", objectId);
        
        if (objectId == "0")
        {
            var rootContainer = CreateContainerXml(
                "0",
                "-1",
                "FinDLNA Media Server",
                "object.container.storageFolder",
                await GetRootChildCountAsync(),
                null
            );
            
            var didlXml = CreateDidlXml(rootContainer);
            return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
        }

        if (objectId.StartsWith("library:"))
        {
            var libraryId = objectId.Substring(8);
            if (Guid.TryParse(libraryId, out var libGuid))
            {
                var libraries = await _jellyfinService.GetLibraryFoldersAsync();
                var library = libraries?.FirstOrDefault(l => l.Id == libGuid);
                
                if (library != null)
                {
                    var container = CreateContainerXml(
                        objectId,
                        "0",
                        library.Name ?? "Unknown",
                        GetLibraryUpnpClass(library),
                        await GetLibraryChildCountAsync(libGuid),
                        library.Id
                    );
                    
                    var didlXml = CreateDidlXml(container);
                    return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
                }
            }
        }

        if (Guid.TryParse(objectId, out var itemGuid))
        {
            var item = await _jellyfinService.GetItemAsync(itemGuid);
            if (item != null)
            {
                string xml;
                if (ContainerTypes.Contains(item.Type ?? BaseItemDto_Type.Folder))
                {
                    xml = CreateContainerXml(
                        objectId,
                        GetParentId(item),
                        item.Name ?? "Unknown",
                        GetUpnpClass(item.Type ?? BaseItemDto_Type.Folder),
                        await GetItemChildCountAsync(itemGuid),
                        item.Id
                    );
                }
                else
                {
                    xml = CreateItemXml(item, GetParentId(item));
                }
                
                var didlXml = CreateDidlXml(xml);
                return new BrowseResult { DidlXml = didlXml, NumberReturned = 1, TotalMatches = 1 };
            }
        }

        var didlEmpty = CreateDidlXml("");
        return new BrowseResult { DidlXml = didlEmpty, NumberReturned = 0, TotalMatches = 0 };
    }

    // MARK: GetLibraryChildCountAsync
    private async Task<int> GetLibraryChildCountAsync(Guid libraryId)
    {
        try
        {
            var items = await _jellyfinService.GetItemsAsync(libraryId);
            if (items == null) return 0;

            var count = items.Count(item => 
                item.Id.HasValue && 
                !ExcludedFolderNames.Contains(item.Name ?? "") &&
                (ContainerTypes.Contains(item.Type ?? BaseItemDto_Type.Folder) || 
                 MediaTypes.Contains(item.Type ?? BaseItemDto_Type.Folder)));

            _logger.LogDebug("Library child count: Library {LibraryId} has {Count} items", libraryId, count);
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

            var count = items.Count(item => 
                item.Id.HasValue && 
                !ExcludedFolderNames.Contains(item.Name ?? "") &&
                (ContainerTypes.Contains(item.Type ?? BaseItemDto_Type.Folder) || 
                 MediaTypes.Contains(item.Type ?? BaseItemDto_Type.Folder)));

            _logger.LogDebug("Item child count: Item {ItemId} has {Count} children", itemId, count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting child count for item {ItemId}", itemId);
            return 0;
        }
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

    // MARK: GetRootChildCountAsync
    private async Task<int> GetRootChildCountAsync()
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        return libraries?.Count ?? 0;
    }

    // MARK: ProcessBrowseAsync
    private async Task<BrowseResult> ProcessBrowseAsync(string objectId, string browseFlag, int startingIndex, int requestedCount, string? userAgent)
    {
        if (objectId == "0")
        {
            return await BrowseRootAsync();
        }

        if (objectId.StartsWith("library:"))
        {
            var libraryId = objectId.Substring(8);
            if (Guid.TryParse(libraryId, out var libGuid))
            {
                return await BrowseLibraryAsync(libGuid, startingIndex, requestedCount);
            }
        }

        if (Guid.TryParse(objectId, out var itemGuid))
        {
            return await BrowseItemAsync(itemGuid, startingIndex, requestedCount);
        }

        _logger.LogWarning("Unknown object ID format: {ObjectId}", objectId);
        return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
    }

    // MARK: BrowseRootAsync
    private async Task<BrowseResult> BrowseRootAsync()
    {
        var libraries = await _jellyfinService.GetLibraryFoldersAsync();
        if (libraries == null || !libraries.Any())
        {
            _logger.LogWarning("No library folders found");
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var containers = new List<string>();
        foreach (var library in libraries)
        {
            if (library.Id.HasValue)
            {
                var childCount = await GetLibraryChildCountAsync(library.Id.Value);
                var container = CreateContainerXml(
                    $"library:{library.Id.Value}",
                    "0",
                    library.Name ?? "Unknown",
                    GetLibraryUpnpClass(library),
                    childCount,
                    library.Id.Value
                );
                containers.Add(container);
                
                _logger.LogInformation("Added library {LibraryName} with {ChildCount} items", 
                    library.Name, childCount);
            }
        }

        var didlXml = CreateDidlXml(string.Join("", containers));
        _logger.LogInformation("Returning {Count} libraries", containers.Count);
        return new BrowseResult { DidlXml = didlXml, NumberReturned = containers.Count, TotalMatches = containers.Count };
    }

    // MARK: BrowseLibraryAsync
    private async Task<BrowseResult> BrowseLibraryAsync(Guid libraryId, int startingIndex, int requestedCount)
    {
        _logger.LogInformation("Browsing library {LibraryId}", libraryId);
        
        var items = await _jellyfinService.GetLibraryContentAsync(libraryId);
        if (items == null || !items.Any())
        {
            _logger.LogWarning("No content found for library {LibraryId}", libraryId);
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var allResults = new List<(bool isContainer, string xml, string title, int sortIndex)>();

        foreach (var item in items)
        {
            if (!item.Id.HasValue || ExcludedFolderNames.Contains(item.Name ?? ""))
                continue;

            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            _logger.LogTrace("Processing item {Name} (Type: {Type})", itemName, itemType);

            if (ContainerTypes.Contains(itemType))
            {
                var childCount = await GetItemChildCountAsync(item.Id.Value);
                var containerXml = CreateContainerXml(
                    item.Id.Value.ToString(),
                    $"library:{libraryId}",
                    itemName,
                    GetUpnpClass(itemType),
                    childCount,
                    item.Id.Value
                );
                allResults.Add((true, containerXml, itemName, item.IndexNumber ?? int.MaxValue));
                _logger.LogDebug("Added container {Name} with {ChildCount} children", itemName, childCount);
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, $"library:{libraryId}");
                if (!string.IsNullOrEmpty(itemXml))
                {
                    allResults.Add((false, itemXml, itemName, item.IndexNumber ?? int.MaxValue));
                    _logger.LogDebug("Added media item {Name}", itemName);
                }
                else
                {
                    _logger.LogWarning("Failed to create XML for media item {Name} (Type: {Type})", itemName, itemType);
                }
            }
            else
            {
                _logger.LogTrace("Skipped item {Name} (Type: {Type}) - not container or media", itemName, itemType);
            }
        }

        _logger.LogInformation("Found {Total} valid items ({Containers} containers, {Media} media) in library {LibraryId}",
            allResults.Count, allResults.Count(x => x.isContainer), allResults.Count(x => !x.isContainer), libraryId);

        var sortedResults = allResults
            .OrderBy(x => !x.isContainer)
            .ThenBy(x => x.sortIndex)
            .ThenBy(x => x.title)
            .ToList();

        var totalMatches = sortedResults.Count;
        var paginatedResults = sortedResults
            .Skip(startingIndex)
            .Take(requestedCount > 0 ? requestedCount : int.MaxValue)
            .Select(x => x.xml)
            .ToList();

        var didlXml = CreateDidlXml(string.Join("", paginatedResults));
        return new BrowseResult { DidlXml = didlXml, NumberReturned = paginatedResults.Count, TotalMatches = totalMatches };
    }

    // MARK: BrowseItemAsync
    private async Task<BrowseResult> BrowseItemAsync(Guid itemId, int startingIndex, int requestedCount)
    {
        _logger.LogInformation("Browsing item {ItemId}", itemId);
        
        var items = await _jellyfinService.GetItemsAsync(itemId);
        if (items == null || !items.Any())
        {
            _logger.LogWarning("No content found for item {ItemId}", itemId);
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var allResults = new List<(BaseItemDto item, bool isContainer, string xml, string title, int sortIndex)>();

        foreach (var item in items)
        {
            if (!item.Id.HasValue || ExcludedFolderNames.Contains(item.Name ?? ""))
                continue;

            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            _logger.LogTrace("Processing child {Name} (Type: {Type})", itemName, itemType);

            if (ContainerTypes.Contains(itemType))
            {
                var childCount = await GetItemChildCountAsync(item.Id.Value);
                var containerXml = CreateContainerXml(
                    item.Id.Value.ToString(),
                    itemId.ToString(),
                    itemName,
                    GetUpnpClass(itemType),
                    childCount,
                    item.Id.Value
                );
                allResults.Add((item, true, containerXml, itemName, item.IndexNumber ?? int.MaxValue));
                _logger.LogDebug("Added container {Name} with {ChildCount} children", itemName, childCount);
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, itemId.ToString());
                if (!string.IsNullOrEmpty(itemXml))
                {
                    allResults.Add((item, false, itemXml, itemName, item.IndexNumber ?? int.MaxValue));
                    _logger.LogDebug("Added media item {Name}", itemName);
                }
                else
                {
                    _logger.LogWarning("Failed to create XML for media item {Name} (Type: {Type})", itemName, itemType);
                }
            }
            else
            {
                _logger.LogTrace("Skipped child {Name} (Type: {Type}) - not container or media", itemName, itemType);
            }
        }

        _logger.LogInformation("Found {Total} valid items ({Containers} containers, {Media} media) in item {ItemId}",
            allResults.Count, allResults.Count(x => x.isContainer), allResults.Count(x => !x.isContainer), itemId);

        var sortedResults = allResults
            .OrderBy(x => !x.isContainer)
            .ThenBy(x => (x.item.Type == BaseItemDto_Type.Episode || x.item.Type == BaseItemDto_Type.Season)
                ? x.sortIndex
                : int.MaxValue)
            .ThenBy(x => x.title)
            .ToList();

        var totalMatches = sortedResults.Count;
        var paginatedResults = sortedResults
            .Skip(startingIndex)
            .Take(requestedCount > 0 ? requestedCount : int.MaxValue)
            .Select(x => x.xml)
            .ToList();

        var didlXml = CreateDidlXml(string.Join("", paginatedResults));
        return new BrowseResult
        {
            DidlXml = didlXml,
            NumberReturned = paginatedResults.Count,
            TotalMatches = totalMatches
        };
    }

    // MARK: CreateItemXml
    private string CreateItemXml(BaseItemDto item, string parentId)
    {
        if (!item.Id.HasValue) 
        {
            _logger.LogWarning("CreateItemXml: Item has no ID");
            return "";
        }

        var runTimeTicks = item.RunTimeTicks;
        var mediaSource = item.MediaSources?.FirstOrDefault();
        
        if ((!runTimeTicks.HasValue || runTimeTicks.Value == 0) && mediaSource?.RunTimeTicks.HasValue == true)
        {
            runTimeTicks = mediaSource.RunTimeTicks;
            _logger.LogDebug("Using media source duration for item {ItemId}: {Duration} ticks", item.Id.Value, runTimeTicks);
        }

        var streamUrl = GetProxyStreamUrl(item.Id.Value);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("CreateItemXml: No stream URL for item {ItemId}", item.Id.Value);
            return "";
        }

        var mimeType = GetMimeTypeFromItem(item);
        var upnpClass = GetUpnpClass(item.Type ?? BaseItemDto_Type.Video);
        var duration = FormatDurationForDlna(runTimeTicks);
        var size = EstimateFileSize(runTimeTicks);
        var resolution = GetResolution(item);

        var title = System.Security.SecurityElement.Escape(
            item.Type == BaseItemDto_Type.Episode 
                ? $"{item.IndexNumber}. {item.Name ?? "Unknown"}" 
                : item.Name ?? "Unknown"
        );
        
        var albumArtUrl = GetAlbumArtUrl(item.Id.Value);
        var additionalMetadata = BuildEnhancedMetadata(item, albumArtUrl);
        
        var resolutionAttr = !string.IsNullOrEmpty(resolution) ? $" resolution=\"{resolution}\"" : "";
        var durationAttr = !string.IsNullOrEmpty(duration) ? $" duration=\"{duration}\"" : "";
        var sizeAttr = size > 0 ? $" size=\"{size}\"" : "";
        
        var bitrate = mediaSource?.Bitrate ?? 8000000;
        var bitrateAttr = $" bitrate=\"{bitrate}\"";
        
        var attributes = $"{sizeAttr}{durationAttr}{resolutionAttr}{bitrateAttr}";
        
        _logger.LogDebug("Creating DLNA item {Title} for {ItemId}", title, item.Id.Value);
        
        var dlnaFlags = "DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        var protocolInfo = $"http-get:*:{mimeType}:{dlnaFlags}";
        
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

    // MARK: BuildEnhancedMetadata
    private string BuildEnhancedMetadata(BaseItemDto item, string? albumArtUrl)
    {
        var metadata = new StringBuilder();
        
        if (!string.IsNullOrEmpty(albumArtUrl))
        {
            metadata.AppendLine($"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>");
        }
        
        if (!string.IsNullOrEmpty(item.Overview))
        {
            var description = item.Overview.Length > 200 ? item.Overview.Substring(0, 200) + "..." : item.Overview;
            metadata.AppendLine($"<dc:description>{System.Security.SecurityElement.Escape(description)}</dc:description>");
        }
        
        if (item.ProductionYear.HasValue)
        {
            metadata.AppendLine($"<dc:date>{item.ProductionYear}</dc:date>");
        }
        
        if (item.Type == BaseItemDto_Type.Episode && item.IndexNumber.HasValue)
        {
            metadata.AppendLine($"<upnp:episodeNumber>{item.IndexNumber}</upnp:episodeNumber>");
            if (item.ParentIndexNumber.HasValue)
            {
                metadata.AppendLine($"<upnp:episodeSeason>{item.ParentIndexNumber}</upnp:episodeSeason>");
            }
        }
        
        var genres = item.Genres?.Take(2);
        if (genres?.Any() == true)
        {
            foreach (var genre in genres)
            {
                metadata.AppendLine($"<upnp:genre>{System.Security.SecurityElement.Escape(genre)}</upnp:genre>");
            }
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
    private string CreateContainerXml(string id, string parentId, string title, string upnpClass, int childCount, Guid? itemId = null)
    {
        var escapedTitle = System.Security.SecurityElement.Escape(title);
        var albumArtUrl = itemId.HasValue ? GetAlbumArtUrl(itemId.Value) : "";
        
        var additionalMetadata = "";
        if (!string.IsNullOrEmpty(albumArtUrl))
        {
            additionalMetadata = $"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>";
        }

        _logger.LogTrace("Creating container {Id} with {ChildCount} children", id, childCount);

        return _xmlTemplateService.GetTemplate("ContainerTemplate",
            id,                 // {0} - id
            parentId,           // {1} - parentID
            childCount,         // {2} - childCount
            escapedTitle,       // {3} - title
            upnpClass,          // {4} - upnp:class
            additionalMetadata  // {5} - additional metadata
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

    // MARK: GetMimeTypeFromItem
    private string GetMimeTypeFromItem(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Audio => "audio/mpeg",
            BaseItemDto_Type.Photo => "image/jpeg",
            _ => "video/mp4"
        };
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

    // MARK: CreateBrowseResponse
    private string CreateBrowseResponse(string result, int numberReturned, int totalMatches)
    {
        var escapedResult = System.Security.SecurityElement.Escape(result);
        var response = _xmlTemplateService.GetTemplate("BrowseResponse", escapedResult, numberReturned, totalMatches);
        
        _logger.LogDebug("Created browse response with {NumberReturned} items, {TotalMatches} total", 
            numberReturned, totalMatches);
        
        return response;
    }

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        var escapedError = System.Security.SecurityElement.Escape(error);
        return _xmlTemplateService.GetTemplate("SoapFault", escapedError);
    }
}