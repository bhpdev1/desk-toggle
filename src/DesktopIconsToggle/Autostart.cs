using Microsoft.Win32;

namespace DesktopIconsToggle;

internal interface IRegistryValueStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

/// <summary>HKCU-backed registry value store. Every operation swallows registry failures.</summary>
internal sealed class RegistryValueStore : IRegistryValueStore
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _subKeyPath;

    public RegistryValueStore(string subKeyPath)
    {
        _subKeyPath = subKeyPath;
    }

    public string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public void Write(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The menu item simply stays unchecked.
        }
    }

    public void Delete(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Same.
        }
    }
}

internal interface IAutostart
{
    bool IsEnabled();

    /// <summary>Flips the state and returns the new one.</summary>
    bool Toggle();
}

/// <summary>Autostart of the standalone exe, through the HKCU Run key.</summary>
internal sealed class Autostart : IAutostart
{
    public const string ValueName = "DesktopIconsToggle";

    private readonly IRegistryValueStore _store;
    private readonly string _quotedExePath;

    public Autostart(IRegistryValueStore store, string exePath)
    {
        _store = store;
        _quotedExePath = $"\"{exePath}\"";
    }

    public bool IsEnabled()
        => string.Equals(_store.Read(ValueName)?.Trim(), _quotedExePath, StringComparison.OrdinalIgnoreCase);

    public void Enable() => _store.Write(ValueName, _quotedExePath);

    public void Disable() => _store.Delete(ValueName);

    /// <summary>Flips the state and returns the new one.</summary>
    public bool Toggle()
    {
        if (IsEnabled())
        {
            Disable();
            return false;
        }

        Enable();
        return true;
    }
}
