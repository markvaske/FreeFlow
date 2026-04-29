using System.IO;
using FluentFTP;
using FreeFlow.Core.Models;
using Serilog;

namespace FreeFlow.Core.Services;

public class FileWatcherService : IDisposable
{
    private readonly AppSettings _settings;
    private FileSystemWatcher? _watcher;
    private bool _isRunning;
    private readonly object _changeLock = new();
    private CancellationTokenSource? _changeCts;
    private bool _disposed;

    public event Action<UploadResult>? UploadCompleted;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? StatusChanged;

    public FileWatcherService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool Start()
    {
        if (_isRunning || string.IsNullOrEmpty(_settings.WatchedFilePath))
            return false;

        _watcher = new FileSystemWatcher
        {
            Path = Path.GetDirectoryName(_settings.WatchedFilePath)!,
            Filter = Path.GetFileName(_settings.WatchedFilePath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
        _isRunning = true;

        StatusChanged?.Invoke("Watching for file changes...");
        Log.Information("File watcher started for {File}", _settings.WatchedFilePath);
        return true;
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_changeLock)
        {
            wasRunning = _isRunning;
            _isRunning = false;
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = null;
        }

        _watcher?.Dispose();
        _watcher = null;
        if (wasRunning)
            StatusChanged?.Invoke("Stopped");
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_changeLock)
        {
            if (!_isRunning)
                return;
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = new CancellationTokenSource();
            token = _changeCts.Token;
        }

        try
        {
            StatusChanged?.Invoke("File changed — waiting for stability...");

            await Task.Delay(TimeSpan.FromSeconds(_settings.SettleDelaySeconds), token);

            var ready = await WaitForFileToBeReady(
                e.FullPath,
                stableWindow: TimeSpan.FromSeconds(1),
                maxWait: TimeSpan.FromSeconds(Math.Max(10, _settings.SettleDelaySeconds * 10)),
                token
            );

            if (!ready)
            {
                ErrorOccurred?.Invoke("File did not become stable in time. Skipping upload.");
                return;
            }

            StatusChanged?.Invoke("Uploading...");
            await UploadToAllDestinations(e.FullPath);
        }
        catch (OperationCanceledException)
        {
            // Expected when multiple change events fire quickly (debounce).
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during file change handling");
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private static async Task<bool> WaitForFileToBeReady(
        string filePath,
        TimeSpan stableWindow,
        TimeSpan maxWait,
        CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(250);

        FileInfo? prev = null;
        while (DateTimeOffset.UtcNow - start < maxWait)
        {
            token.ThrowIfCancellationRequested();

            FileInfo current;
            try
            {
                current = new FileInfo(filePath);
                if (!current.Exists)
                {
                    await Task.Delay(pollInterval, token);
                    continue;
                }

                using var _ = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch
            {
                await Task.Delay(pollInterval, token);
                continue;
            }

            if (prev is not null &&
                prev.Length == current.Length &&
                prev.LastWriteTimeUtc == current.LastWriteTimeUtc)
            {
                await Task.Delay(stableWindow, token);

                var confirm = new FileInfo(filePath);
                if (confirm.Exists &&
                    confirm.Length == current.Length &&
                    confirm.LastWriteTimeUtc == current.LastWriteTimeUtc)
                {
                    return true;
                }
            }

            prev = current;
            await Task.Delay(pollInterval, token);
        }

        return false;
    }

    private async Task UploadToAllDestinations(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var tasks = new List<Task>();

        foreach (var dest in _settings.Destinations.Where(d => d.IsEnabled))
        {
            tasks.Add(UploadToDestination(filePath, dest, fileInfo));
        }

        await Task.WhenAll(tasks);
    }

    private async Task UploadToDestination(string filePath, FtpDestination dest, FileInfo fileInfo)
    {
        if (dest.Protocol == FtpProtocol.Sftp)
        {
            var sftp = new UploadResult
            {
                FileName = fileInfo.Name,
                Success = false,
                ErrorMessage = "SFTP is not yet supported.",
                DestinationName = dest.Name
            };
            UploadCompleted?.Invoke(sftp);
            ErrorOccurred?.Invoke($"Skipped upload to {dest.Name}: SFTP is not yet supported.");
            return;
        }

        try
        {
            using var client = new AsyncFtpClient(dest.Host, dest.Username, dest.Password, dest.Port);
            FtpClientOptions.Configure(client, dest.Protocol);

            await client.Connect();

            var remoteFile = FtpPath.Combine(dest.RemotePath, fileInfo.Name);
            await client.UploadFile(filePath, remoteFile, FtpRemoteExists.Overwrite, true);

            var result = new UploadResult
            {
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                Success = true,
                DestinationName = dest.Name
            };

            UploadCompleted?.Invoke(result);
            Log.Information("Uploaded to {Destination}", dest.Name);
        }
        catch (Exception ex)
        {
            var result = new UploadResult
            {
                FileName = fileInfo.Name,
                Success = false,
                ErrorMessage = ex.Message,
                DestinationName = dest.Name
            };
            UploadCompleted?.Invoke(result);
            ErrorOccurred?.Invoke($"Failed to upload to {dest.Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}