using System.IO;
using FluentFTP;
using FreeFlow.Core.Models;
using Serilog;

namespace FreeFlow.Core.Services;

public sealed class FileWatcherService : IDisposable
{
    private const int MaxUploadAttempts = 3;

    private readonly AppSettings _settings;
    private readonly object _changeLock = new();
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _changeCts;
    private bool _isRunning;
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRunning || string.IsNullOrEmpty(_settings.WatchedFilePath))
            return false;

        var directory = Path.GetDirectoryName(_settings.WatchedFilePath);
        var fileName = Path.GetFileName(_settings.WatchedFilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            ErrorOccurred?.Invoke("The watched file path is invalid.");
            return false;
        }

        try
        {
            _watcher = new FileSystemWatcher
            {
                Path = directory,
                Filter = fileName,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke($"Unable to watch the file: {ex.Message}");
            return false;
        }

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;
        _isRunning = true;

        StatusChanged?.Invoke("Watching for file changes...");
        Log.Information("File watcher started for {File}", _settings.WatchedFilePath);
        return true;
    }

    public void Stop()
    {
        CancelPendingChange();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        var wasRunning = _isRunning;
        _isRunning = false;
        if (wasRunning)
            StatusChanged?.Invoke("Stopped");
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_changeLock)
        {
            if (!_isRunning || _disposed)
                return;

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
            await _uploadLock.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                await UploadToAllDestinations(e.FullPath, token);
            }
            finally
            {
                _uploadLock.Release();
            }
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

    private void OnFileRenamed(object sender, RenamedEventArgs e) => OnFileChanged(sender, e);

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
                    confirm.LastWriteTimeUtc == current.LastWriteTimeUtc &&
                    CanOpenExclusive(filePath))
                {
                    return true;
                }
            }

            prev = current;
            await Task.Delay(pollInterval, token);
        }

        return false;
    }

    private static bool CanOpenExclusive(string filePath)
    {
        try
        {
            using var _ = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UploadToAllDestinations(string filePath, CancellationToken token)
    {
        var fileInfo = new FileInfo(filePath);
        var tasks = new List<Task>();

        foreach (var dest in _settings.Destinations.Where(d => d.IsEnabled))
        {
            tasks.Add(UploadToDestination(filePath, dest, fileInfo, token));
        }

        await Task.WhenAll(tasks);
    }

    private async Task UploadToDestination(
        string filePath,
        FtpDestination dest,
        FileInfo fileInfo,
        CancellationToken token)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxUploadAttempts; attempt++)
        {
            try
            {
                using var client = new AsyncFtpClient(dest.Host, dest.Username, dest.Password, dest.Port);

                FtpClientOptions.Configure(client, dest.Protocol);

                token.ThrowIfCancellationRequested();
                await client.Connect();

                var remoteFile = FtpPath.Combine(dest.RemotePath, fileInfo.Name);
                token.ThrowIfCancellationRequested();
                await client.UploadFile(filePath, remoteFile, FtpRemoteExists.Overwrite, true);

                var result = new UploadResult
                {
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    Success = true,
                    DestinationName = dest.Name
                };

                UploadCompleted?.Invoke(result);
                Log.Information(
                    "Uploaded to {Destination} on attempt {Attempt}",
                    dest.Name,
                    attempt);
                return;
            }
            catch (OperationCanceledException)
            {
                // Expected when the user stops watching during a pending upload.
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt == MaxUploadAttempts)
                    break;

                StatusChanged?.Invoke($"Upload to {dest.Name} failed; retrying ({attempt + 1}/{MaxUploadAttempts})...");
                Log.Warning(
                    ex,
                    "Upload to {Destination} failed on attempt {Attempt}; retrying",
                    dest.Name,
                    attempt);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        var message = lastException?.Message ?? "Unknown upload error.";
        var result = new UploadResult
        {
            FileName = fileInfo.Name,
            Success = false,
            ErrorMessage = message,
            DestinationName = dest.Name
        };
        UploadCompleted?.Invoke(result);
        ErrorOccurred?.Invoke($"Failed to upload to {dest.Name}: {message}");
    }

    private void CancelPendingChange()
    {
        lock (_changeLock)
        {
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = null;
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
