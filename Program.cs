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
builder.Services.AddSingleton<ContentDirectoryService>();
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
    private readonly ILogger<DlnaBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public DlnaBackgroundService(
        DlnaService dlnaService, 
        JellyfinService jellyfinService, 
        ILogger<DlnaBackgroundService> logger,
        IServiceProvider serviceProvider)
    {
        _dlnaService = dlnaService;
        _jellyfinService = jellyfinService;
        _logger = logger;
        _serviceProvider = serviceProvider;
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
                    
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in DLNA service, restarting in 30 seconds");
                    await Task.Delay(30000, stoppingToken);
                }
            }
            else
            {
                _logger.LogDebug("Jellyfin not configured, waiting...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DLNA background service");
        await _dlnaService.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}