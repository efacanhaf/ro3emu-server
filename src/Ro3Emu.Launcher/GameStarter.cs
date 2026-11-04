using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.Launcher;

/// <summary>
/// Starts ro3.exe the way RO3Launcher.exe does (same as Ro3Emu.ClientTools' Launcher): RO3.ClientSecurity.LauncherProofGuard
/// kills the game (exit 70) unless RO3_LAUNCH_PIPE_HANDLE names an inherited anonymous pipe that delivers, within one
/// second, five CRLF lines: RO3_LAUNCH_PROOF_V1, the game's PID, issued-at and expiry unix seconds, 64 lowercase hex chars.
/// </summary>
public static class GameStarter
{
    /// <summary>
    /// The running game (the caller owns the process: the launcher waits in the tray until it exits), or the reason it
    /// stopped within the first seconds.
    /// </summary>
    public static async Task<GameStart> StartAsync(string clientDir)
    {
        string exe = Path.Combine(clientDir, "ro3.exe");
        if (!File.Exists(exe)) return new(null, "ro3.exe not found");

        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = clientDir };
        start.Environment["RO3_LAUNCH_PIPE_HANDLE"] = pipe.GetClientHandleAsString();
        var game = Process.Start(start) ?? throw new InvalidOperationException("ro3.exe did not start");
        pipe.DisposeLocalCopyOfClientHandle();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string proof = string.Join("\r\n", "RO3_LAUNCH_PROOF_V1", game.Id.ToString(), now.ToString(), (now + 30).ToString(),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))) + "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(proof);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();

        // Keep our end open while the game reads the proof (it gives itself one second).
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await game.WaitForExitAsync(wait.Token);
            string failure = game.ExitCode == 70 ? "the game rejected the launch proof (exit 70)" : $"the game closed right away (exit {game.ExitCode})";
            game.Dispose();
            return new(null, failure);
        }
        catch (OperationCanceledException)
        {
            return new(game, null);
        }
    }
}

/// <summary>What <see cref="GameStarter.StartAsync"/> did: the running game, or why it did not start.</summary>
public sealed record GameStart(Process? Game, string? Failure);
