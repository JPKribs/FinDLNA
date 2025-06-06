using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Jellyfin.Sdk;
using FinDLNA.Services;
using FinDLNA.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JellyfinClientOptions>(
    builder.Configuration.GetSection("JellyfinClient"));

builder.Services.AddControllers();
builder.Services.AddProblemDetails();

builder.Services.AddHttpClient();

// MARK: Jellyfin SDK setup
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<JellyfinClientOptions>>().Value;
    var settings = new JellyfinSdkSettings();
    settings.Initialize(opts.AppName, opts.AppVersion, opts.DeviceName, opts.DeviceId);
    return settings;
});

builder.Services.AddSingleton<IAuthenticationProvider, AnonymousAuthenticationProvider>();
builder.Services.AddSingleton<JellyfinRequestAdapter>();

builder.Services.AddSingleton<JellyfinApiClient>(sp =>
{
    var adapter = sp.GetRequiredService<JellyfinRequestAdapter>();
    var config = sp.GetRequiredService<IConfiguration>();
    var client = new JellyfinApiClient(adapter);
    
    var serverUrl = config["Jellyfin:ServerUrl"];
    if (!string.IsNullOrEmpty(serverUrl))
    {
        try
        {
            adapter.BaseUrl = serverUrl;
        }
        catch (Exception ex)
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();
            logger.LogWarning(ex, "Failed to set base URL for Jellyfin client");
        }
    }
    
    return client;
});

// MARK: Enhanced Service Registration
builder.Services.AddSingleton<ContentBuilderService>();
builder.Services.AddSingleton<XmlTemplateService>();
builder.Services.AddSingleton<DeviceProfileService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JellyfinService>();
builder.Services.AddSingleton<SsdpService>();
builder.Services.AddSingleton<ContentDirectoryService>();
builder.Services.AddSingleton<StreamingService>();
builder.Services.AddSingleton<DlnaService>();
builder.Services.AddSingleton<DlnaMetadataBuilder>();
builder.Services.AddSingleton<DlnaStreamUrlBuilder>();
builder.Services.AddSingleton<DiagnosticService>();
builder.Services.AddSingleton<ConfigurationValidator>();
builder.Services.AddHostedService<DlnaBackgroundService>();

var app = builder.Build();

// MARK: Configuration Validation
try
{
    var validator = app.Services.GetRequiredService<ConfigurationValidator>();
    validator.ValidateConfiguration(app.Configuration);
}
catch (InvalidOperationException ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "Configuration validation failed: {Message}", ex.Message);
    throw;
}

// MARK: Test services on startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try 
    {
        var xmlService = scope.ServiceProvider.GetRequiredService<XmlTemplateService>();
        var testTemplate = xmlService.GetTemplate("BrowseResponse", "test", 1, 1);
        logger.LogInformation("XmlTemplateService working - template length: {Length}", testTemplate.Length);
        logger.LogDebug("Test template content: {Content}", testTemplate.Substring(0, Math.Min(200, testTemplate.Length)));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "XmlTemplateService failed to load templates: {Error}", ex.Message);
        logger.LogError("This will cause DLNA browsing to fail - check Templates folder and .csproj embedded resources");
    }

    try
    {
        var deviceProfileService = scope.ServiceProvider.GetRequiredService<DeviceProfileService>();
        var testProfile = await deviceProfileService.GetProfileAsync("SEC_HHP_Samsung/1.0", null, "Samsung", "Smart TV", null);
        logger.LogInformation("DeviceProfileService working - created profile: {ProfileName}", testProfile?.Name ?? "None");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "DeviceProfileService failed to initialize");
    }

    try
    {
        var diagnosticService = scope.ServiceProvider.GetRequiredService<DiagnosticService>();
        logger.LogInformation("DiagnosticService registered successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "DiagnosticService failed to initialize");
    }

    try
    {
        var contentBuilderService = scope.ServiceProvider.GetRequiredService<ContentBuilderService>();
        logger.LogInformation("ContentBuilderService registered successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "ContentBuilderService failed to initialize");
    }
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapFallbackToFile("index.html");

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("FinDLNA starting on http://localhost:5000");

app.Run("http://localhost:5000");