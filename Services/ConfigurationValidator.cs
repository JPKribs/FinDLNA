using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FinDLNA.Services;

// MARK: ConfigurationValidator
public class ConfigurationValidator
{
    private readonly ILogger<ConfigurationValidator> _logger;

    public ConfigurationValidator(ILogger<ConfigurationValidator> logger)
    {
        _logger = logger;
    }

    // MARK: ValidateConfiguration
    public void ValidateConfiguration(IConfiguration configuration)
    {
        _logger.LogInformation("Validating application configuration");

        var requiredSettings = new[]
        {
            "Dlna:Port",
            "WebInterface:Port",
            "JellyfinClient:AppName",
            "JellyfinClient:AppVersion",
            "JellyfinClient:DeviceName",
            "JellyfinClient:DeviceId"
        };

        foreach (var setting in requiredSettings)
        {
            if (string.IsNullOrEmpty(configuration[setting]))
            {
                _logger.LogCritical("Required setting '{Setting}' is missing", setting);
                throw new InvalidOperationException($"Required setting '{setting}' is missing");
            }
        }

        if (!int.TryParse(configuration["Dlna:Port"], out var dlnaPort) || dlnaPort < 1 || dlnaPort > 65535)
        {
            _logger.LogCritical("Dlna:Port must be a valid port number between 1 and 65535");
            throw new InvalidOperationException("Dlna:Port must be a valid port number between 1 and 65535");
        }

        if (!int.TryParse(configuration["WebInterface:Port"], out var webPort) || webPort < 1 || webPort > 65535)
        {
            _logger.LogCritical("WebInterface:Port must be a valid port number between 1 and 65535");
            throw new InvalidOperationException("WebInterface:Port must be a valid port number between 1 and 65535");
        }

        _logger.LogInformation("Configuration validation passed");
    }
}