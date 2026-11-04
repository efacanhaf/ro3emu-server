using System.IO;

namespace Ro3Emu.Launcher;

/// <summary>Desktop / Start menu shortcuts to the launcher, made only when the player says yes (first run or Settings).</summary>
public static class Shortcuts
{
    /// <summary>Creates (or points again at this exe) the shortcuts asked for; returns the ones that could not be made.</summary>
    public static List<string> Create(string exePath, string serverName, bool desktop, bool startMenu)
    {
        string name = FileName(serverName);
        var failed = new List<string>();
        if (desktop && !TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name), exePath, serverName))
            failed.Add("Desktop");
        if (startMenu && !TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name), exePath, serverName))
            failed.Add("Start menu");
        return failed;
    }

    /// <summary>"&lt;serverName&gt;.lnk" without the characters a file name cannot hold.</summary>
    static string FileName(string serverName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new string([.. serverName.Where(c => !invalid.Contains(c))]).Trim();
        return (clean.Length > 0 ? clean : "RO3Emu") + ".lnk";
    }

    static bool TryCreate(string linkPath, string exePath, string serverName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(linkPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = "Start " + serverName;
            shortcut.Save();
            return File.Exists(linkPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }
}
