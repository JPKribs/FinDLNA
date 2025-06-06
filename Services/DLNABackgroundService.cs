using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FinDLNA.Services;

// MARK: DlnaBackgroundService
public class DlnaBackgroundService : BackgroundService
{
    private readonly DlnaService _dlnaService;
    private readonly JellyfinService _jellyfinService;
    private readonly ILogger<DlnaBackgroundService> _logger;
    private readonly IConfiguration _configuration;
    private Timer? _healthCheckTimer;
    private Timer? _cleanupTimer;

    public DlnaBackgroundService(
        DlnaService dlnaService, 
        JellyfinService jellyfinService,
        ILogger<DlnaBackgroundService> logger,
        IConfiguration configuration)
    {
        _dlnaService = dlnaService;
        _jellyfinService = jellyfinService;
        _logger = logger;
        _configuration = configuration;
    }

    // MARK: ExecuteAsync
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DLNA background service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_jellyfinService.IsConfigured)
            {
                try
                {
                    await StartDlnaServiceWithMonitoring(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("DLNA background service cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in DLNA service, restarting in 30 seconds");
                    await SafeStopDlnaService();
                    await DelayWithCancellation(TimeSpan.FromSeconds(30), stoppingToken);
                }
                finally
                {
                    DisposeTimers();
                }
            }
            else
            {
                _logger.LogDebug("Jellyfin not configured, waiting...");
                await DelayWithCancellation(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("DLNA background service stopped");
    }

    // MARK: StartDlnaServiceWithMonitoring
    private async Task StartDlnaServiceWithMonitoring(CancellationToken stoppingToken)
    {
        await _dlnaService.StartAsync();
        _logger.LogInformation("DLNA service started successfully");
        
        StartHealthMonitoring(stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested && _jellyfinService.IsConfigured)
        {
            await DelayWithCancellation(TimeSpan.FromSeconds(30), stoppingToken);
            
            if (!await IsServiceHealthy())
            {
                _logger.LogWarning("DLNA service appears unhealthy, restarting...");
                throw new InvalidOperationException("Service health check failed");
            }
            
            _logger.LogTrace("DLNA service health check passed");
        }
    }

    // MARK: StartHealthMonitoring
    private void StartHealthMonitoring(CancellationToken stoppingToken)
    {
        _healthCheckTimer = new Timer(async _ =>
        {
            if (stoppingToken.IsCancellationRequested) return;

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
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    // MARK: IsServiceHealthy
    private async Task<bool> IsServiceHealthy()
    {
        try
        {
            var port = int.Parse(_configuration["Dlna:Port"] ?? "8200");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            
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
            _logger.LogDebug(ex, "Health check failed with exception");
            return false;
        }
    }

    // MARK: SafeStopDlnaService
    private async Task SafeStopDlnaService()
    {
        try
        {
            await _dlnaService.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping DLNA service during restart");
        }
    }

    // MARK: DelayWithCancellation
    private static async Task DelayWithCancellation(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            throw;
        }
    }

    // MARK: DisposeTimers
    private void DisposeTimers()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
        
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
    }

    // MARK: StopAsync
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DLNA background service");
        
        DisposeTimers();
        
        try
        {
            await _dlnaService.StopAsync();
            _logger.LogInformation("DLNA service stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping DLNA service");
        }
        
        await base.StopAsync(cancellationToken);
    }
}