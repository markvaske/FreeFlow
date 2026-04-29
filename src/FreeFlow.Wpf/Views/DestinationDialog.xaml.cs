using System.Windows;
using FreeFlow.Core.Models;

namespace FreeFlow.Wpf.Views;

public partial class DestinationDialog : Window
{
    private readonly FtpDestination _destination;

    public DestinationDialog(FtpDestination destination)
    {
        _destination = destination;
        InitializeComponent();
        DataContext = _destination;

        ProtocolCombo.ItemsSource = Enum.GetValues(typeof(FtpProtocol));
        PasswordBox.Password = _destination.Password ?? string.Empty;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _destination.Password = PasswordBox.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_destination.Name))
        {
            MessageBox.Show("Please enter a name.", "Missing name", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_destination.Host))
        {
            MessageBox.Show("Please enter a host.", "Missing host", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_destination.Port <= 0 || _destination.Port > 65535)
        {
            MessageBox.Show("Please enter a valid port (1–65535).", "Invalid port", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }
}

