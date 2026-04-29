using System.Security.Cryptography;
using System.Text;
using FreeFlow.Core.Models;
using FreeFlow.Core.Services;
using Serilog;

namespace FreeFlow.Wpf.Services;

internal sealed class WindowsSettingsService
{
    private const string ProtectedPrefix = "dpapi:";
    private readonly SettingsService _settingsService = new();

    public AppSettings Load()
    {
        var settings = _settingsService.Load();
        foreach (var destination in settings.Destinations)
        {
            destination.Password = UnprotectPassword(destination.Password);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        var protectedSettings = Clone(settings);
        foreach (var destination in protectedSettings.Destinations)
        {
            destination.Password = ProtectPassword(destination.Password);
        }

        _settingsService.Save(protectedSettings);
    }

    private static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectPassword(string password)
    {
        if (string.IsNullOrEmpty(password) ||
            !password.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return password;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(password[ProtectedPrefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Return the original protected value to avoid silently wiping the password
            // when DPAPI fails (e.g., settings copied to a different user/machine).
            Log.Warning(ex, "Failed to unprotect password; returning original protected value");
            return password;
        }
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        WatchedFilePath = settings.WatchedFilePath,
        SettleDelaySeconds = settings.SettleDelaySeconds,
        AutoStartOnLaunch = settings.AutoStartOnLaunch,
        StartMinimized = settings.StartMinimized,
        Destinations = settings.Destinations.Select(Clone).ToList()
    };

    private static FtpDestination Clone(FtpDestination destination) => new()
    {
        Id = destination.Id,
        Name = destination.Name,
        Host = destination.Host,
        Port = destination.Port,
        Username = destination.Username,
        Password = destination.Password,
        RemotePath = destination.RemotePath,
        Protocol = destination.Protocol,
        IsEnabled = destination.IsEnabled
    };
}
