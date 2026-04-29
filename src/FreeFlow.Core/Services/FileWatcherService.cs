using System.IO;
using FluentFTP;
using FreeFlow.Core.Models;
using Serilog;

namespace FreeFlow.Core.Services;

public class FileWatcherService
{
    private readonly AppSettings _settings;
    private FileSystemWatcher? _watcher;
    private readonly SettingsService _settingsService;
    private bool _isRunning;
    private readonly object _changeLock = new();
    private CancellationTokenSource? _changeCts;

    public event Action<UploadResult>? UploadCompleted;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? StatusChanged;

    public FileWatcherService(AppSettings settings, SettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
    }

    public void Start()
    {
        if (_isRunning || string.IsNullOrEmpty(_settings.WatchedFilePath))
            return;

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
    }

    public void Stop()
    {
        lock (_changeLock)
        {
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = null;
        }

        _watcher?.Dispose();
        _watcher = null;
        _isRunning = false;
        StatusChanged?.Invoke("Stopped");
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_changeLock)
        {
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = new CancellationTokenSource();
            token = _changeCts.Token;
        }

        try
        {
            StatusChanged?.Invoke("File changed — waiting for stability...");

            // Debounce bursts of change events, then require the file to be stable
            // (size + last-write unchanged) before uploading.
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

                // If another process is still writing, this will typically throw.
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
                // Confirm stability holds for at least the stable window.
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
        try
        {
            using var client = new AsyncFtpClient(dest.Host, dest.Username, dest.Password, dest.Port);

            if (dest.Protocol == FtpProtocol.Ftps)
                client.Config.EncryptionMode = FtpEncryptionMode.Explicit;

            await client.Connect();

            var remoteFile = Path.Combine(dest.RemotePath, fileInfo.Name);
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
}
