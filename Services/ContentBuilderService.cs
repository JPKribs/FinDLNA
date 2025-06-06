using Jellyfin.Sdk.Generated.Models;
using System.Security;

namespace FinDLNA.Services;

// MARK: IContentBuilderService
public interface IContentBuilderService
{
    Task<string> BuildContainerAsync(BaseItemDto item, string parentId, DeviceProfile? deviceProfile);
    Task<string> BuildItemAsync(BaseItemDto item, string parentId, DeviceProfile? deviceProfile);
    string BuildDidlContainer(string content);
}

// MARK: ContentBuilderService
public class ContentBuilderService : IContentBuilderService
{
    private readonly ILogger<ContentBuilderService> _logger;
    private readonly XmlTemplateService _xmlTemplateService;
    private readonly DlnaMetadataBuilder _metadataBuilder;
    private readonly DlnaStreamUrlBuilder _streamUrlBuilder;
    private readonly JellyfinService _jellyfinService;

    public ContentBuilderService(
        ILogger<ContentBuilderService> logger,
        XmlTemplateService xmlTemplateService,
        DlnaMetadataBuilder metadataBuilder,
        DlnaStreamUrlBuilder streamUrlBuilder,
        JellyfinService jellyfinService)
    {
        _logger = logger;
        _xmlTemplateService = xmlTemplateService;
        _metadataBuilder = metadataBuilder;
        _streamUrlBuilder = streamUrlBuilder;
        _jellyfinService = jellyfinService;
    }

    // MARK: BuildContainerAsync
    public async Task<string> BuildContainerAsync(BaseItemDto item, string parentId, DeviceProfile? deviceProfile)
    {
        if (!item.Id.HasValue)
        {
            _logger.LogWarning("Cannot build container for item without ID");
            return string.Empty;
        }

        var childCount = await GetChildCountAsync(item.Id.Value);
        var metadata = _metadataBuilder.BuildContainerMetadata(item.Id, deviceProfile);
        var upnpClass = GetContainerUpnpClass(item);
        var title = SecurityElement.Escape(item.Name ?? "Unknown");

        return _xmlTemplateService.GetTemplate("ContainerTemplate",
            item.Id.Value,
            parentId,
            childCount,
            title,
            upnpClass,
            metadata);
    }

    // MARK: BuildItemAsync
    public async Task<string> BuildItemAsync(BaseItemDto item, string parentId, DeviceProfile? deviceProfile)
    {
        if (!item.Id.HasValue)
        {
            _logger.LogWarning("Cannot build item without ID");
            return string.Empty;
        }

        var streamUrl = _streamUrlBuilder.BuildStreamUrl(item.Id.Value, deviceProfile);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("Cannot generate stream URL for item {ItemId}", item.Id.Value);
            return string.Empty;
        }

        var metadata = _metadataBuilder.BuildItemMetadata(item, deviceProfile);
        var resourceAttributes = _metadataBuilder.BuildResourceAttributes(item, deviceProfile);
        var mimeType = _streamUrlBuilder.GetMimeType(item, deviceProfile);
        var protocolInfo = _streamUrlBuilder.GetProtocolInfo(mimeType, deviceProfile);
        var title = SecurityElement.Escape(_metadataBuilder.GetDisplayTitle(item));
        var upnpClass = GetItemUpnpClass(item);

        return _xmlTemplateService.GetTemplate("ItemTemplate",
            item.Id.Value,
            parentId,
            title,
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            upnpClass,
            metadata,
            protocolInfo,
            resourceAttributes,
            streamUrl);
    }

    // MARK: BuildDidlContainer
    public string BuildDidlContainer(string content)
    {
        return _xmlTemplateService.GetTemplate("DidlLiteTemplate", content);
    }

    // MARK: GetChildCountAsync
    private async Task<int> GetChildCountAsync(Guid itemId)
    {
        try
        {
            var items = await _jellyfinService.GetItemsAsync(itemId);
            return items?.Count ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting child count for {ItemId}", itemId);
            return 0;
        }
    }

    // MARK: GetContainerUpnpClass
    private string GetContainerUpnpClass(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Series => "object.container.album.videoAlbum",
            BaseItemDto_Type.Season => "object.container.album.videoAlbum",
            BaseItemDto_Type.MusicAlbum => "object.container.album.musicAlbum",
            BaseItemDto_Type.MusicArtist => "object.container.person.musicArtist",
            BaseItemDto_Type.CollectionFolder => GetLibraryUpnpClass(item),
            _ => "object.container.storageFolder"
        };
    }

    // MARK: GetItemUpnpClass
    private string GetItemUpnpClass(BaseItemDto item)
    {
        return item.Type switch
        {
            BaseItemDto_Type.Movie => "object.item.videoItem.movie",
            BaseItemDto_Type.Episode => "object.item.videoItem",
            BaseItemDto_Type.Audio => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.AudioBook => "object.item.audioItem.musicTrack",
            BaseItemDto_Type.MusicVideo => "object.item.videoItem.musicVideoClip",
            BaseItemDto_Type.Photo => "object.item.imageItem.photo",
            BaseItemDto_Type.Video => "object.item.videoItem",
            _ => "object.item.videoItem"
        };
    }

    // MARK: GetLibraryUpnpClass
    private string GetLibraryUpnpClass(BaseItemDto library)
    {
        return library.CollectionType switch
        {
            BaseItemDto_CollectionType.Movies => "object.container.genre.movieGenre",
            BaseItemDto_CollectionType.Tvshows => "object.container.genre.movieGenre",
            BaseItemDto_CollectionType.Music => "object.container.storageFolder",
            BaseItemDto_CollectionType.Photos => "object.container.album.photoAlbum",
            _ => "object.container.storageFolder"
        };
    }
}