using System.Text;
using System.Xml.Linq;
using System.Linq;
using Microsoft.Extensions.Logging;
using FinDLNA.Models;
using FinDLNA.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: ContentDirectoryService
public class ContentDirectoryService
{
    private readonly ILogger<ContentDirectoryService> _logger;
    private readonly JellyfinService _jellyfinService;
    private readonly DeviceProfileService _deviceProfileService;
    private readonly XmlTemplateService _xmlTemplateService;

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Behind The Scenes", "Deleted Scenes", "Interviews", "Scenes", "Samples", "Shorts",
        "Featurettes", "Extras", "Trailers", "Theme Videos", "Theme Songs", "Specials"
    };

    private static readonly HashSet<BaseItemDto_Type> ContainerTypes = new()
    {
        BaseItemDto_Type.CollectionFolder,
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
        BaseItemDto_Type.AudioBook
    };

    public ContentDirectoryService(
        ILogger<ContentDirectoryService> logger,
        JellyfinService jellyfinService,
        DeviceProfileService deviceProfileService,
        XmlTemplateService xmlTemplateService)
    {
        _logger = logger;
        _jellyfinService = jellyfinService;
        _deviceProfileService = deviceProfileService;
        _xmlTemplateService = xmlTemplateService;
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

            var result = await ProcessBrowseAsync(objectId, browseFlag, startingIndex, requestedCount, userAgent);
            
            return CreateBrowseResponse(result.DidlXml, result.NumberReturned, result.TotalMatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing browse request");
            return CreateSoapFault("Internal server error");
        }
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

        var allResults = new List<(bool isContainer, string xml, string title)>();

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
                allResults.Add((true, containerXml, itemName));
                _logger.LogDebug("Added container: {Name}", itemName);
            }
            else if (MediaTypes.Contains(itemType))
            {
                var itemXml = CreateItemXml(item, itemId.ToString());
                if (!string.IsNullOrEmpty(itemXml))
                {
                    allResults.Add((false, itemXml, itemName));
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

    // MARK: CreateItemXml
    private string CreateItemXml(BaseItemDto item, string parentId)
    {
        if (!item.Id.HasValue) 
        {
            _logger.LogWarning("CreateItemXml: Item has no ID");
            return "";
        }

        var streamUrl = _jellyfinService.GetStreamUrlAsync(item.Id.Value);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("CreateItemXml: No stream URL for item {ItemId}", item.Id.Value);
            return "";
        }

        var mimeType = GetMimeTypeFromItem(item);
        var upnpClass = GetUpnpClass(item.Type ?? BaseItemDto_Type.Video);
        var duration = FormatDuration(item.RunTimeTicks);
        var size = item.MediaSources?.FirstOrDefault()?.Size ?? 0;
        var resolution = GetResolution(item);

        var title = System.Security.SecurityElement.Escape(item.Name ?? "Unknown");
        var albumArtUrl = GetAlbumArtUrl(item.Id.Value);
        
        var albumArtXml = !string.IsNullOrEmpty(albumArtUrl) ? 
            $"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>" : "";
        
        var resolutionAttr = !string.IsNullOrEmpty(resolution) ? $" resolution=\"{resolution}\"" : "";
        
        _logger.LogDebug("Creating media item XML: {Title} (Type: {Type}, MIME: {MimeType})", 
            title, item.Type, mimeType);
        
        return $"""
            <item id="{item.Id.Value}" parentID="{parentId}" restricted="1">
                <dc:title>{title}</dc:title>
                <upnp:class>{upnpClass}</upnp:class>
                {albumArtXml}
                <res protocolInfo="http-get:*:{mimeType}:*" size="{size}" duration="{duration}"{resolutionAttr}>{streamUrl}</res>
            </item>
            """;
    }

    // MARK: CreateContainerXml
    private string CreateContainerXml(string id, string parentId, string title, string upnpClass, int childCount, Guid? itemId = null)
    {
        var escapedTitle = System.Security.SecurityElement.Escape(title);
        var albumArtUrl = itemId.HasValue ? GetAlbumArtUrl(itemId.Value) : "";
        var albumArtXml = !string.IsNullOrEmpty(albumArtUrl) ? 
            $"<upnp:albumArtURI>{albumArtUrl}</upnp:albumArtURI>" : "";
        
        return $"""
            <container id="{id}" parentID="{parentId}" restricted="1" childCount="{childCount}">
                <dc:title>{escapedTitle}</dc:title>
                <upnp:class>{upnpClass}</upnp:class>
                {albumArtXml}
            </container>
            """;
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
        return $"""
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" 
                       xmlns:dc="http://purl.org/dc/elements/1.1/" 
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
                {content}
            </DIDL-Lite>
            """;
    }

    // MARK: GetUpnpClass
    private string GetUpnpClass(BaseItemDto_Type itemType)
    {
        return itemType switch
        {
            BaseItemDto_Type.Movie => "object.item.videoItem.movie",
            BaseItemDto_Type.Episode => "object.item.videoItem",
            BaseItemDto_Type.Series => "object.container.album.videoAlbum",
            BaseItemDto_Type.Season => "object.container.album.videoAlbum",
            BaseItemDto_Type.Audio => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.MusicAlbum => "object.container.album.musicAlbum",
            BaseItemDto_Type.MusicArtist => "object.container.person.musicArtist",
            BaseItemDto_Type.Photo => "object.item.imageItem.photo",
            BaseItemDto_Type.Video => "object.item.videoItem",
            BaseItemDto_Type.CollectionFolder => "object.container.storageFolder",
            _ => "object.container.storageFolder"
        };
    }

    // MARK: GetMimeTypeFromItem
    private string GetMimeTypeFromItem(BaseItemDto item)
    {
        var mediaSource = item.MediaSources?.FirstOrDefault();
        var container = mediaSource?.Container?.ToLowerInvariant();

        return container switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "wmv" => "video/x-ms-wmv",
            "webm" => "video/webm",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "m4a" => "audio/mp4",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    // MARK: FormatDuration
    private string FormatDuration(long? runTimeTicks)
    {
        if (!runTimeTicks.HasValue) return "0:00:00";
        
        var timeSpan = TimeSpan.FromTicks(runTimeTicks.Value);
        return $"{(int)timeSpan.TotalHours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
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
        return _xmlTemplateService.GetTemplate("BrowseResponse", escapedResult, numberReturned, totalMatches);
    }

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        var escapedError = System.Security.SecurityElement.Escape(error);
        return _xmlTemplateService.GetTemplate("SoapFault", escapedError);
    }
}

// MARK: BrowseResult
public class BrowseResult
{
    public string DidlXml { get; set; } = string.Empty;
    public int NumberReturned { get; set; }
    public int TotalMatches { get; set; }
}