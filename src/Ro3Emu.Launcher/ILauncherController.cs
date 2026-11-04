using System.Text.Json;

namespace Ro3Emu.Launcher;

/// <summary>
/// What the window needs from the logic behind the page: the page's commands, the hero picture it serves, and cleanup.
/// <see cref="LauncherController"/> is an operator's launcher, <see cref="Ro3EmuController"/> RO3Emu's own app.
/// </summary>
public interface ILauncherController : IDisposable
{
    /// <summary>The hero picture once it is loaded (served to the page as "hero-image"); null = the page's built-in sky.</summary>
    HeroImage? Hero { get; }

    Task HandleAsync(string cmd, JsonElement message);
}
