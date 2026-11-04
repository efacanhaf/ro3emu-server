using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>What the page shows beside the install: the server pill, news, the hero picture and "What's new".</summary>
public sealed partial class LauncherController
{
    async Task PingLoopAsync()
    {
        if (string.IsNullOrEmpty(_config.Server)) return;
        string server = _config.Server.TrimEnd('/');
        while (!_life.IsCancellationRequested)
        {
            var (online, players) = await PingAsync(server);
            if (_life.IsCancellationRequested) return;
            _post(new { type = "server", online, players });
            try { await Task.Delay(TimeSpan.FromSeconds(30), _life.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    async Task<(bool Online, int? Players)> PingAsync(string server)
    {
        try
        {
            using var status = await _http.GetAsync(server + "/ro3emu/status", _life.Token);
            if (status.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(_life.Token));
                return (true, json.RootElement.TryGetProperty("players", out var p) ? p.GetInt32() : null);
            }
            // Servers without /ro3emu/status: any answer from the gate-key endpoint means up.
            using var key = await _http.GetAsync(server + "/ro3emu/gate-key.pem", _life.Token);
            return (key.IsSuccessStatusCode, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// news.json in the repository root: { "items": [ { tag, color, date, title, text, url } ] }, newest first. Without
    /// it the page shows the server's name and status only.
    /// </summary>
    async Task LoadNewsAsync()
    {
        try
        {
            byte[]? bytes = await _repo.TryGetBytesAsync("news.json", _life.Token);
            if (bytes is null) return;
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                _post(new { type = "news", items = items.Clone() });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PatcherException) { }
    }

    async Task LoadHeroAsync()
    {
        try
        {
            Hero = await HeroImage.LoadAsync(_config, Path.GetDirectoryName(_exePath)!, _repo, _life.Token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TaskCanceledException) { }
        if (Hero is not null) PostHero();
    }

    /// <summary>The page loads the picture from the launcher; the query only makes a new picture bypass its cache.</summary>
    void PostHero() => _post(new { type = "hero", url = "hero-image?v=" + Hero!.Bytes.Length });

    /// <summary>The version installed before any update this session runs: the base the next "What's new" compares to.</summary>
    void RememberInstalledVersion()
    {
        if (_settings.NotesSeenVersion is not null || _installed is null) return;
        _settings.NotesSeenVersion = _installed.Version;
        _settings.Save();
    }

    /// <summary>
    /// After an update, the new version's notes once, from channels/&lt;channel&gt;-notes.json in the repository:
    /// { "versions": { "&lt;version&gt;": [ "…" ] } }. A fresh install shows none.
    /// </summary>
    async Task ShowNotesIfNewAsync()
    {
        string? version = _installed?.Version;
        if (version is null || version == _settings.NotesSeenVersion) return;
        bool firstSeen = _settings.NotesSeenVersion is null;
        _settings.NotesSeenVersion = version;
        _settings.Save();
        if (firstSeen) return;
        try
        {
            byte[]? bytes = await _repo.TryGetBytesAsync("channels/" + _config.Channel + "-notes.json", _life.Token);
            if (bytes is null) return;
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || !versions.TryGetProperty(version, out var notes)
                || notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() == 0)
            {
                return;
            }
            _post(new { type = "notes", version, items = notes.Clone() });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
    }
}
