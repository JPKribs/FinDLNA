using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Kiota.Abstractions.Authentication;
using Jellyfin.Sdk;
using FinDLNA.Services;
using FinDLNA.Models;
using FinDLNA.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JellyfinClientOptions>(
    builder.Configuration.GetSection("JellyfinClient"));

builder.Services.AddControllers();
builder.Services.AddHttpClient();

// MARK: Database configuration
builder.Services.AddDbContext<DlnaContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Data Source=findlna.db";
    options.UseSqlite(connectionString);
});

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
builder.Services.AddSingleton<XmlTemplateService>();

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

// MARK: Service registration
builder.Services.AddScoped<DeviceProfileService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JellyfinService>();
builder.Services.AddSingleton<SsdpService>();
builder.Services.AddScoped<ContentDirectoryService>();
builder.Services.AddSingleton<PlaybackReportingService>();
builder.Services.AddSingleton<StreamingService>();
builder.Services.AddSingleton<DlnaService>();
builder.Services.AddHostedService<DlnaBackgroundService>();

var app = builder.Build();

// MARK: Database initialization
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DlnaContext>();
    var dbLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        await context.Database.EnsureCreatedAsync();
        dbLogger.LogInformation("Database initialized successfully");
        
        var deviceProfileService = scope.ServiceProvider.GetRequiredService<DeviceProfileService>();
        await deviceProfileService.InitializeDefaultProfilesAsync();
        dbLogger.LogInformation("Device profiles initialized");
    }
    catch (Exception ex)
    {
        dbLogger.LogError(ex, "Failed to initialize database");
    }

    // MARK: Test XML Template Service
    try 
    {
        var xmlService = scope.ServiceProvider.GetRequiredService<XmlTemplateService>();
        var testTemplate = xmlService.GetTemplate("BrowseResponse", "test", 1, 1);
        dbLogger.LogInformation("XmlTemplateService working - template length: {Length}", testTemplate.Length);
        dbLogger.LogDebug("Test template content: {Content}", testTemplate.Substring(0, Math.Min(200, testTemplate.Length)));
    }
    catch (Exception ex)
    {
        dbLogger.LogError(ex, "XmlTemplateService failed to load templates: {Error}", ex.Message);
        dbLogger.LogError("This will cause DLNA browsing to fail - check Templates folder and .csproj embedded resources");
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
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private Timer? _healthCheckTimer;
    private Timer? _cleanupTimer;

    public DlnaBackgroundService(
        DlnaService dlnaService, 
        JellyfinService jellyfinService,
        PlaybackReportingService playbackReportingService,
        ILogger<DlnaBackgroundService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _dlnaService = dlnaService;
        _jellyfinService = jellyfinService;
        _playbackReportingService = playbackReportingService;
        _logger = logger;
        _serviceProvider = serviceProvider;
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