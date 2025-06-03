using Microsoft.Extensions.Logging;
using System.Reflection;

namespace FinDLNA.Services;

// MARK: XmlTemplateService
public class XmlTemplateService
{
    private readonly ILogger<XmlTemplateService> _logger;
    private readonly Dictionary<string, string> _templates = new();

    public XmlTemplateService(ILogger<XmlTemplateService> logger)
    {
        _logger = logger;
        LogAvailableResources(); // MARK: Debug helper
    }

    // MARK: LogAvailableResources
    private void LogAvailableResources()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            
            _logger.LogInformation("Available embedded resources:");
            foreach (var resourceName in resourceNames)
            {
                _logger.LogInformation("  - {ResourceName}", resourceName);
            }
            
            if (resourceNames.Length == 0)
            {
                _logger.LogError("No embedded resources found! Check your .csproj file.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing embedded resources");
        }
    }

    // MARK: GetTemplate
    public string GetTemplate(string templateName, params object[] args)
    {
        try
        {
            if (!_templates.ContainsKey(templateName))
            {
                _templates[templateName] = LoadTemplate(templateName);
            }

            var template = _templates[templateName];
            
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogError("Template {TemplateName} is empty after loading", templateName);
                return CreateErrorTemplate(templateName);
            }

            return args.Length > 0 ? string.Format(template, args) : template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template {TemplateName}", templateName);
            return CreateErrorTemplate(templateName);
        }
    }

    // MARK: LoadTemplate
    private string LoadTemplate(string templateName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"FinDLNA.Templates.{templateName}.xml";

        _logger.LogDebug("Attempting to load template: {ResourceName}", resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger.LogError("Template resource not found: {ResourceName}", resourceName);
            
            // MARK: Try alternative resource names
            var allResources = assembly.GetManifestResourceNames();
            var possibleMatches = allResources.Where(r => r.Contains(templateName)).ToList();
            
            if (possibleMatches.Any())
            {
                _logger.LogInformation("Possible template matches found:");
                foreach (var match in possibleMatches)
                {
                    _logger.LogInformation("  - {Match}", match);
                }
            }
            
            throw new FileNotFoundException($"Template '{templateName}' not found as embedded resource");
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        
        _logger.LogInformation("Successfully loaded template {TemplateName}: {Length} characters", templateName, content.Length);
        _logger.LogDebug("Template content preview: {Preview}", content.Substring(0, Math.Min(100, content.Length)));
        
        return content;
    }

    // MARK: CreateErrorTemplate
    private string CreateErrorTemplate(string templateName)
    {
        var errorXml = $"""
            <?xml version="1.0"?>
            <error>
                <message>Template '{templateName}' not found</message>
                <timestamp>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</timestamp>
            </error>
            """;
            
        _logger.LogWarning("Returning error template for missing {TemplateName}", templateName);
        return errorXml;
    }

    // MARK: ClearCache
    public void ClearCache()
    {
        _templates.Clear();
        _logger.LogInformation("Template cache cleared");
    }
}