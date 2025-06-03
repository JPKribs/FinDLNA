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

            _logger.LogDebug("Browse request: ObjectID={ObjectId}, Flag={Flag}, Start={Start}, Count={Count}",
                objectId, browseFlag, startingIndex, requestedCount);

            BrowseResult result;
            
            // MARK: Handle BrowseMetadata differently for Samsung TVs
            if (browseFlag == "BrowseMetadata")
            {
                result = await ProcessBrowseMetadataAsync(objectId, userAgent);
            }
            else
            {
                result = await ProcessBrowseAsync(objectId, browseFlag, startingIndex, requestedCount, userAgent);
            }

            return CreateBrowseResponse(result.DidlXml, result.NumberReturned, result.TotalMatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing browse request");
            return CreateSoapFault("Internal server error");
        }
    }

    // MARK: ProcessBrowseMetadataAsync
    private async Task<BrowseResult> ProcessBrowseMetadataAsync(string objectId, string? userAgent)
    {
        _logger.LogInformation("Processing BrowseMetadata for ObjectID: {ObjectId}", objectId);
        
        if (objectId == "0")
        {
            // MARK: For root metadata, return the MediaServer container itself
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

        // MARK: For other items, try to get their metadata
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
                        "object.container.storageFolder",
                        library.ChildCount ?? 0,
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
                        item.ChildCount ?? 0,
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

        // MARK: Fallback for unknown items
        var didlEmpty = CreateDidlXml("");
        return new BrowseResult { DidlXml = didlEmpty, NumberReturned = 0, TotalMatches = 0 };
    }

    // MARK: GetParentId
    private string GetParentId(BaseItemDto item)
    {
        // MARK: Determine parent ID based on item structure
        if (item.ParentId.HasValue)
        {
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
        if (libraries == null)
        {
            _logger.LogWarning("Failed to get library folders");
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var containers = new List<string>();
        foreach (var library in libraries)
        {
            if (library.Id.HasValue)
            {
                var container = CreateContainerXml(
                    $"library:{library.Id.Value}",
                    "0",
                    library.Name ?? "Unknown",
                    "object.container.storageFolder",
                    library.ChildCount ?? 0,
                    library.Id.Value
                );
                containers.Add(container);
            }
        }

        var didlXml = CreateDidlXml(string.Join("", containers));
        return new BrowseResult { DidlXml = didlXml, NumberReturned = containers.Count, TotalMatches = containers.Count };
    }

    // MARK: BrowseLibraryAsync
    private async Task<BrowseResult> BrowseLibraryAsync(Guid libraryId, int startingIndex, int requestedCount)
    {
        var items = await _jellyfinService.GetItemsAsync(libraryId);
        if (items == null)
        {
            _logger.LogWarning("Failed to get library content for {LibraryId}", libraryId);
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var allResults = new List<(bool isContainer, string xml, string title)>();

        foreach (var item in items)
        {
            if (!item.Id.HasValue || ExcludedFolderNames.Contains(item.Name ?? ""))
                continue;

            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            _logger.LogDebug("Processing library item: {Name} (Type: {Type})", itemName, itemType);

            if (ContainerTypes.Contains(itemType))
            {
                var containerXml = CreateContainerXml(
                    item.Id.Value.ToString(),
                    $"library:{libraryId}",
                    itemName,
                    GetUpnpClass(itemType),
                    item.ChildCount ?? 0,
                    item.Id.Value
                );
                allResults.Add((true, containerXml, itemName));
                _logger.LogDebug("Added library container: {Name}", itemName);
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, $"library:{libraryId}");
                if (!string.IsNullOrEmpty(itemXml))
                {
                    allResults.Add((false, itemXml, itemName));
                    _logger.LogDebug("Added library media item: {Name} (XML length: {Length})", itemName, itemXml.Length);
                }
                else
                {
                    _logger.LogWarning("Failed to create XML for library media item: {Name} (Type: {Type})", itemName, itemType);
                }
            }
            else
            {
                _logger.LogDebug("Skipped library item: {Name} (Type: {Type}) - not container or media", itemName, itemType);
            }
        }

        _logger.LogInformation("BrowseLibraryAsync for {LibraryId}: Found {Total} items ({Containers} containers, {Media} media)",
            libraryId, allResults.Count,
            allResults.Count(x => x.isContainer),
            allResults.Count(x => !x.isContainer));

        var sortedResults = allResults
            .OrderBy(x => !x.isContainer)
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
        var items = await _jellyfinService.GetItemsAsync(itemId);
        if (items == null)
        {
            _logger.LogWarning("Failed to get item content for {ItemId}", itemId);
            return new BrowseResult { DidlXml = CreateDidlXml(""), NumberReturned = 0, TotalMatches = 0 };
        }

        var allResults = new List<(BaseItemDto item, bool isContainer, string xml, string title)>();

        foreach (var item in items)
        {
            if (!item.Id.HasValue || ExcludedFolderNames.Contains(item.Name ?? ""))
                continue;

            var itemType = item.Type ?? BaseItemDto_Type.Folder;
            var itemName = item.Name ?? "Unknown";

            _logger.LogDebug("Processing item: {Name} (Type: {Type})", itemName, itemType);

            if (ContainerTypes.Contains(itemType))
            {
                var containerXml = CreateContainerXml(
                    item.Id.Value.ToString(),
                    itemId.ToString(),
                    itemName,
                    GetUpnpClass(itemType),
                    item.ChildCount ?? 0,
                    item.Id.Value
                );
                allResults.Add((item, true, containerXml, itemName));
                _logger.LogDebug("Added container: {Name}", itemName);
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, itemId.ToString());
                if (!string.IsNullOrEmpty(itemXml))
                {
                    allResults.Add((item, false, itemXml, itemName));
                    _logger.LogDebug("Added media item: {Name} (XML length: {Length})", itemName, itemXml.Length);
                }
                else
                {
                    _logger.LogWarning("Failed to create XML for media item: {Name} (Type: {Type})", itemName, itemType);
                }
            }
            else
            {
                _logger.LogDebug("Skipped item: {Name} (Type: {Type}) - not container or media", itemName, itemType);
            }
        }

        _logger.LogInformation("BrowseItemAsync for {ItemId}: Found {Total} items ({Containers} containers, {Media} media)",
            itemId, allResults.Count,
            allResults.Count(x => x.isContainer),
            allResults.Count(x => !x.isContainer));

        var sortedResults = allResults
            .OrderBy(x => !x.isContainer)
            .ThenBy(x => (x.item.Type == BaseItemDto_Type.Episode || x.item.Type == BaseItemDto_Type.Season)
                ? x.item.IndexNumber ?? int.MaxValue
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
        var albumArtXml = !string.IsNullOrEmpty(albumArtUrl) ? 
            $"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>" : "";
        
        var resolutionAttr = !string.IsNullOrEmpty(resolution) ? $" resolution=\"{resolution}\"" : "";
        var durationAttr = !string.IsNullOrEmpty(duration) ? $" duration=\"{duration}\"" : "";
        var sizeAttr = size > 0 ? $" size=\"{size}\"" : "";
        
        var bitrate = mediaSource?.Bitrate ?? 8000000;
        var bitrateAttr = $" bitrate=\"{bitrate}\"";
        
        var attributes = $"{sizeAttr}{durationAttr}{resolutionAttr}{bitrateAttr}";
        
        _logger.LogInformation("Creating DLNA item: {Title} (Duration: {Duration}, Size: {Size} bytes, Bitrate: {Bitrate})", 
            title, duration, size, bitrate);
        
        var dlnaFlags = "DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        var protocolInfo = $"http-get:*:{mimeType}:{dlnaFlags}";
        
        return _xmlTemplateService.GetTemplate("ItemTemplate",
            item.Id.Value,           // {0} - id
            parentId,                // {1} - parentID
            title,                   // {2} - title
            DateTime.UtcNow.ToString("yyyy-MM-dd"), // {3} - date
            upnpClass,               // {4} - upnp:class
            albumArtXml,             // {5} - album art
            protocolInfo,            // {6} - protocolInfo
            attributes,              // {7} - res attributes
            streamUrl                // {8} - stream URL
        );
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
        var estimatedBitrate = 8000000; // 8 Mbps
        return (long)(durationSeconds * estimatedBitrate / 8);
    }

    // MARK: GetProxyStreamUrl
    private string GetProxyStreamUrl(Guid itemId)
    {
        var localIp = GetLocalIPAddress();
        var dlnaPort = _configuration["Dlna:Port"] ?? "8200";
        var streamUrl = $"http://{localIp}:{dlnaPort}/stream/{itemId}";
        
        _logger.LogDebug("Generated proxy stream URL for item {ItemId}: {StreamUrl}", itemId, streamUrl);
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
                    _logger.LogDebug("Using local IP address: {IpAddress}", ip.ToString());
                    return ip.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get local IP address, using loopback");
        }
        
        _logger.LogWarning("No non-loopback IP found, using 127.0.0.1");
        return "127.0.0.1";
    }

    // MARK: CreateContainerXml
    private string CreateContainerXml(string id, string parentId, string title, string upnpClass, int childCount, Guid? itemId = null)
    {
        var escapedTitle = System.Security.SecurityElement.Escape(title);
        var albumArtUrl = itemId.HasValue ? GetAlbumArtUrl(itemId.Value) : "";
        var albumArtXml = !string.IsNullOrEmpty(albumArtUrl) ?
            $"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>" : "";

        return _xmlTemplateService.GetTemplate("ContainerTemplate",
            id,              // {0} - id
            parentId,        // {1} - parentID
            childCount,      // {2} - childCount
            escapedTitle,    // {3} - title
            upnpClass,       // {4} - upnp:class
            albumArtXml      // {5} - album art
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
            BaseItemDto_Type.MusicVideo => "object.item.videoItem",
            BaseItemDto_Type.Photo => "object.item.imageItem.photo",
            BaseItemDto_Type.Video => "object.item.videoItem",
            BaseItemDto_Type.CollectionFolder => "object.container.storageFolder",
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
        _logger.LogTrace("Browse response XML: {Response}", response);
        
        return response;
    }

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        var escapedError = System.Security.SecurityElement.Escape(error);
        return _xmlTemplateService.GetTemplate("SoapFault", escapedError);
    }
}