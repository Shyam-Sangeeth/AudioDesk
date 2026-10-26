using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace AudioDesk.Audio;

/// <summary>A Sonar EQ preset ("config" in Sonar's terms).</summary>
public sealed record SonarPreset(string Id, string Name);

/// <summary>A Sonar channel's own volume (0-1) and mute: its slider in Sonar.</summary>
public sealed record SonarLevel(float Volume, bool Muted);

/// <summary>What AudioDesk shows of Sonar, per Sonar channel ("game", "media", "aux", ...): the real device it plays
/// on, the selected preset, the presets marked as favourites in Sonar (in Sonar's order), all its presets, and its
/// volume.</summary>
public sealed record SonarState(
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyDictionary<string, SonarPreset> SelectedPresets,
    IReadOnlyDictionary<string, IReadOnlyList<SonarPreset>> FavoritePresets,
    IReadOnlyDictionary<string, IReadOnlyList<SonarPreset>> AllPresets,
    IReadOnlyDictionary<string, SonarLevel> Levels);

/// <summary>
/// Talks to SteelSeries Sonar's local web server (the one SteelSeries GG's own Sonar page uses) to read and change
/// the real device each Sonar channel plays on, its EQ preset and its volume. The server isn't a public API and its port changes every time Sonar
/// starts, so it's looked up through GG on first use and again after any failure. Only Sonar's Classic mode is
/// supported (Streamer mode routes audio differently).
/// </summary>
public sealed class SonarClient : IDisposable
{
    private static readonly string CorePropsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SteelSeries", "GG", "coreProps.json");

    // GG's own address is HTTPS with a certificate it makes itself; it's only ever on this machine.
    private readonly HttpClient _gg = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri is { } uri && uri.IsLoopback,
    }) { Timeout = TimeSpan.FromSeconds(2) };

    private readonly HttpClient _sonar = new() { Timeout = TimeSpan.FromSeconds(2) };
    private string? _baseAddress;

    // Each channel's full preset list is large (hundreds of game presets), so the lists are re-read only now and then.
    private static readonly TimeSpan PresetsMaxAge = TimeSpan.FromSeconds(30);
    private IReadOnlyDictionary<string, IReadOnlyList<SonarPreset>>? _favorites;
    private IReadOnlyDictionary<string, IReadOnlyList<SonarPreset>>? _allPresets;
    private DateTime _presetsRead;

    /// <summary>
    /// Sonar's outputs, selected presets and favourite presets for <paramref name="channels"/> (Sonar channel ids),
    /// or null when Sonar can't be reached or isn't in Classic mode. Runs on a worker thread.
    /// </summary>
    public SonarState? GetState(IReadOnlyList<string> channels)
    {
        try
        {
            string baseAddress = BaseAddress();
            if (Get($"{baseAddress}/mode").GetValue<string>() != "classic") return null;

            var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Get($"{baseAddress}/classicRedirections") is JsonArray redirections)
                foreach (var item in redirections)
                    if (item?["id"]?.GetValue<string>() is { } id && item["deviceId"]?.GetValue<string>() is { Length: > 0 } device)
                        outputs[id] = device;

            var selected = new Dictionary<string, SonarPreset>(StringComparer.OrdinalIgnoreCase);
            if (Get($"{baseAddress}/configs/selected") is JsonArray configs)
                foreach (var item in configs)
                    if (item?["virtualAudioDevice"]?.GetValue<string>() is { } channel && ToPreset(item) is { } preset)
                        selected[channel] = preset;

            if (_favorites == null || _allPresets == null || DateTime.UtcNow - _presetsRead > PresetsMaxAge)
            {
                var favorites = new Dictionary<string, IReadOnlyList<SonarPreset>>(StringComparer.OrdinalIgnoreCase);
                var all = new Dictionary<string, IReadOnlyList<SonarPreset>>(StringComparer.OrdinalIgnoreCase);
                foreach (string channel in channels)
                {
                    var list = Get($"{baseAddress}/configs?vad={Uri.EscapeDataString(channel)}") as JsonArray ?? [];
                    favorites[channel] = list.Where(p => p?["isFavorite"]?.GetValue<bool>() == true)
                                             .OrderBy(p => p!["favoritePosition"]?.GetValue<int>() ?? 0)
                                             .Select(ToPreset).OfType<SonarPreset>().ToList();
                    all[channel] = list.Select(ToPreset).OfType<SonarPreset>().ToList();
                }
                _favorites = favorites;
                _allPresets = all;
                _presetsRead = DateTime.UtcNow;
            }

            var levels = new Dictionary<string, SonarLevel>(StringComparer.OrdinalIgnoreCase);
            if (Get($"{baseAddress}/volumeSettings/classic")["devices"] is JsonObject devices)
                foreach (var (channel, settings) in devices)
                    if (settings?["classic"] is JsonObject classic && classic["volume"] != null)
                        levels[channel] = new SonarLevel(classic["volume"]!.GetValue<float>(), classic["muted"]?.GetValue<bool>() ?? false);

            return new SonarState(outputs, selected, _favorites, _allPresets, levels);
        }
        catch
        {
            _baseAddress = null; // Sonar restarted (new port) or stopped; look it up again next time
            _favorites = null;
            _allPresets = null;
            return null;
        }
    }

    private JsonNode Get(string url) =>
        JsonNode.Parse(_sonar.GetStringAsync(url).GetAwaiter().GetResult()) ?? throw new InvalidDataException(url);

    private static SonarPreset? ToPreset(JsonNode? node) =>
        node?["id"]?.GetValue<string>() is { } id && node["name"]?.GetValue<string>() is { } name ? new SonarPreset(id, name) : null;

    /// <summary>Selects a preset; Sonar knows which channel it belongs to.</summary>
    public async Task SelectPresetAsync(string presetId)
    {
        try
        {
            string baseAddress = await Task.Run(BaseAddress);
            var response = await _sonar.PutAsync($"{baseAddress}/configs/{Uri.EscapeDataString(presetId)}/select", null);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            _baseAddress = null;
            throw;
        }
    }

    /// <summary>Makes a Sonar channel ("game", "media", "aux") play on <paramref name="deviceId"/>.</summary>
    public async Task SetOutputAsync(string sonarChannel, string deviceId)
    {
        try
        {
            string baseAddress = await Task.Run(BaseAddress);
            var response = await _sonar.PutAsync(
                $"{baseAddress}/classicRedirections/{sonarChannel}/deviceId/{Uri.EscapeDataString(deviceId)}", null);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            _baseAddress = null;
            throw;
        }
    }

    /// <summary>Sets a Sonar channel's ("game", "media", "aux") own volume (0-1) and mute, its slider in Sonar;
    /// only what differs from <paramref name="before"/>.</summary>
    public async Task SetLevelAsync(string sonarChannel, SonarLevel level, SonarLevel? before)
    {
        try
        {
            string baseAddress = await Task.Run(BaseAddress);
            string volume = Math.Clamp(level.Volume, 0f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (before == null || Math.Abs(before.Volume - level.Volume) > 0.0005f)
                (await _sonar.PutAsync($"{baseAddress}/volumeSettings/classic/{sonarChannel}/Volume/{volume}", null)).EnsureSuccessStatusCode();
            if (before == null || before.Muted != level.Muted)
                (await _sonar.PutAsync($"{baseAddress}/volumeSettings/classic/{sonarChannel}/Mute/{(level.Muted ? "true" : "false")}", null)).EnsureSuccessStatusCode();
        }
        catch
        {
            _baseAddress = null;
            throw;
        }
    }

    /// <summary>Sonar's web server address, e.g. http://127.0.0.1:4653, found through GG's list of its apps.</summary>
    private string BaseAddress()
    {
        if (_baseAddress != null) return _baseAddress;

        var props = JsonNode.Parse(File.ReadAllText(CorePropsPath));
        string gg = props?["ggEncryptedAddress"]?.GetValue<string>() ?? throw new InvalidOperationException("SteelSeries GG isn't running.");
        var apps = JsonNode.Parse(_gg.GetStringAsync($"https://{gg}/subApps").GetAwaiter().GetResult());
        string address = apps?["subApps"]?["sonar"]?["metadata"]?["webServerAddress"]?.GetValue<string>() ?? "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Sonar isn't running.");
        return _baseAddress = address.TrimEnd('/');
    }

    public void Dispose()
    {
        _gg.Dispose();
        _sonar.Dispose();
    }
}
