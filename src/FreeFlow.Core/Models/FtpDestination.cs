namespace FreeFlow.Core.Models;

public enum FtpProtocol
{
    Ftp,
    Ftps,
    Sftp
}

public class FtpDestination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // Encrypted at rest using DPAPI on Windows
    public string RemotePath { get; set; } = "/";
    public FtpProtocol Protocol { get; set; } = FtpProtocol.Ftps;
    public bool IsEnabled { get; set; } = true;
}
