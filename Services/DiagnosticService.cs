using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FinDLNA.Services;

namespace FinDLNA.Services;

// MARK: DiagnosticService
public class DiagnosticService
{
    private readonly ILogger<DiagnosticService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JellyfinService _jellyfinService;
    private readonly SsdpService _ssdpService;
    private readonly ContentDirectoryService _contentDirectoryService;

    public DiagnosticService(
        ILogger<DiagnosticService> logger,
        IConfiguration configuration,
        JellyfinService jellyfinService,
        SsdpService ssdpService,
        ContentDirectoryService contentDirectoryService)
    {
        _logger = logger;
        _configuration = configuration;
        _jellyfinService = jellyfinService;
        _ssdpService = ssdpService;
        _contentDirectoryService = contentDirectoryService;
    }

    // MARK: RunFullDiagnosticsAsync
    public async Task<DiagnosticReport> RunFullDiagnosticsAsync()
    {
        _logger.LogInformation("Starting full diagnostic scan");

        var report = new DiagnosticReport
        {
            Timestamp = DateTime.UtcNow,
            Tests = new List<DiagnosticTest>()
        };

        var tests = new (string Name, Func<Task<DiagnosticTest>> TestFunc)[]
        {
            ("Configuration", TestConfiguration),
            ("Network Connectivity", TestNetworkConnectivity),
            ("Jellyfin Connection", TestJellyfinConnection),
            ("Jellyfin Content", TestJellyfinContent),
            ("DLNA Service", TestDlnaService),
            ("SSDP Discovery", TestSsdpDiscovery),
            ("Content Directory", TestContentDirectory),
            ("XML Templates", TestXmlTemplates),
            ("Port Availability", TestPortAvailability),
            ("Network Interfaces", TestNetworkInterfaces)
        };

        foreach (var (name, testFunc) in tests)
        {
            try
            {
                _logger.LogDebug("Running diagnostic test: {TestName}", name);
                var test = await testFunc();
                test.Name = name;
                report.Tests.Add(test);
                
                _logger.LogDebug("Test {TestName} completed: {Status}", name, test.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic test {TestName} failed with exception", name);
                report.Tests.Add(new DiagnosticTest
                {
                    Name = name,
                    Status = TestStatus.Failed,
                    Message = $"Test failed with exception: {ex.Message}",
                    Details = ex.ToString()
                });
            }
        }

        GenerateRecommendations(report);
        
        _logger.LogInformation("Diagnostic scan completed with {PassedCount} passed, {WarningCount} warnings, {FailedCount} failed",
            report.Tests.Count(t => t.Status == TestStatus.Passed),
            report.Tests.Count(t => t.Status == TestStatus.Warning),
            report.Tests.Count(t => t.Status == TestStatus.Failed));

        return report;
    }

    // MARK: TestConfiguration
    private async Task<DiagnosticTest> TestConfiguration()
    {
        await Task.CompletedTask;
        
        var issues = new List<string>();
        var warnings = new List<string>();

        var jellyfinUrl = _configuration["Jellyfin:ServerUrl"];
        var jellyfinToken = _configuration["Jellyfin:AccessToken"];
        var jellyfinUserId = _configuration["Jellyfin:UserId"];
        var dlnaPort = _configuration["Dlna:Port"];
        var dlnaServerName = _configuration["Dlna:ServerName"];

        if (string.IsNullOrEmpty(jellyfinUrl))
            issues.Add("Jellyfin ServerUrl not configured");
        else if (!Uri.TryCreate(jellyfinUrl, UriKind.Absolute, out _))
            issues.Add("Jellyfin ServerUrl is not a valid URL");

        if (string.IsNullOrEmpty(jellyfinToken))
            issues.Add("Jellyfin AccessToken not configured");

        if (string.IsNullOrEmpty(jellyfinUserId))
            issues.Add("Jellyfin UserId not configured");
        else if (!Guid.TryParse(jellyfinUserId, out _))
            issues.Add("Jellyfin UserId is not a valid GUID");

        if (string.IsNullOrEmpty(dlnaPort))
            warnings.Add("DLNA Port not configured, using default 8200");
        else if (!int.TryParse(dlnaPort, out var port) || port < 1 || port > 65535)
            issues.Add($"DLNA Port '{dlnaPort}' is not a valid port number");

        if (string.IsNullOrEmpty(dlnaServerName))
            warnings.Add("DLNA ServerName not configured, using default");

        if (issues.Any())
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"Configuration issues found: {string.Join(", ", issues)}",
                Details = string.Join("\n", issues.Concat(warnings))
            };
        }

        if (warnings.Any())
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Warning,
                Message = $"Configuration warnings: {string.Join(", ", warnings)}",
                Details = string.Join("\n", warnings)
            };
        }

        return new DiagnosticTest
        {
            Status = TestStatus.Passed,
            Message = "All configuration values are valid"
        };
    }

    // MARK: TestNetworkConnectivity
    private async Task<DiagnosticTest> TestNetworkConnectivity()
    {
        var details = new List<string>();
        var issues = new List<string>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                           ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            details.Add($"Found {interfaces.Count} active network interfaces");

            var hasValidInterface = false;
            foreach (var ni in interfaces)
            {
                var properties = ni.GetIPProperties();
                var ipv4Addresses = properties.UnicastAddresses
                    .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(addr => addr.Address.ToString())
                    .ToList();

                if (ipv4Addresses.Any())
                {
                    hasValidInterface = true;
                    details.Add($"Interface {ni.Name}: {string.Join(", ", ipv4Addresses)}");
                }
            }

            if (!hasValidInterface)
            {
                issues.Add("No network interfaces with IPv4 addresses found");
            }

            var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 5000);
            
            if (reply.Status == IPStatus.Success)
            {
                details.Add($"Internet connectivity OK (ping to 8.8.8.8: {reply.RoundtripTime}ms)");
            }
            else
            {
                issues.Add($"Internet connectivity issue: {reply.Status}");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Network test failed: {ex.Message}");
        }

        return new DiagnosticTest
        {
            Status = issues.Any() ? TestStatus.Failed : TestStatus.Passed,
            Message = issues.Any() ? $"Network issues: {string.Join(", ", issues)}" : "Network connectivity OK",
            Details = string.Join("\n", details.Concat(issues))
        };
    }

    // MARK: TestJellyfinConnection
    private async Task<DiagnosticTest> TestJellyfinConnection()
    {
        var details = new List<string>();

        if (!_jellyfinService.IsConfigured)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = "Jellyfin service is not configured",
                Details = "Check Jellyfin ServerUrl, AccessToken, and UserId configuration"
            };
        }

        try
        {
            var connectionTest = await _jellyfinService.TestConnectionAsync();
            
            if (connectionTest)
            {
                details.Add("Jellyfin connection successful");
                
                var libraries = await _jellyfinService.GetLibraryFoldersAsync();
                if (libraries?.Any() == true)
                {
                    details.Add($"Found {libraries.Count} library folders");
                    foreach (var lib in libraries.Take(5))
                    {
                        details.Add($"  - {lib.Name} ({lib.CollectionType})");
                    }
                }
                else
                {
                    details.Add("Warning: No library folders found");
                }

                return new DiagnosticTest
                {
                    Status = TestStatus.Passed,
                    Message = "Jellyfin connection and libraries OK",
                    Details = string.Join("\n", details)
                };
            }
            else
            {
                return new DiagnosticTest
                {
                    Status = TestStatus.Failed,
                    Message = "Jellyfin connection failed",
                    Details = "Unable to connect to Jellyfin server. Check URL, token, and network connectivity."
                };
            }
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"Jellyfin connection test failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    // MARK: TestJellyfinContent
    private async Task<DiagnosticTest> TestJellyfinContent()
    {
        var details = new List<string>();
        var warnings = new List<string>();

        try
        {
            var libraries = await _jellyfinService.GetLibraryFoldersAsync();
            if (libraries?.Any() != true)
            {
                return new DiagnosticTest
                {
                    Status = TestStatus.Failed,
                    Message = "No Jellyfin libraries found",
                    Details = "Cannot test content without libraries"
                };
            }

            var totalItems = 0;
            var librariesWithContent = 0;

            foreach (var library in libraries.Take(3))
            {
                if (!library.Id.HasValue) continue;

                var content = await _jellyfinService.GetLibraryContentAsync(library.Id.Value);
                var itemCount = content?.Count ?? 0;
                totalItems += itemCount;

                if (itemCount > 0)
                {
                    librariesWithContent++;
                    details.Add($"Library '{library.Name}': {itemCount} items");
                    
                    if (itemCount > 0 && content != null)
                    {
                        var itemTypes = content
                            .Where(i => i.Type.HasValue)
                            .GroupBy(i => i.Type!.Value)
                            .ToDictionary(g => g.Key, g => g.Count());
                        foreach (var typeGroup in itemTypes.Take(3))
                        {
                            details.Add($"  - {typeGroup.Key}: {typeGroup.Value}");
                        }
                    }
                }
                else
                {
                    warnings.Add($"Library '{library.Name}' is empty");
                }
            }

            if (totalItems == 0)
            {
                return new DiagnosticTest
                {
                    Status = TestStatus.Warning,
                    Message = "No content found in Jellyfin libraries",
                    Details = string.Join("\n", warnings)
                };
            }

            return new DiagnosticTest
            {
                Status = warnings.Any() ? TestStatus.Warning : TestStatus.Passed,
                Message = $"Found {totalItems} items across {librariesWithContent} libraries",
                Details = string.Join("\n", details.Concat(warnings))
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"Content test failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    // MARK: TestDlnaService
    private async Task<DiagnosticTest> TestDlnaService()
    {
        var details = new List<string>();

        try
        {
            var dlnaPort = int.Parse(_configuration["Dlna:Port"] ?? "8200");
            
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            
            var deviceXmlUrl = $"http://localhost:{dlnaPort}/device.xml";
            var response = await client.GetAsync(deviceXmlUrl);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                details.Add($"DLNA service responding on port {dlnaPort}");
                details.Add($"Device description XML: {content.Length} characters");
                
                if (content.Contains("MediaServer"))
                {
                    details.Add("Device description contains MediaServer definition");
                }
                else
                {
                    details.Add("Warning: Device description may be malformed");
                }

                var contentDirUrl = $"http://localhost:{dlnaPort}/ContentDirectory/scpd.xml";
                var cdResponse = await client.GetAsync(contentDirUrl);
                if (cdResponse.IsSuccessStatusCode)
                {
                    details.Add("ContentDirectory service description available");
                }
                else
                {
                    details.Add($"Warning: ContentDirectory service not responding ({cdResponse.StatusCode})");
                }

                return new DiagnosticTest
                {
                    Status = TestStatus.Passed,
                    Message = "DLNA service is running and responding",
                    Details = string.Join("\n", details)
                };
            }
            else
            {
                return new DiagnosticTest
                {
                    Status = TestStatus.Failed,
                    Message = $"DLNA service not responding (HTTP {response.StatusCode})",
                    Details = $"Failed to connect to {deviceXmlUrl}"
                };
            }
        }
        catch (HttpRequestException ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = "DLNA service not accessible",
                Details = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"DLNA service test failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    // MARK: TestSsdpDiscovery
    private async Task<DiagnosticTest> TestSsdpDiscovery()
    {
        var details = new List<string>();
        
        try
        {
            details.Add("Testing SSDP multicast capability");
            
            using var testClient = new UdpClient();
            testClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            try
            {
                testClient.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
                details.Add("Successfully joined SSDP multicast group");
                testClient.DropMulticastGroup(IPAddress.Parse("239.255.255.250"));
            }
            catch (Exception ex)
            {
                details.Add($"Multicast join failed: {ex.Message}");
                return new DiagnosticTest
                {
                    Status = TestStatus.Failed,
                    Message = "SSDP multicast not available",
                    Details = string.Join("\n", details)
                };
            }

            var deviceXml = _ssdpService.GetDeviceDescriptionXml();
            if (!string.IsNullOrEmpty(deviceXml))
            {
                details.Add($"Device description XML generated ({deviceXml.Length} chars)");
                
                if (deviceXml.Contains("MediaServer"))
                {
                    details.Add("Device XML contains MediaServer device type");
                }
                
                if (deviceXml.Contains("ContentDirectory"))
                {
                    details.Add("Device XML contains ContentDirectory service");
                }
            }
            else
            {
                details.Add("Warning: Device description XML is empty");
            }

            await _ssdpService.SendManualAliveNotificationsAsync();
            details.Add("Manual SSDP alive notifications sent successfully");

            return new DiagnosticTest
            {
                Status = TestStatus.Passed,
                Message = "SSDP discovery system operational",
                Details = string.Join("\n", details)
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"SSDP test failed: {ex.Message}",
                Details = string.Join("\n", details) + "\n" + ex.ToString()
            };
        }
    }

    // MARK: TestContentDirectory
    private async Task<DiagnosticTest> TestContentDirectory()
    {
        var details = new List<string>();
        
        try
        {
            var testSoapRequest = """
                <?xml version="1.0"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                    <s:Body>
                        <u:Browse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                            <ObjectID>0</ObjectID>
                            <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                            <Filter>*</Filter>
                            <StartingIndex>0</StartingIndex>
                            <RequestedCount>10</RequestedCount>
                            <SortCriteria></SortCriteria>
                        </u:Browse>
                    </s:Body>
                </s:Envelope>
                """;

            var response = await _contentDirectoryService.ProcessBrowseRequestAsync(testSoapRequest, "FinDLNA-Diagnostic/1.0");
            
            if (!string.IsNullOrEmpty(response))
            {
                details.Add($"Browse request processed successfully ({response.Length} chars)");
                
                if (response.Contains("BrowseResponse"))
                {
                    details.Add("Response contains valid BrowseResponse");
                }
                
                if (response.Contains("DIDL-Lite"))
                {
                    details.Add("Response contains DIDL-Lite metadata");
                }
                
                if (response.Contains("NumberReturned"))
                {
                    var numberReturnedStart = response.IndexOf("<NumberReturned>") + 16;
                    var numberReturnedEnd = response.IndexOf("</NumberReturned>");
                    if (numberReturnedStart > 15 && numberReturnedEnd > numberReturnedStart)
                    {
                        var numberReturned = response.Substring(numberReturnedStart, numberReturnedEnd - numberReturnedStart);
                        details.Add($"Browse returned {numberReturned} items");
                    }
                }
                
                return new DiagnosticTest
                {
                    Status = TestStatus.Passed,
                    Message = "ContentDirectory service responding correctly",
                    Details = string.Join("\n", details)
                };
            }
            else
            {
                return new DiagnosticTest
                {
                    Status = TestStatus.Failed,
                    Message = "ContentDirectory returned empty response",
                    Details = "Browse request failed to return any data"
                };
            }
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"ContentDirectory test failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    // MARK: TestXmlTemplates
    private async Task<DiagnosticTest> TestXmlTemplates()
    {
        await Task.CompletedTask;
        
        var details = new List<string>();
        var templateNames = new[]
        {
            "DeviceDescription",
            "ContentDirectoryServiceDescription",
            "BrowseResponse",
            "DidlLiteTemplate",
            "ItemTemplate",
            "ContainerTemplate"
        };

        var failedTemplates = new List<string>();

        foreach (var templateName in templateNames)
        {
            try
            {
                var template = GetXmlTemplateForTest(templateName);
                if (!string.IsNullOrEmpty(template))
                {
                    details.Add($"Template '{templateName}': {template.Length} chars");
                }
                else
                {
                    failedTemplates.Add(templateName);
                }
            }
            catch (Exception ex)
            {
                failedTemplates.Add($"{templateName} ({ex.Message})");
            }
        }

        if (failedTemplates.Any())
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"Missing XML templates: {string.Join(", ", failedTemplates)}",
                Details = string.Join("\n", details)
            };
        }

        return new DiagnosticTest
        {
            Status = TestStatus.Passed,
            Message = $"All {templateNames.Length} XML templates loaded successfully",
            Details = string.Join("\n", details)
        };
    }

    // MARK: TestPortAvailability
    private async Task<DiagnosticTest> TestPortAvailability()
    {
        await Task.CompletedTask;
        
        var details = new List<string>();
        var issues = new List<string>();

        var dlnaPort = int.Parse(_configuration["Dlna:Port"] ?? "8200");
        var webPort = int.Parse(_configuration["WebInterface:Port"] ?? "5000");
        var ssdpPort = 1900;

        var ports = new[]
        {
            (dlnaPort, "DLNA Service"),
            (webPort, "Web Interface"),
            (ssdpPort, "SSDP Discovery")
        };

        foreach (var (port, description) in ports)
        {
            try
            {
                var tcpEndpoint = new IPEndPoint(IPAddress.Any, port);
                using var tcpListener = new TcpListener(tcpEndpoint);
                tcpListener.Start();
                tcpListener.Stop();
                details.Add($"Port {port} ({description}): Available");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                if (port == dlnaPort || port == webPort)
                {
                    details.Add($"Port {port} ({description}): In use (possibly by FinDLNA)");
                }
                else
                {
                    issues.Add($"Port {port} ({description}): Already in use");
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Port {port} ({description}): Error - {ex.Message}");
            }
        }

        return new DiagnosticTest
        {
            Status = issues.Any() ? TestStatus.Warning : TestStatus.Passed,
            Message = issues.Any() ? $"Port issues: {string.Join(", ", issues)}" : "All required ports available",
            Details = string.Join("\n", details.Concat(issues))
        };
    }

    // MARK: TestNetworkInterfaces
    private async Task<DiagnosticTest> TestNetworkInterfaces()
    {
        await Task.CompletedTask;
        
        var details = new List<string>();
        var warnings = new List<string>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            details.Add($"Total network interfaces: {interfaces.Length}");

            var activeInterfaces = interfaces
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .ToList();
            details.Add($"Active interfaces: {activeInterfaces.Count}");

            var multicastCapable = 0;
            var ipv4Interfaces = 0;

            foreach (var ni in activeInterfaces)
            {
                var properties = ni.GetIPProperties();
                var hasIPv4 = properties.UnicastAddresses
                    .Any(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                               !IPAddress.IsLoopback(addr.Address));

                if (hasIPv4)
                {
                    ipv4Interfaces++;
                    details.Add($"Interface '{ni.Name}': IPv4 enabled, Type: {ni.NetworkInterfaceType}");
                }

                if (ni.SupportsMulticast)
                {
                    multicastCapable++;
                }
            }

            if (ipv4Interfaces == 0)
            {
                warnings.Add("No IPv4-enabled network interfaces found");
            }

            if (multicastCapable == 0)
            {
                warnings.Add("No multicast-capable interfaces found (SSDP may not work)");
            }

            details.Add($"IPv4-enabled interfaces: {ipv4Interfaces}");
            details.Add($"Multicast-capable interfaces: {multicastCapable}");

            return new DiagnosticTest
            {
                Status = warnings.Any() ? TestStatus.Warning : TestStatus.Passed,
                Message = warnings.Any() ? $"Network warnings: {string.Join(", ", warnings)}" : "Network interfaces OK",
                Details = string.Join("\n", details.Concat(warnings))
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticTest
            {
                Status = TestStatus.Failed,
                Message = $"Network interface test failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    // MARK: GenerateRecommendations
    private void GenerateRecommendations(DiagnosticReport report)
    {
        var recommendations = new List<string>();

        var failedTests = report.Tests.Where(t => t.Status == TestStatus.Failed).ToList();
        var warningTests = report.Tests.Where(t => t.Status == TestStatus.Warning).ToList();

        if (failedTests.Any(t => t.Name == "Configuration"))
        {
            recommendations.Add("Fix configuration issues in appsettings.json before proceeding");
        }

        if (failedTests.Any(t => t.Name == "Jellyfin Connection"))
        {
            recommendations.Add("Verify Jellyfin server is running and accessible at the configured URL");
            recommendations.Add("Check that the AccessToken is valid and not expired");
        }

        if (failedTests.Any(t => t.Name == "DLNA Service"))
        {
            recommendations.Add("Restart the DLNA service or check for port conflicts");
        }

        if (failedTests.Any(t => t.Name == "SSDP Discovery"))
        {
            recommendations.Add("Check firewall settings and multicast routing");
            recommendations.Add("Ensure the application has sufficient network permissions");
        }

        if (warningTests.Any(t => t.Name == "Network Interfaces"))
        {
            recommendations.Add("Check network adapter settings and multicast support");
        }

        if (failedTests.Any(t => t.Name == "XML Templates"))
        {
            recommendations.Add("Verify that XML template files are included as embedded resources");
        }

        if (warningTests.Any(t => t.Name == "Jellyfin Content"))
        {
            recommendations.Add("Add media content to Jellyfin libraries for DLNA sharing");
        }

        report.Recommendations = recommendations;
    }

    // MARK: GetXmlTemplateForTest
    private string GetXmlTemplateForTest(string templateName)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = $"FinDLNA.Templates.{templateName}.xml";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return "";
                
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }
}

// MARK: DiagnosticReport
public class DiagnosticReport
{
    public DateTime Timestamp { get; set; }
    public List<DiagnosticTest> Tests { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public string ToSummary()
    {
        var passed = Tests.Count(t => t.Status == TestStatus.Passed);
        var warnings = Tests.Count(t => t.Status == TestStatus.Warning);
        var failed = Tests.Count(t => t.Status == TestStatus.Failed);

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Diagnostic Report - {Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        summary.AppendLine($"Tests: {passed} passed, {warnings} warnings, {failed} failed");
        summary.AppendLine();

        if (failed > 0)
        {
            summary.AppendLine("FAILED TESTS:");
            foreach (var test in Tests.Where(t => t.Status == TestStatus.Failed))
            {
                summary.AppendLine($"  ❌ {test.Name}: {test.Message}");
            }
            summary.AppendLine();
        }

        if (warnings > 0)
        {
            summary.AppendLine("WARNINGS:");
            foreach (var test in Tests.Where(t => t.Status == TestStatus.Warning))
            {
                summary.AppendLine($"  ⚠️  {test.Name}: {test.Message}");
            }
            summary.AppendLine();
        }

        if (Recommendations.Any())
        {
            summary.AppendLine("RECOMMENDATIONS:");
            foreach (var recommendation in Recommendations)
            {
                summary.AppendLine($"  💡 {recommendation}");
            }
        }

        return summary.ToString();
    }
}

// MARK: DiagnosticTest
public class DiagnosticTest
{
    public string Name { get; set; } = "";
    public TestStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string? Details { get; set; }
}

// MARK: TestStatus
public enum TestStatus
{
    Passed,
    Warning,
    Failed
}