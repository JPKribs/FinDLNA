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

// MARK: Service registration - ORDER MATTERS!
builder.Services.AddSingleton<XmlTemplateService>();
builder.Services.AddSingleton<DeviceProfileService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JellyfinService>();
builder.Services.AddSingleton<SsdpService>();
builder.Services.AddSingleton<ContentDirectoryService>();
builder.Services.AddSingleton<PlaybackReportingService>();
builder.Services.AddSingleton<StreamingService>();
builder.Services.AddSingleton<DlnaService>();
builder.Services.AddSingleton<DiagnosticService>(); // MARK: Add DiagnosticService
builder.Services.AddHostedService<DlnaBackgroundService>();

var app = builder.Build();

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
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapFallbackToFile("index.html");

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("FinDLNA starting on http://localhost:5000");

app.Run("http://localhost:5000");

// MARK: DlnaBackgroundService
public class DlnaBackgroundService : BackgroundService
{
    private readonly DlnaService _dlnaService;
    private readonly JellyfinService _jellyfinService;
    private readonly PlaybackReportingService _playbackReportingService;
    private readonly ILogger<DlnaBackgroundService> _logger;
    private readonly IConfiguration _configuration;
    private Timer? _healthCheckTimer;
    private Timer? _cleanupTimer;

    public DlnaBackgroundService(
        DlnaService dlnaService, 
        JellyfinService jellyfinService,
        PlaybackReportingService playbackReportingService,
        ILogger<DlnaBackgroundService> logger,
        IConfiguration configuration)
    {
        _dlnaService = dlnaService;
        _jellyfinService = jellyfinService;
        _playbackReportingService = playbackReportingService;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_jellyfinService.IsConfigured)
            {
                try
                {
                    await _dlnaService.StartAsync();
                    _logger.LogInformation("DLNA service started successfully");
                    
                    StartHealthMonitoring(stoppingToken);
                    StartSessionCleanup(stoppingToken);
                    
                    while (!stoppingToken.IsCancellationRequested && _jellyfinService.IsConfigured)
                    {
                        try
                        {
                            await Task.Delay(30000, stoppingToken);
                            
                            if (!await IsServiceHealthy())
                            {
                                _logger.LogWarning("DLNA service appears unhealthy, restarting...");
                                throw new InvalidOperationException("Service health check failed");
                            }
                            
                            _logger.LogDebug("DLNA service health check passed");
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in DLNA service, restarting in 30 seconds");
                    
                    try
                    {
                        await _dlnaService.StopAsync();
                    }
                    catch (Exception stopEx)
                    {
                        _logger.LogWarning(stopEx, "Error stopping DLNA service during restart");
                    }
                    
                    await Task.Delay(30000, stoppingToken);
                }
                finally
                {
                    _healthCheckTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                }
            }
            else
            {
                _logger.LogDebug("Jellyfin not configured, waiting...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    // MARK: StartHealthMonitoring
    private void StartHealthMonitoring(CancellationToken stoppingToken)
    {
        _healthCheckTimer = new Timer(async _ =>
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var isHealthy = await IsServiceHealthy();
                    if (!isHealthy)
                    {
                        _logger.LogWarning("Health check timer detected unhealthy service");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check timer error");
                }
            }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    // MARK: StartSessionCleanup
    private void StartSessionCleanup(CancellationToken stoppingToken)
    {
        _cleanupTimer = new Timer(async _ =>
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _playbackReportingService.CleanupStaleSessionsAsync();
                    
                    var activeSessions = await _playbackReportingService.GetActiveSessionsAsync();
                    if (activeSessions.Count > 0)
                    {
                        _logger.LogDebug("Active playback sessions: {Count}", activeSessions.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session cleanup error");
                }
            }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // MARK: IsServiceHealthy
    private async Task<bool> IsServiceHealthy()
    {
        try
        {
            var port = int.Parse(_configuration["Dlna:Port"] ?? "8200");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            
            var response = await client.GetAsync($"http://localhost:{port}/device.xml");
            var isHealthy = response.IsSuccessStatusCode;
            
            if (!isHealthy)
            {
                _logger.LogWarning("Health check failed: HTTP {StatusCode}", response.StatusCode);
            }
            
            return isHealthy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed with exception");
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DLNA background service");
        
        _healthCheckTimer?.Dispose();
        _cleanupTimer?.Dispose();
        
        try
        {
            await _dlnaService.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping DLNA service");
        }
        
        await base.StopAsync(cancellationToken);
    }
}