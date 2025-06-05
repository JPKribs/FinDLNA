using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FinDLNA.Services;

namespace FinDLNA.Controllers;

[ApiController]
[Route("api/[controller]")]
// MARK: DiagnosticController
public class DiagnosticController : ControllerBase
{
    private readonly DiagnosticService _diagnosticService;
    private readonly ILogger<DiagnosticController> _logger;

    public DiagnosticController(DiagnosticService diagnosticService, ILogger<DiagnosticController> logger)
    {
        _diagnosticService = diagnosticService;
        _logger = logger;
    }

    [HttpGet("run")]
    // MARK: RunDiagnostics
    public async Task<IActionResult> RunDiagnostics()
    {
        try
        {
            _logger.LogInformation("Diagnostic scan requested via API");
            var report = await _diagnosticService.RunFullDiagnosticsAsync();
            
            return Ok(new 
            { 
                success = true, 
                report = new
                {
                    timestamp = report.Timestamp,
                    summary = new
                    {
                        passed = report.Tests.Count(t => t.Status == TestStatus.Passed),
                        warnings = report.Tests.Count(t => t.Status == TestStatus.Warning),
                        failed = report.Tests.Count(t => t.Status == TestStatus.Failed)
                    },
                    tests = report.Tests.Select(t => new
                    {
                        name = t.Name,
                        status = t.Status.ToString(),
                        message = t.Message,
                        details = t.Details
                    }),
                    recommendations = report.Recommendations
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running diagnostics");
            return StatusCode(500, new { success = false, error = "Diagnostic scan failed" });
        }
    }

    [HttpGet("summary")]
    // MARK: GetDiagnosticSummary
    public async Task<IActionResult> GetDiagnosticSummary()
    {
        try
        {
            var report = await _diagnosticService.RunFullDiagnosticsAsync();
            return Ok(new 
            { 
                success = true, 
                summary = report.ToSummary(),
                passed = report.Tests.Count(t => t.Status == TestStatus.Passed),
                warnings = report.Tests.Count(t => t.Status == TestStatus.Warning),
                failed = report.Tests.Count(t => t.Status == TestStatus.Failed)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting diagnostic summary");
            return StatusCode(500, new { success = false, error = "Diagnostic summary failed" });
        }
    }
}