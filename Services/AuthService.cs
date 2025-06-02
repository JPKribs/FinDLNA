using System;
using System.Collections.Generic;
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
            var baseUrl = request.ServerUrl.TrimEnd('/');
            var authUrl = $"{baseUrl}/Users/AuthenticateByName";

            var authRequest = new
            {
                Username = request.Username,
                Pw = request.Password ?? string.Empty
            };

            var json = JsonSerializer.Serialize(authRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, authUrl)
            {
                Content = content
            };

            requestMessage.Headers.Add("X-Emby-Authorization", 
                $"MediaBrowser Client=\"{_clientOptions.AppName}\", Device=\"{_clientOptions.DeviceName}\", DeviceId=\"{_clientOptions.DeviceId}\", Version=\"{_clientOptions.AppVersion}\"");

            var response = await _httpClient.SendAsync(requestMessage);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Authentication failed with status {StatusCode}: {Content}", 
                    response.StatusCode, errorContent);

                var errorMessage = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.BadRequest => "Bad request — likely malformed credentials",
                    System.Net.HttpStatusCode.Unauthorized => "Unauthorized — bad username/password",
                    System.Net.HttpStatusCode.Forbidden => "Forbidden — account disabled or restricted",
                    System.Net.HttpStatusCode.NotFound => "Not found — invalid server URL",
                    _ => $"Unexpected error: {response.StatusCode}"
                };

                return new AuthResult { Success = false, Error = errorMessage };
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (string.IsNullOrEmpty(responseContent))
            {
                return new AuthResult { Success = false, Error = "Empty response from server" };
            }

            var responseObj = JsonSerializer.Deserialize<JsonElement>(responseContent);

            string? accessToken = null;
            string? userId = null;

            if (responseObj.TryGetProperty("AccessToken", out var tokenElement))
            {
                accessToken = tokenElement.GetString();
            }

            if (responseObj.TryGetProperty("User", out var userElement))
            {
                if (userElement.TryGetProperty("Id", out var idElement))
                {
                    userId = idElement.GetString();
                }
            }

            if (!string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(userId))
            {
                await SaveConfigurationAsync(baseUrl, accessToken, userId);

                return new AuthResult
                {
                    Success = true,
                    AccessToken = accessToken,
                    UserId = userId
                };
            }

            return new AuthResult { Success = false, Error = "Authentication response missing required fields" };
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

    // MARK: SaveConfigurationAsync
    private async Task SaveConfigurationAsync(string serverUrl, string accessToken, string userId)
    {
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");

        var json = await File.ReadAllTextAsync(configPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var options = new JsonSerializerOptions { WriteIndented = true };
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "Jellyfin")
            {
                writer.WritePropertyName("Jellyfin");
                writer.WriteStartObject();
                writer.WriteString("ServerUrl", serverUrl);
                writer.WriteString("AccessToken", accessToken);
                writer.WriteString("UserId", userId);
                writer.WriteEndObject();
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        var updatedJson = Encoding.UTF8.GetString(stream.ToArray());
        await File.WriteAllTextAsync(configPath, updatedJson);
        
        _logger.LogInformation("Configuration saved successfully");
    }
}