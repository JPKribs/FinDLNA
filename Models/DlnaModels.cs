namespace FinDLNA.Models;

// MARK: DlnaDevice
public class DlnaDevice
{
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public string FriendlyName { get; set; } = "FinDLNA Server";
    public string Manufacturer { get; set; } = "FinDLNA";
    public string ModelName { get; set; } = "FinDLNA Media Server";
    public string ModelNumber { get; set; } = "1.0.0";
    public int Port { get; set; } = 8200;
}

// MARK: BrowseResult
public class BrowseResult
{
    public string DidlXml { get; set; } = string.Empty;
    public int NumberReturned { get; set; }
    public int TotalMatches { get; set; }
}