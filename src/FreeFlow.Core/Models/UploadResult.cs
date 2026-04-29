namespace FreeFlow.Core.Models;

public class UploadResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string DestinationName { get; set; } = string.Empty;
}
