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
            _logger.LogInformation("Fetching library folders for user {UserId}", userId);
            
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
            });
            
            _logger.LogInformation("Retrieved {Count} library folders", response?.Items?.Count ?? 0);
            
            if (response?.Items != null)
            {
                foreach (var item in response.Items)
                {
                    _logger.LogInformation("Library: {Name} (Type: {Type}, Id: {Id}, CollectionType: {CollectionType}, ChildCount: {ChildCount})", 
                        item.Name, item.Type, item.Id, item.CollectionType, item.ChildCount);
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
            _logger.LogInformation("Fetching library content for library {LibraryId}, user {UserId}", libraryId, userId);
            
            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
                }
                
                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.ParentId = libraryId;
                requestConfiguration.QueryParameters.Recursive = true;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];
                
                // Exclude certain item types that aren't useful for DLNA browsing
                requestConfiguration.QueryParameters.ExcludeItemTypes = [
                    BaseItemKind.Person,
                    BaseItemKind.Genre,
                    BaseItemKind.Studio
                ];
            });
            
            _logger.LogInformation("Retrieved {Count} items from library {LibraryId}", response?.Items?.Count ?? 0, libraryId);
            
            if (response?.Items != null && response.Items.Count > 0)
            {
                foreach (var item in response.Items.Take(5))
                {
                    _logger.LogInformation("Library item: {Name} (Type: {Type}, IsFolder: {IsFolder}, Id: {Id})", 
                        item.Name, item.Type, item.IsFolder, item.Id);
                }
                
                if (response.Items.Count > 5)
                {
                    _logger.LogInformation("... and {Count} more items", response.Items.Count - 5);
                }
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
            _logger.LogInformation("Fetching items for parent {ParentId}, user {UserId}", parentId, userId);
            
            var response = await _apiClient.Items.GetAsync(requestConfiguration =>
            {
                var accessToken = _configuration["Jellyfin:AccessToken"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    requestConfiguration.Headers.Add("X-Emby-Token", accessToken);
                }
                
                requestConfiguration.QueryParameters.UserId = userId;
                requestConfiguration.QueryParameters.EnableTotalRecordCount = true;
                
                if (parentId.HasValue)
                {
                    requestConfiguration.QueryParameters.ParentId = parentId.Value;
                    requestConfiguration.QueryParameters.Recursive = false;
                }
                
                if (!string.IsNullOrEmpty(mediaTypes))
                {
                    var mediaTypeEnums = mediaTypes.Split(',')
                        .Select(mt => Enum.Parse<MediaType>(mt.Trim(), true))
                        .ToArray();
                    requestConfiguration.QueryParameters.MediaTypes = mediaTypeEnums;
                }
                
                requestConfiguration.QueryParameters.SortBy = [ItemSortBy.SortName];
                requestConfiguration.QueryParameters.SortOrder = [SortOrder.Ascending];
            });
            
            _logger.LogInformation("Retrieved {Count} items for parent {ParentId}", response?.Items?.Count ?? 0, parentId);
            
            if (response?.Items != null)
            {
                foreach (var item in response.Items.Take(3))
                {
                    _logger.LogInformation("Item: {Name} (Type: {Type}, IsFolder: {IsFolder}, Id: {Id})", 
                        item.Name, item.Type, item.IsFolder, item.Id);
                }
            }
            
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
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item {ItemId}", itemId);
            return null;
        }
    }

    // MARK: GetStreamUrlAsync
    public string? GetStreamUrlAsync(Guid itemId)
    {
        if (!IsConfigured) return null;

        try
        {
            var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
            var accessToken = _configuration["Jellyfin:AccessToken"];
            
            return $"{serverUrl}/Videos/{itemId}/stream?X-Emby-Token={accessToken}";
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
            _logger.LogError(ex, "Failed to get image URL for item {ItemId}", itemId);
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
}