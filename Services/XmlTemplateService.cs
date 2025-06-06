using Microsoft.Extensions.Logging;
using System.Reflection;

namespace FinDLNA.Services;

// MARK: XmlTemplateService
public class XmlTemplateService
{
    private readonly ILogger<XmlTemplateService> _logger;
    private readonly Dictionary<string, string> _templates = new();
    private readonly Assembly _assembly = Assembly.GetExecutingAssembly();

    public XmlTemplateService(ILogger<XmlTemplateService> logger)
    {
        _logger = logger;
    }

    // MARK: GetTemplate
    public string GetTemplate(string templateName, params object[] args)
    {
        try
        {
            var template = GetCachedTemplate(templateName);
            return args.Length > 0 ? string.Format(template, args) : template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template {TemplateName}", templateName);
            return CreateErrorTemplate(templateName);
        }
    }

    // MARK: GetCachedTemplate
    private string GetCachedTemplate(string templateName)
    {
        if (_templates.TryGetValue(templateName, out var cachedTemplate))
        {
            return cachedTemplate;
        }

        var template = LoadTemplate(templateName);
        _templates[templateName] = template;
        return template;
    }

    // MARK: LoadTemplate
    private string LoadTemplate(string templateName)
    {
        var resourceName = $"FinDLNA.Templates.{templateName}.xml";
        
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            LogTemplateNotFound(templateName, resourceName);
            throw new FileNotFoundException($"Template '{templateName}' not found as embedded resource");
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogError("Template {TemplateName} is empty", templateName);
            throw new InvalidDataException($"Template '{templateName}' is empty");
        }

        _logger.LogDebug("Loaded template {TemplateName}: {Length} characters", templateName, content.Length);
        return content;
    }

    // MARK: LogTemplateNotFound
    private void LogTemplateNotFound(string templateName, string resourceName)
    {
        _logger.LogError("Template resource not found: {ResourceName}", resourceName);
        
        var allResources = _assembly.GetManifestResourceNames();
        var possibleMatches = allResources.Where(r => r.Contains(templateName, StringComparison.OrdinalIgnoreCase)).ToList();
        
        if (possibleMatches.Count > 0)
        {
            _logger.LogInformation("Possible template matches found: {Matches}", string.Join(", ", possibleMatches));
        }
        else
        {
            _logger.LogWarning("No template matches found. Available resources: {Count}", allResources.Length);
        }
    }

    // MARK: CreateErrorTemplate
    private string CreateErrorTemplate(string templateName)
    {
        _logger.LogWarning("Returning error template for missing {TemplateName}", templateName);
        
        return $"""
            <?xml version="1.0"?>
            <error>
                <message>Template '{templateName}' not found</message>
                <timestamp>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</timestamp>
            </error>
            """;
    }

    // MARK: ClearCache
    public void ClearCache()
    {
        _templates.Clear();
        _logger.LogInformation("Template cache cleared");
    }

    // MARK: IsTemplateAvailable
    public bool IsTemplateAvailable(string templateName)
    {
        if (_templates.ContainsKey(templateName))
            return true;

        var resourceName = $"FinDLNA.Templates.{templateName}.xml";
        return _assembly.GetManifestResourceStream(resourceName) != null;
    }

    // MARK: GetAvailableTemplates
    public IEnumerable<string> GetAvailableTemplates()
    {
        return _assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("FinDLNA.Templates.") && name.EndsWith(".xml"))
            .Select(name => name.Substring("FinDLNA.Templates.".Length, name.Length - "FinDLNA.Templates.".Length - ".xml".Length));
    }
}