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

        _logger.LogDebug("Loading template: {ResourceName}", resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger.LogError("Template resource not found: {ResourceName}", resourceName);
            throw new FileNotFoundException($"Template '{templateName}' not found as embedded resource");
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        
        _logger.LogDebug("Loaded template {TemplateName}: {Length} characters", templateName, content.Length);
        return content;
    }

    // MARK: CreateErrorTemplate
    private string CreateErrorTemplate(string templateName)
    {
        return $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                <s:Body>
                    <s:Fault>
                        <faultcode>s:Server</faultcode>
                        <faultstring>Template Error: {templateName} not found</faultstring>
                    </s:Fault>
                </s:Body>
            </s:Envelope>
            """;
    }

    // MARK: ClearCache
    public void ClearCache()
    {
        _templates.Clear();
        _logger.LogInformation("Template cache cleared");
    }
}