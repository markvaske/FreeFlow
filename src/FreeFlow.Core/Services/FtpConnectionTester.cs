using FluentFTP;
using FreeFlow.Core.Models;

namespace FreeFlow.Core.Services;

public static class FtpConnectionTester
{
    public static async Task<ConnectionTestResult> TestAsync(
        FtpDestination destination,
        Action<string>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        if (destination.Protocol == FtpProtocol.Sftp)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = "SFTP is not implemented yet. Choose FTP or FTPS for MVP."
            };
        }

        if (string.IsNullOrWhiteSpace(destination.Host) ||
            string.IsNullOrWhiteSpace(destination.Username) ||
            string.IsNullOrWhiteSpace(destination.Password))
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = "Host, Username, and Password are required before testing."
            };
        }

        var testFileName = $".freeflow-test-{Guid.NewGuid():N}.txt";
        var localTemp = Path.Combine(Path.GetTempPath(), testFileName);

        try
        {
            using var client = new AsyncFtpClient(
                destination.Host,
                destination.Username,
                destination.Password,
                destination.Port);

            FtpClientOptions.Configure(client, destination.Protocol);

            statusChanged?.Invoke("Connecting...");
            cancellationToken.ThrowIfCancellationRequested();
            await client.Connect();

            var remoteDir = FtpPath.NormalizeDirectory(destination.RemotePath);
            statusChanged?.Invoke("Checking remote folder...");
            cancellationToken.ThrowIfCancellationRequested();
            if (!await client.DirectoryExists(remoteDir))
            {
                return new ConnectionTestResult
                {
                    Success = false,
                    Message = $"Remote folder does not exist: {remoteDir}"
                };
            }

            var remoteFile = FtpPath.Combine(remoteDir, testFileName);
            await File.WriteAllTextAsync(localTemp, $"FreeFlow test upload at {DateTime.Now:O}", cancellationToken);

            statusChanged?.Invoke("Uploading test file...");
            cancellationToken.ThrowIfCancellationRequested();
            await client.UploadFile(localTemp, remoteFile, FtpRemoteExists.Overwrite, createRemoteDir: true);

            statusChanged?.Invoke("Cleaning up...");
            cancellationToken.ThrowIfCancellationRequested();
            await client.DeleteFile(remoteFile);

            return new ConnectionTestResult
            {
                Success = true,
                Message = "Connection and upload test passed."
            };
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = "Connection test was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            try { File.Delete(localTemp); } catch { /* best-effort cleanup */ }
        }
    }

}
