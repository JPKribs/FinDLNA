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

// MARK: DlnaContainer
public class DlnaContainer
{
    public string Id { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public int ChildCount { get; set; }
    public bool Restricted { get; set; } = true;
    public bool Searchable { get; set; } = true;
}

// MARK: DlnaItem
public class DlnaItem
{
    public string Id { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Duration { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public bool Restricted { get; set; } = true;
}

// MARK: BrowseResult
public class BrowseResult
{
    public string DidlXml { get; set; } = string.Empty;
    public int NumberReturned { get; set; }
    public int TotalMatches { get; set; }
}