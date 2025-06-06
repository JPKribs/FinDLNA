using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Sdk.Generated.Models;

namespace FinDLNA.Services;

// MARK: DlnaStreamUrlBuilder
public class DlnaStreamUrlBuilder
{
    private readonly ILogger<DlnaStreamUrlBuilder> _logger;
    private readonly IConfiguration _configuration;

    public DlnaStreamUrlBuilder(ILogger<DlnaStreamUrlBuilder> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    // MARK: BuildStreamUrl
    public string BuildStreamUrl(Guid itemId, DeviceProfile? deviceProfile)
    {
        var serverUrl = _configuration["Jellyfin:ServerUrl"]?.TrimEnd('/');
        var accessToken = _configuration["Jellyfin:AccessToken"];
        
        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
        {
            _logger.LogError("Missing Jellyfin server URL or access token");
            return "";
        }

        var queryParams = BuildQueryParameters(accessToken, deviceProfile);
        var queryString = string.Join("&", queryParams);
        var streamUrl = $"{serverUrl}/Videos/{itemId}/stream?{queryString}";
        
        _logger.LogTrace("Generated stream URL for item {ItemId}", itemId);
        return streamUrl;
    }

    // MARK: BuildQueryParameters
    private List<string> BuildQueryParameters(string accessToken, DeviceProfile? deviceProfile)
    {
        var queryParams = new List<string>
        {
            $"api_key={accessToken}",
            "Static=true"
        };

        if (deviceProfile?.MaxStreamingBitrate.HasValue == true)
        {
            queryParams.Add($"MaxStreamingBitrate={deviceProfile.MaxStreamingBitrate.Value}");
        }

        // Add device-specific transcoding hints
        AddDeviceSpecificParameters(queryParams, deviceProfile);

        return queryParams;
    }

    // MARK: AddDeviceSpecificParameters
    private void AddDeviceSpecificParameters(List<string> queryParams, DeviceProfile? deviceProfile)
    {
        if (deviceProfile?.Name == null) return;

        if (deviceProfile.Name.Contains("Samsung"))
        {
            queryParams.Add("EnableAutoStreamCopy=true");
            queryParams.Add("AllowVideoStreamCopy=true");
            queryParams.Add("AllowAudioStreamCopy=true");
        }
        else if (deviceProfile.Name.Contains("Xbox"))
        {
            queryParams.Add("EnableAutoStreamCopy=false");
            queryParams.Add("VideoCodec=h264");
            queryParams.Add("AudioCodec=aac");
        }
        else if (deviceProfile.Name.Contains("LG"))
        {
            queryParams.Add("EnableAutoStreamCopy=true");
        }
    }

    // MARK: GetProtocolInfo
    public string GetProtocolInfo(string mimeType, DeviceProfile? deviceProfile)
    {
        var dlnaFlags = GetDlnaFlags(deviceProfile);
        return $"http-get:*:{mimeType}:{dlnaFlags}";
    }

    // MARK: GetDlnaFlags
    private string GetDlnaFlags(DeviceProfile? deviceProfile)
    {
        var defaultFlags = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        
        if (deviceProfile?.Name == null) return defaultFlags;

        if (deviceProfile.Name.Contains("Samsung"))
            return "DLNA.ORG_PN=AVC_MP4_MP_HD_1080i_AAC;DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        
        if (deviceProfile.Name.Contains("Xbox"))
            return "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01500000000000000000000000000000";

        return defaultFlags;
    }

    // MARK: GetMimeType
    public string GetMimeType(BaseItemDto item, DeviceProfile? deviceProfile)
    {
        var defaultMimeType = item.Type switch
        {
            BaseItemDto_Type.Audio => "audio/mpeg",
            BaseItemDto_Type.Photo => "image/jpeg",
            _ => "video/mp4"
        };

        if (deviceProfile?.Name == null) return defaultMimeType;

        // Device-specific MIME type preferences
        if (deviceProfile.Name.Contains("Samsung") && item.Type != BaseItemDto_Type.Audio)
            return "video/mp4";
        
        if (deviceProfile.Name.Contains("LG") && item.Type != BaseItemDto_Type.Audio)
            return "video/mp4";

        return defaultMimeType;
    }
}