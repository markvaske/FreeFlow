using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FreeFlow.Core.Models;
using FreeFlow.Core.Services;
using FreeFlow.Wpf.Utils;
using Microsoft.Win32;

namespace FreeFlow.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private AppSettings _settings;
    private FileWatcherService? _watcher;
    private bool _isRunning;
    private string _statusText = "Stopped";

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        Destinations = new ObservableCollection<FtpDestination>(_settings.Destinations);
        Activity = new ObservableCollection<string>();

        BrowseWatchedFileCommand = new RelayCommand(BrowseWatchedFile);
        AddDestinationCommand = new RelayCommand(AddDestination);
        EditDestinationCommand = new RelayCommand(EditSelectedDestination, () => SelectedDestination is not null);
        RemoveDestinationCommand = new RelayCommand(RemoveSelectedDestination, () => SelectedDestination is not null);
        TestDestinationCommand = new RelayCommand(TestSelectedDestination, () => SelectedDestination is not null);

        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        SaveCommand = new RelayCommand(Save, () => !IsRunning);

        StatusChanged("Ready");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FtpDestination> Destinations { get; }
    public ObservableCollection<string> Activity { get; }

    public RelayCommand BrowseWatchedFileCommand { get; }
    public RelayCommand AddDestinationCommand { get; }
    public RelayCommand EditDestinationCommand { get; }
    public RelayCommand RemoveDestinationCommand { get; }
    public RelayCommand TestDestinationCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SaveCommand { get; }

    public string WatchedFilePath
    {
        get => _settings.WatchedFilePath;
        set
        {
            if (_settings.WatchedFilePath == value) return;
            _settings.WatchedFilePath = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public int SettleDelaySeconds
    {
        get => _settings.SettleDelaySeconds;
        set
        {
            if (_settings.SettleDelaySeconds == value) return;
            _settings.SettleDelaySeconds = Math.Max(0, value);
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            RefreshCanExecute();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public FtpDestination? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (ReferenceEquals(_selectedDestination, value)) return;
            _selectedDestination = value;
            OnPropertyChanged();
            RefreshCanExecute();
        }
    }
    private FtpDestination? _selectedDestination;

    private void BrowseWatchedFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the stats file to watch",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            WatchedFilePath = dialog.FileName;
            AddActivity($"Watched file set: {dialog.FileName}");
        }
    }

    private void AddDestination()
    {
        var draft = new FtpDestination
        {
            Name = $"Destination {Destinations.Count + 1}",
            Protocol = FtpProtocol.Ftps,
            Port = 21,
            RemotePath = "/",
            IsEnabled = true
        };

        var dialog = new Views.DestinationDialog(draft) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            Destinations.Add(draft);
            AddActivity($"Added destination: {draft.Name}");
        }
    }

    private void EditSelectedDestination()
    {
        if (SelectedDestination is null) return;

        // Edit a copy and only apply if the user clicks Save.
        var copy = Clone(SelectedDestination);
        var dialog = new Views.DestinationDialog(copy) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            Apply(SelectedDestination, copy);
            AddActivity($"Updated destination: {SelectedDestination.Name}");
        }
    }

    private void RemoveSelectedDestination()
    {
        if (SelectedDestination is null) return;
        var name = SelectedDestination.Name;
        Destinations.Remove(SelectedDestination);
        SelectedDestination = null;
        AddActivity($"Removed destination: {name}");
    }

    private void TestSelectedDestination()
    {
        if (SelectedDestination is null) return;

        if (SelectedDestination.Protocol == FtpProtocol.Sftp)
        {
            MessageBox.Show(
                "SFTP isn’t implemented yet in the current core uploader.\n\nChoose FTP or FTPS for MVP.",
                "SFTP not supported yet",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        // MVP: lightweight “test” that just validates we have enough info.
        // A real connection test will be added once UI + protocol plumbing is solid.
        if (string.IsNullOrWhiteSpace(SelectedDestination.Host) ||
            string.IsNullOrWhiteSpace(SelectedDestination.Username) ||
            string.IsNullOrWhiteSpace(SelectedDestination.Password))
        {
            MessageBox.Show(
                "Please fill in Host, Username, and Password before testing.",
                "Missing fields",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }

        MessageBox.Show(
            "Looks good (basic validation passed).\n\nNext step: implement a real connect/upload test.",
            "Test destination",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(WatchedFilePath))
        {
            MessageBox.Show("Choose a watched file first.", "Missing watched file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Destinations.Count == 0)
        {
            MessageBox.Show("Add at least one destination.", "No destinations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Destinations.Any(d => d.IsEnabled && d.Protocol == FtpProtocol.Sftp))
        {
            MessageBox.Show(
                "One or more enabled destinations are set to SFTP, but SFTP isn’t implemented yet.\n\nDisable SFTP destinations or switch them to FTP/FTPS.",
                "SFTP not supported yet",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        // Sync current destination edits back into settings before starting.
        _settings.Destinations = Destinations.ToList();
        Save();

        _watcher = new FileWatcherService(_settings, _settingsService);
        _watcher.StatusChanged += StatusChanged;
        _watcher.ErrorOccurred += ErrorOccurred;
        _watcher.UploadCompleted += UploadCompleted;

        _watcher.Start();
        IsRunning = true;
        StatusText = "Watching...";
        AddActivity("Started watching.");
    }

    private void Stop()
    {
        _watcher?.Stop();
        _watcher = null;
        IsRunning = false;
        StatusText = "Stopped";
        AddActivity("Stopped.");
    }

    private void Save()
    {
        _settings.Destinations = Destinations.ToList();
        _settingsService.Save(_settings);
        AddActivity("Settings saved.");
    }

    private void StatusChanged(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = message;
            AddActivity(message);
        });
    }

    private void ErrorOccurred(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = "Error";
            AddActivity($"ERROR: {message}");
        });
    }

    private void UploadCompleted(UploadResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var outcome = result.Success ? "OK" : $"FAIL ({result.ErrorMessage})";
            AddActivity($"Upload {outcome}: {result.FileName} → {result.DestinationName}");
        });
    }

    private void AddActivity(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Activity.Insert(0, line);
        while (Activity.Count > 200)
            Activity.RemoveAt(Activity.Count - 1);
    }

    private void RefreshCanExecute()
    {
        EditDestinationCommand.RaiseCanExecuteChanged();
        RemoveDestinationCommand.RaiseCanExecuteChanged();
        TestDestinationCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static FtpDestination Clone(FtpDestination src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        Host = src.Host,
        Port = src.Port,
        Username = src.Username,
        Password = src.Password,
        RemotePath = src.RemotePath,
        Protocol = src.Protocol,
        IsEnabled = src.IsEnabled
    };

    private static void Apply(FtpDestination target, FtpDestination src)
    {
        target.Name = src.Name;
        target.Host = src.Host;
        target.Port = src.Port;
        target.Username = src.Username;
        target.Password = src.Password;
        target.RemotePath = src.RemotePath;
        target.Protocol = src.Protocol;
        target.IsEnabled = src.IsEnabled;
    }
}

