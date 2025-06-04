using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: JellyfinService
public class JellyfinService
{
    private readonly ILogger<JellyfinService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinApiClient _apiClient;

    public JellyfinService(
        ILogger<JellyfinService> logger,
        IConfiguration configuration,
        JellyfinApiClient apiClient)
    {
        _logger = logger;
        _configuration = configuration;
        _apiClient = apiClient;

        if (IsConfigured)
        {
            _logger.LogInformation("Jellyfin client initialized for server: {ServerUrl}",
                _configuration["Jellyfin:ServerUrl"]);
        }
        else
        {
            _logger.LogWarning("Jellyfin not configured - missing ServerUrl or AccessToken");
        }
    }

    // MARK: GetLibraryFoldersAsync
    public async Task<IReadOnlyList<BaseItemDto>?> GetLibraryFoldersAsync()
    {
        if (!IsConfigured) return null;

        try
        {
            var userId = Guid.Parse(_configuration["Jellyfin:UserId"] ?? "");

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
                }

                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.Recursive = false;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.IncludeItemTypes = [BaseItemKind.CollectionFolder];
                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];
                
                requestConfiguration.QueryParameters.Fields = [
                    ItemFields.MediaSources,
                    ItemFields.Path,
                    ItemFields.SortName,
                    ItemFields.DateCreated,
                    ItemFields.ChildCount
                ];
            });

            _logger.LogInformation("Retrieved {Count} library folders", response?.Items?.Count ?? 0);
            
            if (response?.Items?.Any() == true)
            {
                foreach (var lib in response.Items)
                {
                    _logger.LogInformation("Library: {Name} (ID: {Id}, Type: {CollectionType}, ChildCount: {ChildCount})", 
                        lib.Name, lib.Id, lib.CollectionType, lib.ChildCount);
                }
            }

            return response?.Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library folders");
            return null;
        }
    }

    // MARK: GetLibraryContentAsync
    public async Task<IReadOnlyList<BaseItemDto>?> GetLibraryContentAsync(Guid libraryId)
    {
        if (!IsConfigured) return null;

        try
        {
            var userId = Guid.Parse(_configuration["Jellyfin:UserId"] ?? "");

            _logger.LogInformation("Fetching content for library {LibraryId}", libraryId);

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
                }

                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.ParentId = libraryId;
                requestConfiguration.QueryParameters.Recursive = false; // Only direct children
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName, ItemSortBy.IndexNumber];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];

                // Include all relevant content types
                requestConfiguration.QueryParameters.IncludeItemTypes = [
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.Audio,
                    BaseItemKind.MusicAlbum,
                    BaseItemKind.MusicArtist,
                    BaseItemKind.Photo,
                    BaseItemKind.Video,
                    BaseItemKind.Folder,
                    BaseItemKind.BoxSet,
                    BaseItemKind.CollectionFolder
                ];

                // Exclude problematic types
                requestConfiguration.QueryParameters.ExcludeItemTypes = [
                    BaseItemKind.Person,
                    BaseItemKind.Genre,
                    BaseItemKind.Studio
                ];

                requestConfiguration.QueryParameters.Fields = [
                    ItemFields.MediaSources,
                    ItemFields.MediaStreams,
                    ItemFields.Path,
                    ItemFields.Overview,
                    ItemFields.ProviderIds,
                    ItemFields.SortName,
                    ItemFields.DateCreated,
                    ItemFields.ChildCount,
                    ItemFields.ParentId,
                    ItemFields.Genres,
                    ItemFields.Studios
                ];

                // Get all items - no limit
                requestConfiguration.QueryParameters.Limit = null;
                requestConfiguration.QueryParameters.StartIndex = null;
            });

            var itemCount = response?.Items?.Count ?? 0;
            _logger.LogInformation("Retrieved {Count} items from library {LibraryId}", itemCount, libraryId);

            if (response?.Items?.Any() == true)
            {
                var grouped = response.Items.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.Count());
                foreach (var group in grouped)
                {
                    _logger.LogInformation("Library {LibraryId} has {Count} items of type {Type}", 
                        libraryId, group.Value, group.Key);
                }
            }
            else
            {
                _logger.LogWarning("No items found in library {LibraryId}", libraryId);
            }

            return response?.Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library content for library {LibraryId}", libraryId);
            return null;
        }
    }

    // MARK: GetItemsAsync
    public async Task<IReadOnlyList<BaseItemDto>?> GetItemsAsync(Guid? parentId = null, string? mediaTypes = null)
    {
        if (!IsConfigured) return null;

        try
        {
            var userId = Guid.Parse(_configuration["Jellyfin:UserId"] ?? "");

            _logger.LogDebug("Fetching items for parent {ParentId}", parentId);

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
                }

                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.Recursive = false; // Direct children only

                if (parentId.HasValue)
                {
                    requestConfiguration.QueryParameters.ParentId = parentId.Value;
                }

                if (!string.IsNullOrEmpty(mediaTypes))
                {
                    var mediaTypeEnums = mediaTypes.Split(',')
                        .Select(mt => Enum.Parse<MediaType>(mt.Trim(), true))
                        .ToArray();
                    requestConfiguration.QueryParameters.MediaTypes = mediaTypeEnums;
                }

                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName, ItemSortBy.IndexNumber];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];

                requestConfiguration.QueryParameters.Fields = [
                    ItemFields.MediaSources,
                    ItemFields.MediaStreams,
                    ItemFields.Path,
                    ItemFields.Overview,
                    ItemFields.ProviderIds,
                    ItemFields.SortName,
                    ItemFields.DateCreated,
                    ItemFields.ChildCount,
                    ItemFields.ParentId,
                    ItemFields.Genres
                ];

                // Get all items - no limit
                requestConfiguration.QueryParameters.Limit = null;
                requestConfiguration.QueryParameters.StartIndex = null;
            });

            var itemCount = response?.Items?.Count ?? 0;
            _logger.LogDebug("Retrieved {Count} items for parent {ParentId}", itemCount, parentId);

            return response?.Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items for parent {ParentId}", parentId);
            return null;
        }
    }

    // MARK: GetItemAsync
    public async Task<BaseItemDto?> GetItemAsync(Guid itemId)
    {
        if (!IsConfigured) return null;

        try
        {
            var response = await _apiClient.Items[itemId].GetAsync(config =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    config.Headers.Add("X-Emby-Token", accessToken);
                }
            });
            
            if (response != null)
            {
                _logger.LogTrace("Retrieved item {ItemId} ({Name}, Type: {Type})", 
                    itemId, response.Name, response.Type);
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: GetStreamUrlAsync
    public string? GetStreamUrlAsync(Guid itemId, string? container = null, bool includeDurationHeaders = true)
    {
        if (!IsConfigured) return null;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            var queryParams = new List<string>
            {
                $"X-Emby-Token={accessToken}"
            };

            if (!string.IsNullOrEmpty(container))
            {
                queryParams.Add($"Container={container}");
            }

            if (includeDurationHeaders)
            {
                queryParams.Add("EnableRedirection=false");
                queryParams.Add("EnableRemoteMedia=false");
            }

            var queryString = string.Join("&", queryParams);
            var streamUrl = $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
            
            _logger.LogTrace("Generated stream URL for {ItemId}", itemId);
            
            return streamUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get stream URL for item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: GetImageUrlAsync
    public string? GetImageUrlAsync(Guid itemId, ImageType imageType = ImageType.Primary)
    {
        if (!IsConfigured) return null;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            return $"{serverUrl}/Items/{itemId}/Images/{imageType}?X-Emby-Token={accessToken}";
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to get image URL for item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: IsConfigured
    public bool IsConfigured => !string.IsNullOrEmpty(_configuration["Jellyfin:AccessToken"]) &&
                               !string.IsNullOrEmpty(_configuration["Jellyfin:ServerUrl"]);

    // MARK: RefreshConfiguration
    public void RefreshConfiguration()
    {
        if (IsConfigured)
        {
            _logger.LogInformation("Configuration refreshed for server: {ServerUrl}",
                _configuration["Jellyfin:ServerUrl"]);
        }
    }

    // MARK: GetSubtitleUrlAsync
    public string? GetSubtitleUrlAsync(Guid itemId, int subtitleStreamIndex, string format = "srt")
    {
        if (!IsConfigured) return null;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];
            
            return $"{serverUrl}/Videos/{itemId}/{subtitleStreamIndex}/Subtitles.{format}?X-Emby-Token={accessToken}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get subtitle URL for item {ItemId}, stream {StreamIndex}", itemId, subtitleStreamIndex);
            return null;
        }
    }

    // MARK: TestConnectionAsync
    public async Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured) return false;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Emby-Token", accessToken);

            var response = await httpClient.GetAsync($"{serverUrl}/System/Info");
            var isSuccess = response.IsSuccessStatusCode;

            _logger.LogInformation("Connection test {Result}", isSuccess ? "PASSED" : "FAILED");

            return isSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed");
            return false;
        }
    }
}