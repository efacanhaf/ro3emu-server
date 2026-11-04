using System.IO;
using System.Net.Http;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// The big picture beside the news. launcher.json "hero" names a file beside the exe or a URL relative to the
/// repository; without it the launcher tries &lt;repository&gt;/hero.jpg. The page gets the picture from the launcher
/// itself (<see cref="MainWindow"/> serves it as "hero-image"), so an http repository works from the https page. The
/// last downloaded picture is kept in %LOCALAPPDATA%\RO3Emu\hero.cache for starts without the repository; null = the
/// page draws its built-in hero.
/// </summary>
public sealed record HeroImage(byte[] Bytes, string ContentType)
{
    const int MaxBytes = 8 * 1024 * 1024;
    const string DefaultName = "hero.jpg";

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RO3Emu", "hero.cache");

    public static async Task<HeroImage?> LoadAsync(LauncherConfig config, string exeDirectory, RepositoryClient repo, CancellationToken ct)
    {
        string? hero = string.IsNullOrWhiteSpace(config.Hero) ? null : config.Hero.Trim();
        if (hero is not null && !hero.Contains("://", StringComparison.Ordinal))
        {
            string file = Path.Combine(exeDirectory, hero);
            if (File.Exists(file)) return FromBytes(await File.ReadAllBytesAsync(file, ct));
        }
        try
        {
            byte[]? bytes = await repo.TryGetBytesAsync(hero ?? DefaultName, ct);
            var image = bytes is null ? null : FromBytes(bytes);
            SaveCache(image);
            return image;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Repository unreachable: the picture from the last start, if any.
            return File.Exists(CachePath) ? FromBytes(await File.ReadAllBytesAsync(CachePath, ct)) : null;
        }
    }

    /// <summary>A JPEG, PNG or WebP picture of at most 8 MB; anything else is ignored.</summary>
    static HeroImage? FromBytes(byte[] b)
    {
        if (b.Length > MaxBytes || b.Length < 12) return null;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return new(b, "image/jpeg");
        if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return new(b, "image/png");
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return new(b, "image/webp");
        return null;
    }

    static void SaveCache(HeroImage? image)
    {
        try
        {
            if (image is null)
            {
                File.Delete(CachePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllBytes(CachePath, image.Bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
