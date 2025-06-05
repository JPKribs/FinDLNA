using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: Enhanced JellyfinService
public class JellyfinService
{
    private readonly ILogger<JellyfinService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinApiClient _apiClient;
    private readonly Dictionary<string, object> _cache = new();
    private readonly object _cacheLock = new();
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);

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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetLibraryFoldersAsync called but Jellyfin not configured");
            return null;
        }

        var cacheKey = "library_folders";
        if (TryGetFromCache<IReadOnlyList<BaseItemDto>>(cacheKey, out var cachedResult))
        {
            _logger.LogTrace("Returning cached library folders");
            return cachedResult;
        }

        try
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                _logger.LogError("Invalid user ID in configuration");
                return null;
            }

            _logger.LogDebug("Fetching library folders for user {UserId}", userId);

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                ConfigureRequest(requestConfiguration);
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

            var libraries = response?.Items?.Where(ValidateLibraryItem).ToList().AsReadOnly();
            
            if (libraries?.Any() == true)
            {
                SetCache(cacheKey, libraries);
                
                _logger.LogInformation("Retrieved {Count} valid library folders", libraries.Count);
                
                foreach (var lib in libraries)
                {
                    _logger.LogDebug("Library: {Name} (ID: {Id}, Type: {CollectionType}, ChildCount: {ChildCount})", 
                        lib.Name, lib.Id, lib.CollectionType, lib.ChildCount);
                }
                
                return libraries;
            }
            else
            {
                _logger.LogWarning("No valid library folders found");
                return new List<BaseItemDto>().AsReadOnly();
            }
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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetLibraryContentAsync called but Jellyfin not configured");
            return null;
        }

        var cacheKey = $"library_content_{libraryId}";
        if (TryGetFromCache<IReadOnlyList<BaseItemDto>>(cacheKey, out var cachedResult))
        {
            _logger.LogTrace("Returning cached library content for {LibraryId}", libraryId);
            return cachedResult;
        }

        try
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                _logger.LogError("Invalid user ID in configuration");
                return null;
            }

            _logger.LogDebug("Fetching content for library {LibraryId}", libraryId);

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                ConfigureRequest(requestConfiguration);
                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.ParentId = libraryId;
                requestConfiguration.QueryParameters.Recursive = false;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName, ItemSortBy.IndexNumber];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];

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
                    ItemFields.Studios,
                    ItemFields.CumulativeRunTimeTicks
                ];
            });

            var items = response?.Items?.Where(ValidateContentItem).ToList().AsReadOnly();
            
            if (items?.Any() == true)
            {
                SetCache(cacheKey, items);
                
                _logger.LogInformation("Retrieved {Count} valid items from library {LibraryId}", items.Count, libraryId);
                
                var grouped = items
                    .Where(i => i.Type.HasValue)
                    .GroupBy(i => i.Type!.Value)
                    .ToDictionary(g => g.Key, g => g.Count());
                foreach (var group in grouped.OrderByDescending(g => g.Value))
                {
                    _logger.LogDebug("Library {LibraryId} has {Count} items of type {Type}", 
                        libraryId, group.Value, group.Key);
                }
                
                return items;
            }
            else
            {
                _logger.LogInformation("No valid items found in library {LibraryId}", libraryId);
                return new List<BaseItemDto>().AsReadOnly();
            }
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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetItemsAsync called but Jellyfin not configured");
            return null;
        }

        var cacheKey = $"items_{parentId}_{mediaTypes}";
        if (TryGetFromCache<IReadOnlyList<BaseItemDto>>(cacheKey, out var cachedResult))
        {
            _logger.LogTrace("Returning cached items for parent {ParentId}", parentId);
            return cachedResult;
        }

        try
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                _logger.LogError("Invalid user ID in configuration");
                return null;
            }

            _logger.LogDebug("Fetching items for parent {ParentId}", parentId);

            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                ConfigureRequest(requestConfiguration);
                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.Recursive = false;

                if (parentId.HasValue)
                {
                    requestConfiguration.QueryParameters.ParentId = parentId.Value;
                }

                if (!string.IsNullOrEmpty(mediaTypes))
                {
                    var mediaTypeEnums = mediaTypes.Split(',')
                        .Select(mt => 
                        {
                            if (Enum.TryParse<MediaType>(mt.Trim(), true, out var result))
                                return result;
                            _logger.LogWarning("Invalid media type: {MediaType}", mt);
                            return (MediaType?)null;
                        })
                        .Where(mt => mt.HasValue)
                        .Select(mt => mt!.Value)
                        .ToArray();
                    
                    if (mediaTypeEnums.Any())
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
                    ItemFields.Genres,
                    ItemFields.CumulativeRunTimeTicks
                ];
            });

            var items = response?.Items?.Where(ValidateContentItem).ToList().AsReadOnly();
            
            if (items?.Any() == true)
            {
                SetCache(cacheKey, items);
                _logger.LogDebug("Retrieved {Count} valid items for parent {ParentId}", items.Count, parentId);
                return items;
            }
            else
            {
                _logger.LogDebug("No valid items found for parent {ParentId}", parentId);
                return new List<BaseItemDto>().AsReadOnly();
            }
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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetItemAsync called but Jellyfin not configured");
            return null;
        }

        var cacheKey = $"item_{itemId}";
        if (TryGetFromCache<BaseItemDto>(cacheKey, out var cachedResult))
        {
            _logger.LogTrace("Returning cached item {ItemId}", itemId);
            return cachedResult;
        }

        try
        {
            var response = await _apiClient.Items[itemId].GetAsync(config =>
            {
                ConfigureRequest(config);
            });
            
            if (response != null && ValidateContentItem(response))
            {
                SetCache(cacheKey, response);
                _logger.LogTrace("Retrieved item {ItemId} ({Name}, Type: {Type})", 
                    itemId, response.Name, response.Type);
                return response;
            }
            else
            {
                _logger.LogWarning("Item {ItemId} not found or invalid", itemId);
                return null;
            }
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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetStreamUrlAsync called but Jellyfin not configured");
            return null;
        }

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Missing server URL or access token for stream URL generation");
                return null;
            }

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
        if (!IsConfigured) 
        {
            _logger.LogWarning("GetImageUrlAsync called but Jellyfin not configured");
            return null;
        }

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
            {
                _logger.LogDebug("Missing server URL or access token for image URL generation");
                return null;
            }

            return $"{serverUrl}/Items/{itemId}/Images/{imageType}?X-Emby-Token={accessToken}";
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to get image URL for item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: TestConnectionAsync
    public async Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured) 
        {
            _logger.LogInformation("Connection test skipped - Jellyfin not configured");
            return false;
        }

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Missing server URL or access token for connection test");
                return false;
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Emby-Token", accessToken);
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var response = await httpClient.GetAsync($"{serverUrl}/System/Info");
            var isSuccess = response.IsSuccessStatusCode;

            _logger.LogInformation("Connection test {Result} for {ServerUrl}", 
                isSuccess ? "PASSED" : "FAILED", serverUrl);

            return isSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed");
            return false;
        }
    }

    // MARK: ClearCache
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
        _logger.LogInformation("Jellyfin service cache cleared");
    }

    // MARK: IsConfigured
    public bool IsConfigured => !string.IsNullOrEmpty(_configuration["Jellyfin:AccessToken"]) &&
                               !string.IsNullOrEmpty(_configuration["Jellyfin:ServerUrl"]) &&
                               !string.IsNullOrEmpty(_configuration["Jellyfin:UserId"]);

    // MARK: Private Helper Methods

    private void ConfigureRequest(dynamic requestConfiguration)
    {
        var accessToken = _configuration["Jellyfin:AccessToken"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
        }
    }

    private Guid GetUserId()
    {
        var userIdString = _configuration["Jellyfin:UserId"];
        if (string.IsNullOrEmpty(userIdString))
        {
            _logger.LogError("User ID not found in configuration");
            return Guid.Empty;
        }

        if (Guid.TryParse(userIdString, out var userId))
        {
            return userId;
        }

        _logger.LogError("Invalid User ID format in configuration: {UserId}", userIdString);
        return Guid.Empty;
    }

    private bool ValidateLibraryItem(BaseItemDto item)
    {
        if (!item.Id.HasValue)
        {
            _logger.LogTrace("Skipping library item without ID");
            return false;
        }

        if (string.IsNullOrEmpty(item.Name))
        {
            _logger.LogTrace("Skipping library item {Id} without name", item.Id);
            return false;
        }

        if (item.Type != BaseItemDto_Type.CollectionFolder)
        {
            _logger.LogTrace("Skipping non-collection folder: {Name} ({Type})", item.Name, item.Type);
            return false;
        }

        return true;
    }

    private bool ValidateContentItem(BaseItemDto item)
    {
        if (!item.Id.HasValue)
        {
            _logger.LogTrace("Skipping content item without ID");
            return false;
        }

        if (string.IsNullOrEmpty(item.Name))
        {
            _logger.LogTrace("Skipping content item {Id} without name", item.Id);
            return false;
        }

        var invalidTypes = new[]
        {
            BaseItemDto_Type.Person,
            BaseItemDto_Type.Genre,
            BaseItemDto_Type.Studio
        };

        if (invalidTypes.Contains(item.Type ?? BaseItemDto_Type.Folder))
        {
            _logger.LogTrace("Skipping invalid content type: {Name} ({Type})", item.Name, item.Type);
            return false;
        }

        return true;
    }

    private bool TryGetFromCache<T>(string key, out T? value) where T : class
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cachedItem) && cachedItem is CacheItem<T> item)
            {
                if (DateTime.UtcNow - item.Timestamp < _cacheTimeout)
                {
                    value = item.Value;
                    return true;
                }
                else
                {
                    _cache.Remove(key);
                }
            }
        }

        value = null;
        return false;
    }

    private void SetCache<T>(string key, T value) where T : class
    {
        lock (_cacheLock)
        {
            _cache[key] = new CacheItem<T> { Value = value, Timestamp = DateTime.UtcNow };
            
            if (_cache.Count > 100)
            {
                var oldestKeys = _cache
                    .Where(kvp => kvp.Value is CacheItem<object> item && DateTime.UtcNow - item.Timestamp > _cacheTimeout)
                    .Select(kvp => kvp.Key)
                    .Take(20)
                    .ToList();

                foreach (var oldKey in oldestKeys)
                {
                    _cache.Remove(oldKey);
                }
            }
        }
    }

    private class CacheItem<T>
    {
        public T Value { get; set; } = default!;
        public DateTime Timestamp { get; set; }
    }
}