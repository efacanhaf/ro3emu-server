using System.IO;

namespace Ro3Emu.Launcher;

/// <summary>
/// Settings → Uninstall: only the folder this launcher filled itself (<see cref="UserSettings.OwnedDirectory"/>) is ever
/// removed. A folder that already held files when the install began, such as the player's own ROO_PC, is never offered.
/// </summary>
public sealed partial class LauncherController
{
    /// <summary>Before an install into an empty or new folder: remember the folder as the launcher's own.</summary>
    void ClaimFreshDirectory()
    {
        if (_installed is not null) return;
        string dir = Path.GetFullPath(_layout.GameDirectory);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return;
        _settings.OwnedDirectory = dir;
        _settings.Save();
    }

    bool CanUninstall()
    {
        if (_settings.OwnedDirectory is not { } owned) return false;
        string dir = Path.GetFullPath(_layout.GameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.GetFullPath(_exePath);
        return string.Equals(Path.GetFullPath(owned).TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is not null // never a drive root
            && !exe.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(_layout.MetaDirectory, "state.json"));
    }

    async Task UninstallAsync()
    {
        if (_work != null || _phase is "launching" or "running" || IsGameRunning())
        {
            _post(new { type = "uninstall", status = "error", message = "Close the game and wait for the launcher to finish first." });
            return;
        }
        if (!CanUninstall())
        {
            _post(new { type = "uninstall", status = "error", message = "This folder was not installed by the launcher, so nothing was removed." });
            return;
        }
        PostState("checking", "Removing the game…");
        try
        {
            string dir = _layout.GameDirectory;
            await Task.Run(() => Directory.Delete(dir, recursive: true));
            _installed = null;
            _settings.OwnedDirectory = null;
            _settings.NotesSeenVersion = null;
            _settings.Save();
            _post(new { type = "uninstall", status = "done" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _post(new { type = "uninstall", status = "error", message = "Some files could not be removed: " + e.Message });
        }
        await CheckAsync();
    }
}
