using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FinDLNA.Services;

namespace FinDLNA.Controllers;

[ApiController]
[Route("api/[controller]")]
// MARK: RefreshController
public class RefreshController : ControllerBase
{
    private readonly ILogger<RefreshController> _logger;
    private readonly SsdpService _ssdpService;
    private readonly DlnaService _dlnaService;

    public RefreshController(
        ILogger<RefreshController> logger,
        SsdpService ssdpService,
        DlnaService dlnaService)
    {
        _logger = logger;
        _ssdpService = ssdpService;
        _dlnaService = dlnaService;
    }

    [HttpPost("ssdp")]
    // MARK: RefreshSsdp
    public async Task<IActionResult> RefreshSsdp()
    {
        try
        {
            _logger.LogInformation("Manual SSDP refresh requested");
            await _ssdpService.SendManualAliveNotificationsAsync();
            return Ok(new { success = true, message = "SSDP refresh completed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SSDP refresh");
            return StatusCode(500, new { success = false, error = "SSDP refresh failed" });
        }
    }

    [HttpPost("dlna")]
    // MARK: RestartDlna
    public async Task<IActionResult> RestartDlna()
    {
        try
        {
            _logger.LogInformation("Manual DLNA restart requested");
            await _dlnaService.StopAsync();
            await _dlnaService.StartAsync();
            return Ok(new { success = true, message = "DLNA service restarted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during DLNA restart");
            return StatusCode(500, new { success = false, error = "DLNA restart failed" });
        }
    }
}