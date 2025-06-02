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

// MARK: Jellyfin SDK setup - proper way
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
    
    // Configure base URL from config
    var serverUrl = config["Jellyfin:ServerUrl"];
    if (!string.IsNullOrEmpty(serverUrl))
    {
        try
        {
            adapter.BaseUrl = serverUrl;
        }
        catch (Exception ex)
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(ex, "Failed to set base URL for Jellyfin client");
        }
    }
    
    return client;
});

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JellyfinService>();
builder.Services.AddSingleton<SsdpService>();
builder.Services.AddSingleton<ContentDirectoryService>();
builder.Services.AddSingleton<StreamingService>();
builder.Services.AddSingleton<DlnaService>();
builder.Services.AddHostedService<DlnaBackgroundService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapFallbackToFile("index.html");

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("FinDLNA starting on http://localhost:5000");

app.Run("http://localhost:5000");

// MARK: DlnaBackgroundService
public class DlnaBackgroundService : BackgroundService
{
    private readonly DlnaService _dlnaService;
    private readonly JellyfinService _jellyfinService;
    private readonly ILogger<DlnaBackgroundService> _logger;

    public DlnaBackgroundService(DlnaService dlnaService, JellyfinService jellyfinService, ILogger<DlnaBackgroundService> logger)
    {
        _dlnaService = dlnaService;
        _jellyfinService = jellyfinService;
        _logger = logger;
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
                    _logger.LogError(ex, "Error in DLNA service");
                }
            }
            else
            {
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _dlnaService.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}