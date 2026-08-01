using System.IO;
using Microsoft.Win32;

namespace Saurus.Core;

/// <summary>
/// "Start with Windows", via the per-user Run key.
///
/// HKCU rather than HKLM deliberately: no elevation, no effect on other accounts, and the
/// user can see and disable it from Task Manager's Startup tab without knowing SAURUS wrote
/// it. That last part matters — a background tool that can only be disabled from inside
/// itself is a tool you cannot get rid of when it misbehaves.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SAURUS";

    /// <summary>
    /// Path to the real executable. Under `dotnet run` the host process is dotnet.exe, which
    /// would be useless to register, so that case is reported as unsupported.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)) return null;

            return path;
        }
    }

    /// <summary>False when running under `dotnet run`, where there is nothing sensible to register.</summary>
    public static bool IsSupported => ExecutablePath is not null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch (Exception ex)
            {
                Log.Error("could not read autostart state", ex);
                return false;
            }
        }
    }

    public static bool Enable()
    {
        var exe = ExecutablePath;
        if (exe is null)
        {
            Log.Warn("autostart unavailable: running via dotnet run, publish first");
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            // Quoted because the published path will contain spaces on most machines.
            key?.SetValue(ValueName, $"\"{exe}\"");
            Log.Info($"autostart enabled -> {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("could not enable autostart", ex);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("autostart disabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("could not disable autostart", ex);
            return false;
        }
    }

    public static bool Toggle() => IsEnabled ? Disable() : Enable();
}
