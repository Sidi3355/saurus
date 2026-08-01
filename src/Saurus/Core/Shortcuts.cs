using System.IO;

namespace Saurus.Core;

/// <summary>
/// Start Menu and desktop shortcuts.
///
/// A tray icon only exists while the process does — there is no such thing as a tray entry
/// for a stopped app. What you actually want when the app is not running is somewhere
/// persistent to click to start it, which is a shortcut.
///
/// Built through WScript.Shell by late binding rather than by adding a COM interop
/// reference, so there is no extra dependency for twenty lines of work.
/// </summary>
public static class Shortcuts
{
    private const string LinkName = "SAURUS.lnk";

    private static string StartMenuPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);

    private static string DesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName);

    public static bool Exists => File.Exists(StartMenuPath) || File.Exists(DesktopPath);

    /// <summary>
    /// Creates both shortcuts. Returns the paths actually written; an empty list means it
    /// failed, which is reported rather than swallowed.
    /// </summary>
    public static List<string> Install(bool desktop = true, bool startMenu = true)
    {
        var made = new List<string>();

        var exe = AutoStart.ExecutablePath;
        if (exe is null)
        {
            Log.Warn("shortcuts unavailable: running via dotnet run, publish first");
            return made;
        }

        if (startMenu && TryWrite(StartMenuPath, exe)) made.Add(StartMenuPath);
        if (desktop && TryWrite(DesktopPath, exe)) made.Add(DesktopPath);

        return made;
    }

    public static void Uninstall()
    {
        foreach (var path in new[] { StartMenuPath, DesktopPath })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Error($"could not remove {path}", ex); }
        }
        Log.Info("shortcuts removed");
    }

    private static bool TryWrite(string linkPath, string exe)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Log.Error("WScript.Shell unavailable; cannot create shortcut");
                return false;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.IconLocation = exe + ",0";
            link.Description = "SAURUS - highlight text anywhere for a dense explanation";
            link.Save();

            Log.Info($"shortcut written: {linkPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"could not write shortcut {linkPath}", ex);
            return false;
        }
    }
}
