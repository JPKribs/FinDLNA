using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FinDLNA.Models;

namespace FinDLNA.Services;

// MARK: AuthService
public class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinClientOptions _clientOptions;
    private readonly HttpClient _httpClient;

    public AuthService(
        ILogger<AuthService> logger,
        IConfiguration configuration,
        IOptions<JellyfinClientOptions> clientOptions,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _clientOptions = clientOptions.Value;
        _httpClient = httpClient;
    }

    // MARK: AuthenticateAsync
    public async Task<AuthResult> AuthenticateAsync(LoginRequest request)
    {
        try
        {
            var response = await SendAuthRequestAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                return CreateErrorResult(response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(responseContent))
            {
                return new AuthResult { Success = false, Error = "Empty response from server" };
            }

            return await ProcessAuthResponseAsync(responseContent, request.ServerUrl);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "Network error during authentication");
            return new AuthResult { Success = false, Error = $"Connection error: {httpEx.Message}" };
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx, "JSON parsing error during authentication");
            return new AuthResult { Success = false, Error = "Invalid server response format" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication error");
            return new AuthResult { Success = false, Error = ex.Message };
        }
    }

    // MARK: SendAuthRequestAsync
    private async Task<HttpResponseMessage> SendAuthRequestAsync(LoginRequest request)
    {
        var baseUrl = request.ServerUrl.TrimEnd('/');
        var authUrl = $"{baseUrl}/Users/AuthenticateByName";

        var authRequest = new
        {
            Username = request.Username,
            Pw = request.Password ?? string.Empty
        };

        var json = JsonSerializer.Serialize(authRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, authUrl) { Content = content };
        requestMessage.Headers.Add("X-Emby-Authorization", BuildAuthHeader());

        return await _httpClient.SendAsync(requestMessage);
    }

    // MARK: BuildAuthHeader
    private string BuildAuthHeader()
    {
        return $"MediaBrowser Client=\"{_clientOptions.AppName}\", " +
               $"Device=\"{_clientOptions.DeviceName}\", " +
               $"DeviceId=\"{_clientOptions.DeviceId}\", " +
               $"Version=\"{_clientOptions.AppVersion}\"";
    }

    // MARK: CreateErrorResult
    private AuthResult CreateErrorResult(System.Net.HttpStatusCode statusCode, string errorContent)
    {
        _logger.LogError("Authentication failed with status {StatusCode}: {Content}", statusCode, errorContent);

        var errorMessage = statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Bad request — likely malformed credentials",
            System.Net.HttpStatusCode.Unauthorized => "Unauthorized — bad username/password",
            System.Net.HttpStatusCode.Forbidden => "Forbidden — account disabled or restricted",
            System.Net.HttpStatusCode.NotFound => "Not found — invalid server URL",
            _ => $"Unexpected error: {statusCode}"
        };

        return new AuthResult { Success = false, Error = errorMessage };
    }

    // MARK: ProcessAuthResponseAsync
    private async Task<AuthResult> ProcessAuthResponseAsync(string responseContent, string serverUrl)
    {
        var responseObj = JsonSerializer.Deserialize<JsonElement>(responseContent);

        var accessToken = ExtractProperty(responseObj, "AccessToken");
        var userId = ExtractNestedProperty(responseObj, "User", "Id");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId))
        {
            return new AuthResult { Success = false, Error = "Authentication response missing required fields" };
        }

        await SaveConfigurationAsync(serverUrl.TrimEnd('/'), accessToken, userId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            UserId = userId
        };
    }

    // MARK: ExtractProperty
    private string? ExtractProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
    }

    // MARK: ExtractNestedProperty
    private string? ExtractNestedProperty(JsonElement element, string parentProperty, string childProperty)
    {
        if (element.TryGetProperty(parentProperty, out var parent) &&
            parent.TryGetProperty(childProperty, out var child))
        {
            return child.GetString();
        }
        return null;
    }

    // MARK: SaveConfigurationAsync
    private async Task SaveConfigurationAsync(string serverUrl, string accessToken, string userId)
    {
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        var existingConfig = await ReadExistingConfigAsync(configPath);
        var updatedConfig = UpdateJellyfinSection(existingConfig, serverUrl, accessToken, userId);
        
        await File.WriteAllTextAsync(configPath, updatedConfig);
        _logger.LogInformation("Configuration saved successfully");
    }

    // MARK: ReadExistingConfigAsync
    private async Task<JsonElement> ReadExistingConfigAsync(string configPath)
    {
        var json = await File.ReadAllTextAsync(configPath);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // MARK: UpdateJellyfinSection
    private string UpdateJellyfinSection(JsonElement root, string serverUrl, string accessToken, string userId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "Jellyfin")
            {
                WriteJellyfinSection(writer, serverUrl, accessToken, userId);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // MARK: WriteJellyfinSection
    private void WriteJellyfinSection(Utf8JsonWriter writer, string serverUrl, string accessToken, string userId)
    {
        writer.WritePropertyName("Jellyfin");
        writer.WriteStartObject();
        writer.WriteString("ServerUrl", serverUrl);
        writer.WriteString("AccessToken", accessToken);
        writer.WriteString("UserId", userId);
        writer.WriteEndObject();
    }
}