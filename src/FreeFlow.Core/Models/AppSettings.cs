namespace FreeFlow.Core.Models;

public class AppSettings
{
    public string WatchedFilePath { get; set; } = string.Empty;
    public int SettleDelaySeconds { get; set; } = 4;
    public bool AutoStartOnLaunch { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public List<FtpDestination> Destinations { get; set; } = new();
}
