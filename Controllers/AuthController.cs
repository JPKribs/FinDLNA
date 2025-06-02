using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FinDLNA.Models;
using FinDLNA.Services;

namespace FinDLNA.Controllers;

[ApiController]
[Route("api/[controller]")]
// MARK: AuthController
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpPost("login")]
    // MARK: Login
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ServerUrl))
            {
                return BadRequest(new { success = false, error = "Server URL is required" });
            }

            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return BadRequest(new { success = false, error = "UserId is required" });
            }

            var result = await _authService.AuthenticateAsync(request);
            
            if (result.Success)
            {
                return Ok(new { success = true, message = "Authentication successful" });
            }
            
            return BadRequest(new { success = false, error = result.Error });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login endpoint error");
            return StatusCode(500, new { success = false, error = "Internal server error" });
        }
    }

    [HttpGet("status")]
    // MARK: GetStatus
    public IActionResult GetStatus()
    {
        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var serverUrl = config["Jellyfin:ServerUrl"];
        var hasToken = !string.IsNullOrEmpty(config["Jellyfin:AccessToken"]);
        
        return Ok(new { 
            configured = hasToken && !string.IsNullOrEmpty(serverUrl),
            serverUrl = serverUrl 
        });
    }
}