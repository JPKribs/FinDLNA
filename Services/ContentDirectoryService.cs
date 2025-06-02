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

    public ContentDirectoryService(ILogger<ContentDirectoryService> logger, JellyfinService jellyfinService)
    {
        _logger = logger;
        _jellyfinService = jellyfinService;
    }

    // MARK: GetServiceDescriptionXml
    public string GetServiceDescriptionXml()
    {
        return """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
            <specVersion>
                <major>1</major>
                <minor>0</minor>
            </specVersion>
            <actionList>
                <action>
                    <name>Browse</name>
                    <argumentList>
                        <argument>
                            <name>ObjectID</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>BrowseFlag</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>Filter</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>StartingIndex</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>RequestedCount</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>SortCriteria</name>
                            <direction>in</direction>
                            <relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>Result</name>
                            <direction>out</direction>
                            <relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>NumberReturned</name>
                            <direction>out</direction>
                            <relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>TotalMatches</name>
                            <direction>out</direction>
                            <relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable>
                        </argument>
                        <argument>
                            <name>UpdateID</name>
                            <direction>out</direction>
                            <relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable>
                        </argument>
                    </argumentList>
                </action>
                <action>
                    <name>GetSearchCapabilities</name>
                    <argumentList>
                        <argument>
                            <name>SearchCaps</name>
                            <direction>out</direction>
                            <relatedStateVariable>SearchCapabilities</relatedStateVariable>
                        </argument>
                    </argumentList>
                </action>
                <action>
                    <name>GetSortCapabilities</name>
                    <argumentList>
                        <argument>
                            <name>SortCaps</name>
                            <direction>out</direction>
                            <relatedStateVariable>SortCapabilities</relatedStateVariable>
                        </argument>
                    </argumentList>
                </action>
            </actionList>
            <serviceStateTable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_ObjectID</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_BrowseFlag</name>
                    <dataType>string</dataType>
                    <allowedValueList>
                        <allowedValue>BrowseMetadata</allowedValue>
                        <allowedValue>BrowseDirectChildren</allowedValue>
                    </allowedValueList>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_Filter</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_Index</name>
                    <dataType>ui4</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_Count</name>
                    <dataType>ui4</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_SortCriteria</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_Result</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>A_ARG_TYPE_UpdateID</name>
                    <dataType>ui4</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>SearchCapabilities</name>
                    <dataType>string</dataType>
                </stateVariable>
                <stateVariable sendEvents="no">
                    <name>SortCapabilities</name>
                    <dataType>string</dataType>
                </stateVariable>
            </serviceStateTable>
        </scpd>
        """;
    }

    // MARK: ProcessBrowseRequestAsync
    public async Task<string> ProcessBrowseRequestAsync(string soapBody)
    {
        try
        {
            _logger.LogInformation("Raw SOAP body received: {SoapBody}", soapBody);
            
            var doc = XDocument.Parse(soapBody);
            var ns = XNamespace.Get("http://schemas.xmlsoap.org/soap/envelope/");
            var upnpNs = XNamespace.Get("urn:schemas-upnp-org:service:ContentDirectory:1");

            var browseElement = doc.Descendants(upnpNs + "Browse").FirstOrDefault();
            if (browseElement == null)
            {
                _logger.LogError("No Browse element found in SOAP request");
                return CreateSoapFault("Invalid SOAP request");
            }

            var objectId = browseElement.Element(upnpNs + "ObjectID")?.Value ?? "0";
            var browseFlag = browseElement.Element(upnpNs + "BrowseFlag")?.Value ?? "BrowseDirectChildren";
            var startIndex = int.Parse(browseElement.Element(upnpNs + "StartingIndex")?.Value ?? "0");
            var requestedCount = int.Parse(browseElement.Element(upnpNs + "RequestedCount")?.Value ?? "0");

            _logger.LogInformation("Parsed ObjectID: '{ObjectId}', BrowseFlag: '{BrowseFlag}'", objectId, browseFlag);
            _logger.LogInformation("DLNA Browse Request - ObjectID: {ObjectId}, BrowseFlag: {BrowseFlag}", objectId, browseFlag);

            var result = await BrowseAsync(objectId, browseFlag, startIndex, requestedCount);
            return CreateBrowseResponse(result.didl, result.numberReturned, result.totalMatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing browse request");
            return CreateSoapFault("Internal server error");
        }
    }

    // MARK: ProcessSearchCapabilitiesRequest
    public string ProcessSearchCapabilitiesRequest()
    {
        return """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:GetSearchCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                        <SearchCaps></SearchCaps>
                    </u:GetSearchCapabilitiesResponse>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: ProcessSortCapabilitiesRequest
    public string ProcessSortCapabilitiesRequest()
    {
        return """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:GetSortCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                        <SortCaps>dc:title</SortCaps>
                    </u:GetSortCapabilitiesResponse>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: BrowseAsync
    private async Task<(string didl, int numberReturned, int totalMatches)> BrowseAsync(string objectId, string browseFlag, int startIndex, int requestedCount)
    {
        if (!_jellyfinService.IsConfigured)
        {
            return (CreateDidlLite(""), 0, 0);
        }

        var didlBuilder = new StringBuilder();
        var items = new List<BaseItemDto>();

        _logger.LogInformation("Browsing objectId: '{ObjectId}' (length: {Length})", objectId, objectId?.Length ?? 0);

        if (objectId == "0")
        {
            _logger.LogInformation("Fetching root library folders");
            var libraries = await _jellyfinService.GetLibraryFoldersAsync();
            if (libraries != null)
            {
                items.AddRange(libraries);
                _logger.LogInformation("Found {Count} library folders", libraries.Count);
            }
        }
        else if (Guid.TryParse(objectId, out var parentId))
        {
            _logger.LogInformation("Successfully parsed GUID. Fetching children for parentId: {ParentId}", parentId);
            
            var parentItem = await _jellyfinService.GetItemAsync(parentId);
            if (parentItem != null)
            {
                _logger.LogInformation("Parent item: {Name} (Type: {Type}, CollectionType: {CollectionType})", 
                    parentItem.Name, parentItem.Type, parentItem.CollectionType);

                if (parentItem.Type == BaseItemDto_Type.CollectionFolder)
                {
                    _logger.LogInformation("Browsing collection folder content for {CollectionType}", parentItem.CollectionType);
                    var childItems = await _jellyfinService.GetLibraryContentAsync(parentId);
                    if (childItems != null)
                    {
                        items.AddRange(childItems);
                        _logger.LogInformation("Found {Count} items in collection folder", childItems.Count);
                    }
                    else
                    {
                        _logger.LogWarning("GetLibraryContentAsync returned null for {ParentId}", parentId);
                    }
                }
                else
                {
                    var childItems = await _jellyfinService.GetItemsAsync(parentId);
                    if (childItems != null)
                    {
                        var filteredItems = childItems.Where(item => item.Id != parentId).ToList();
                        items.AddRange(filteredItems);
                        _logger.LogInformation("Found {Count} child items for {ParentId}", filteredItems.Count, parentId);
                    }
                    else
                    {
                        _logger.LogWarning("GetItemsAsync returned null for {ParentId}", parentId);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Could not retrieve parent item for ID: {ParentId}", parentId);
            }
        }
        else
        {
            _logger.LogWarning("Could not parse objectId as GUID: '{ObjectId}'", objectId);
        }

        var totalMatches = items.Count;
        var endIndex = requestedCount > 0 ? Math.Min(startIndex + requestedCount, totalMatches) : totalMatches;
        var itemsToReturn = items.Skip(startIndex).Take(endIndex - startIndex).ToList();

        _logger.LogInformation("Returning {Count} items (from {StartIndex} to {EndIndex} of {TotalMatches})", 
            itemsToReturn.Count, startIndex, endIndex, totalMatches);

        foreach (var item in itemsToReturn)
        {
            if (IsContainer(item))
            {
                didlBuilder.Append(CreateContainerDidl(item));
            }
            else
            {
                didlBuilder.Append(CreateItemDidl(item));
            }
        }

        var didl = CreateDidlLite(didlBuilder.ToString());
        return (didl, itemsToReturn.Count, totalMatches);
    }

    // MARK: IsContainer
    private bool IsContainer(BaseItemDto item)
    {
        return item.IsFolder == true || 
               item.Type == BaseItemDto_Type.Folder || 
               item.Type == BaseItemDto_Type.CollectionFolder ||
               item.Type == BaseItemDto_Type.UserView ||
               item.Type == BaseItemDto_Type.MusicAlbum ||
               item.Type == BaseItemDto_Type.Series ||
               item.Type == BaseItemDto_Type.Season;
    }

    // MARK: CreateContainerDidl
    private string CreateContainerDidl(BaseItemDto item)
    {
        var upnpClass = GetUpnpClass(item);
        var title = System.Security.SecurityElement.Escape(item.Name ?? "Unknown");
        var imageUrl = _jellyfinService.GetImageUrlAsync(item.Id ?? Guid.Empty, ImageType.Primary);
        var parentId = item.ParentId?.ToString() ?? "0";
        
        var albumArt = !string.IsNullOrEmpty(imageUrl) 
            ? $"<upnp:albumArtURI>{System.Security.SecurityElement.Escape(imageUrl)}</upnp:albumArtURI>"
            : "";
        
        return $"""
            <container id="{item.Id}" parentID="{parentId}" restricted="1" searchable="1">
                <dc:title>{title}</dc:title>
                <upnp:class>{upnpClass}</upnp:class>
                <upnp:writeStatus>NOT_WRITABLE</upnp:writeStatus>
                {albumArt}
            </container>
            """;
    }

    // MARK: CreateItemDidl
    private string CreateItemDidl(BaseItemDto item)
    {
        var upnpClass = GetUpnpClass(item);
        var title = System.Security.SecurityElement.Escape(item.Name ?? "Unknown");
        var streamUrl = GetLocalStreamUrl(item.Id ?? Guid.Empty);
        var mimeType = GetMimeType(item);
        var size = GetItemSize(item);
        var imageUrl = _jellyfinService.GetImageUrlAsync(item.Id ?? Guid.Empty, ImageType.Primary);
        var parentId = item.ParentId?.ToString() ?? "0";
        
        if (string.IsNullOrEmpty(streamUrl))
        {
            return "";
        }

        var duration = "";
        if (item.RunTimeTicks.HasValue)
        {
            var timeSpan = TimeSpan.FromTicks(item.RunTimeTicks.Value);
            duration = $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        var durationAttr = !string.IsNullOrEmpty(duration) ? $" duration=\"{duration}\"" : "";
        var resources = $"""<res protocolInfo="http-get:*:{mimeType}:*" size="{size}"{durationAttr}>{System.Security.SecurityElement.Escape(streamUrl)}</res>""";
        
        var albumArt = !string.IsNullOrEmpty(imageUrl) 
            ? $"<upnp:albumArtURI>{System.Security.SecurityElement.Escape(imageUrl)}</upnp:albumArtURI>"
            : "";

        return $"""
            <item id="{item.Id}" parentID="{parentId}" restricted="1">
                <dc:title>{title}</dc:title>
                <upnp:class>{upnpClass}</upnp:class>
                <upnp:writeStatus>NOT_WRITABLE</upnp:writeStatus>
                {albumArt}
                {resources}
            </item>
            """;
    }

    // MARK: GetLocalStreamUrl
    private string GetLocalStreamUrl(Guid itemId)
    {
        // Return a URL pointing to our local streaming endpoint
        return $"http://localhost:8200/stream/{itemId}";
    }

    // MARK: GetItemSize
    private long GetItemSize(BaseItemDto item)
    {
        var mediaSource = item.MediaSources?.FirstOrDefault();
        return mediaSource?.Size ?? 0;
    }

    // MARK: GetUpnpClass
    private string GetUpnpClass(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Movie => "object.item.videoItem.movie",
            BaseItemDto_Type.Episode => "object.item.videoItem.videoBroadcast",
            BaseItemDto_Type.Audio => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.Photo => "object.item.imageItem.photo",
            BaseItemDto_Type.MusicAlbum => "object.container.album.musicAlbum",
            BaseItemDto_Type.Series => "object.container.person.movieGenre",
            BaseItemDto_Type.Season => "object.container",
            BaseItemDto_Type.CollectionFolder => "object.container.storageFolder",
            BaseItemDto_Type.Folder => "object.container.storageFolder",
            _ => "object.container"
        };
    }

    // MARK: GetMimeType
    private string GetMimeType(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Movie or BaseItemDto_Type.Episode => "video/mp4",
            BaseItemDto_Type.Audio => "audio/mpeg",
            BaseItemDto_Type.Photo => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    // MARK: CreateDidlLite
    private string CreateDidlLite(string content)
    {
        return $"""
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" 
                       xmlns:dc="http://purl.org/dc/elements/1.1/" 
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
                {content}
            </DIDL-Lite>
            """;
    }

    // MARK: CreateBrowseResponse
    private string CreateBrowseResponse(string didl, int numberReturned, int totalMatches)
    {
        var escapedDidl = System.Security.SecurityElement.Escape(didl);
        
        // MARK: Debug - log the actual DIDL-Lite XML being returned
        _logger.LogInformation("DIDL-Lite XML being returned: {DidlXml}", didl);
        
        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body>
                    <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                        <Result>{escapedDidl}</Result>
                        <NumberReturned>{numberReturned}</NumberReturned>
                        <TotalMatches>{totalMatches}</TotalMatches>
                        <UpdateID>0</UpdateID>
                    </u:BrowseResponse>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: CreateSoapFault
    private string CreateSoapFault(string error)
    {
        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                <s:Body>
                    <s:Fault>
                        <faultcode>s:Client</faultcode>
                        <faultstring>{System.Security.SecurityElement.Escape(error)}</faultstring>
                    </s:Fault>
                </s:Body>
            </s:Envelope>
            """;
    }
}