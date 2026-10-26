using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using AudioDesk.Audio;
using NAudio.CoreAudioApi;

namespace AudioDesk.Mixer;

/// <summary>Output channels. Voice (chat and mic) is left to SteelSeries Sonar.</summary>
public enum Channel { Game, Media, Aux }

/// <summary>One-click layouts: everything through Sonar, everything on the headset, or everything on the home theater.</summary>
public enum Profile { Sonar, Headset, HomeTheater }

public sealed class ChannelSettings
{
    /// <summary>Output device for Media/Aux (null only in old settings; pinned to Game's device on start). Unused for Game,
    /// which is the Windows default playback device.</summary>
    public string? DeviceId { get; set; }

    /// <summary>The device to go back to when Sonar is turned off.</summary>
    public string? DirectDeviceId { get; set; }

    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    /// <summary>Media/Aux through Sonar: device id → Sonar preset id to select when Sonar switches the channel to that device.</summary>
    public Dictionary<string, string> SonarPresetLinks { get; set; } = [];

    /// <summary>Media/Aux through Sonar: the preset for devices without their own link (null = leave the preset alone).</summary>
    public string? SonarDefaultPresetId { get; set; }
}

public sealed class MixerConfig
{
    public Dictionary<Channel, ChannelSettings> Channels { get; set; } = [];

    /// <summary>Process name → channel. Apps not listed are on Game.</summary>
    public Dictionary<string, Channel> Apps { get; set; } = [];

    /// <summary>Devices the profiles use; null means "pick automatically".</summary>
    public string? HeadsetDeviceId { get; set; }
    public string? HomeTheaterDeviceId { get; set; }

    /// <summary>Playback devices kept awake with quiet noise: device id → noise level in dBFS.</summary>
    public Dictionary<string, double> KeepAwake { get; set; } = [];

    /// <summary>Look for a newer release on GitHub shortly after AudioDesk starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Run the local API, the named pipe Knob Hub's AudioDesk app controls AudioDesk through.</summary>
    public bool LocalApiEnabled { get; set; } = true;
}

public sealed record MixerApp(
    string Key,
    string Name,
    string? ExePath,
    IReadOnlyList<uint> ProcessIds,
    bool IsPlaying,
    Channel Channel);

/// <summary>
/// Sonar-style channels without virtual devices. Each channel is a set of apps plus a physical
/// device; apps are routed straight to their channel's device with Windows' per-app routing,
/// so the audio engine never resamples or downmixes them through a middle device.
/// </summary>
public sealed class ChannelManager : IDisposable
{
    public static readonly Channel[] OutputChannels = Enum.GetValues<Channel>();

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioDesk", "mixer.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AudioService _audio;
    private readonly MixerConfig _config;

    // Slow audio calls run on worker threads; their results come back to the UI thread through this.
    private readonly TaskScheduler _ui = TaskScheduler.FromCurrentSynchronizationContext();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _saveTimer;
    private HashSet<uint> _knownPids = [];
    private string _signature = "";

    public IReadOnlyList<MixerApp> Apps { get; private set; } = [];
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread when apps, channels or devices change.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on the UI thread after every app scan (about every 1.5 s), changed or not.</summary>
    public event Action? Scanned;

    /// <summary>Raised on the UI thread when a channel's volume or mute was set (from the window, the tray or the
    /// local API), so everything showing it can follow.</summary>
    public event Action<Channel>? LevelChanged;

    /// <summary>Which channel each app's streams belong to, for the level meters.</summary>
    public IReadOnlyDictionary<uint, Channel> BuildMeterMap()
    {
        var processChannels = new Dictionary<uint, Channel>();
        foreach (var app in Apps)
            foreach (uint pid in app.ProcessIds) processChannels[pid] = app.Channel;
        return processChannels;
    }

    public ChannelManager(AudioService audio)
    {
        _audio = audio;
        _config = Load();
        foreach (var channel in Enum.GetValues<Channel>())
            _config.Channels.TryAdd(channel, new ChannelSettings());
        foreach (var settings in _config.Channels.Values)
        {
            settings.SonarPresetLinks = new Dictionary<string, string>(settings.SonarPresetLinks ?? [], StringComparer.OrdinalIgnoreCase);
        }
        _config.KeepAwake = new Dictionary<string, double>(_config.KeepAwake ?? [], StringComparer.OrdinalIgnoreCase);

        // New apps and other tools re-routing apps are picked up on the next poll.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _pollTimer.Tick += (_, _) => Update(notify: false);

        // Volume sliders change many times a second; write the file once they settle.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public void Start()
    {
        // Known before the first scan, so channels that go through Sonar don't start on their fallback device.
        _sonarRunning = IsSonarRunning();

        PinChannelsToRealDevices();
        Update(notify: true);
        _pollTimer.Start();
    }

    /// <summary>
    /// Older settings could leave Media/Aux on "same as Game" (no device). The pickers only offer real
    /// devices now, so pin those channels to the device Game uses today.
    /// </summary>
    private void PinChannelsToRealDevices()
    {
        string? game = DeviceId(Channel.Game);
        if (game == null) return;
        bool changed = false;
        foreach (var channel in new[] { Channel.Media, Channel.Aux })
        {
            if (_config.Channels[channel].DeviceId != null) continue;
            _config.Channels[channel].DeviceId = game;
            changed = true;
        }
        if (changed) Save();
    }

    public void Refresh() => Update(notify: true);

    /// <summary>
    /// The device a channel plays on right now. Game is the Windows default output. For Media/Aux this is the saved
    /// device, or a fallback while the saved device is missing (see <see cref="EffectiveDeviceId"/>).
    /// </summary>
    public string? DeviceId(Channel channel) => channel switch
    {
        Channel.Game => _audio.DefaultId(DataFlow.Render, Role.Multimedia),
        _ => EffectiveDeviceId(channel),
    };

    /// <summary>True while a Media/Aux channel's saved device is missing and it plays on a fallback.</summary>
    public bool IsUsingFallback(Channel channel) =>
        channel != Channel.Game &&
        _config.Channels[channel].DeviceId is { } saved &&
        !string.Equals(saved, EffectiveDeviceId(channel), StringComparison.OrdinalIgnoreCase);

    public List<DeviceChoice> Choices(Channel channel)
    {
        var devices = _audio.GetDevices(DataFlow.Render);

        // Only real devices: Sonar's devices are chosen with the Sonar button, and other virtual devices (Broadcast,
        // virtual cables) are for voice or other mixers.
        var choices = devices
            .Where(d => !d.IsVirtual)
            .Select(d => new DeviceChoice(d.Id, d.Name, d.Format))
            .ToList();

        // Keep the current device visible even when it's filtered out (e.g. Game is on a Sonar device while Sonar
        // can't be reached) or missing, so the picker never looks empty.
        string? current = DeviceId(channel);
        if (current != null && choices.All(c => c.Id != current))
        {
            var device = devices.FirstOrDefault(d => d.Id == current);
            choices.Add(device != null ? new DeviceChoice(device.Id, device.Name, device.Format) : new DeviceChoice(current, "Unavailable device"));
        }
        return choices;
    }

    public void SetDevice(Channel channel, string deviceId)
    {
        string label = DeviceLabel(channel, deviceId);
        if (channel == Channel.Game)
        {
            SwitchInBackground([channel], label, deviceId, Role.Console, Role.Multimedia);
            return;
        }
        _config.Channels[channel].DeviceId = deviceId.Length == 0 ? null : deviceId;
        Save();
        SwitchInBackground([channel], label, null);
    }

    private string DeviceLabel(Channel channel, string deviceId) =>
        Choices(channel).FirstOrDefault(c => c.Id == deviceId)?.Label ?? "the new device";

    // --- Switching state (drives the loaders in the window) ---

    private readonly Dictionary<Channel, int> _switching = [];

    public bool IsSwitching(Channel channel) => _switching.GetValueOrDefault(channel) > 0;

    /// <summary>
    /// Runs a switch without freezing the window: changes the Windows default device on a worker thread when
    /// <paramref name="defaultDeviceId"/> is set (that call waits while the device restarts, and the S/PDIF
    /// output also restarts its Dolby encoder), refreshes the device list, and re-routes apps. The affected
    /// channels show a loader until all of that is done.
    /// </summary>
    private async void SwitchInBackground(Channel[] channels, string label, string? defaultDeviceId, params Role[] roles)
    {
        foreach (var channel in channels) _switching[channel] = _switching.GetValueOrDefault(channel) + 1;
        StateChanged?.Invoke();

        try
        {
            if (defaultDeviceId != null)
            {
                await Task.Run(() => PolicyConfig.SetDefault(defaultDeviceId, roles));
                await _audio.RefreshDevicesAsync();
            }
            await UpdateAsync();
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't switch to {label}: {ex.Message}";
        }
        finally
        {
            foreach (var channel in channels) _switching[channel]--;
            StateChanged?.Invoke();
        }
    }

    // --- Profiles ---

    private static readonly string[] HeadsetWords = ["Headset", "Headphone", "Earphone"];

    private static bool IsHeadsetName(string name) =>
        HeadsetWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Real playback devices a profile can use (no virtual devices).</summary>
    public List<DeviceChoice> ProfileChoices() =>
        _audio.GetDevices(DataFlow.Render)
            .Where(d => !d.IsVirtual)
            .Select(d => new DeviceChoice(d.Id, d.Name, d.Format))
            .ToList();

    /// <summary>The profile's chosen device if it's connected, otherwise a best guess (a headset, or the first other device).
    /// The Sonar profile has no device of its own: Sonar plays on the output you picked in Sonar.</summary>
    public string? ProfileDeviceId(Profile profile)
    {
        if (profile == Profile.Sonar) return null;
        var choices = ProfileChoices();
        string? chosen = profile == Profile.Headset ? _config.HeadsetDeviceId : _config.HomeTheaterDeviceId;
        if (chosen != null && choices.Any(c => c.Id == chosen)) return chosen;

        var headset = choices.FirstOrDefault(c => IsHeadsetName(c.Label));
        return profile == Profile.Headset
            ? headset?.Id
            : choices.FirstOrDefault(c => c.Id != headset?.Id && !IsHeadsetName(c.Label))?.Id;
    }

    public void SetProfileDevice(Profile profile, string deviceId)
    {
        if (profile == Profile.Headset) _config.HeadsetDeviceId = deviceId;
        else _config.HomeTheaterDeviceId = deviceId;
        Save();
        StateChanged?.Invoke();
    }

    /// <summary>Every channel goes through Sonar (the Sonar profile is on).</summary>
    public bool IsSonarMode => IsSonarAvailable && AllThroughSonar;

    /// <summary>
    /// Headset or Home Theater when every channel ends up on that device, or null for a custom layout. A channel that
    /// goes through Sonar counts by the device Sonar plays it on, so with the Sonar profile on, this shows where Sonar
    /// plays.
    /// </summary>
    public Profile? ActiveProfile()
    {
        string? Destination(Channel c) => SonarOutputId(c) ?? EffectiveDeviceId(c);

        foreach (var profile in new[] { Profile.Headset, Profile.HomeTheater })
        {
            string? target = ProfileDeviceId(profile);
            if (target != null && OutputChannels.All(c => string.Equals(Destination(c), target, StringComparison.OrdinalIgnoreCase)))
                return profile;
        }
        return null;
    }

    /// <summary>
    /// Sonar sends Game, Media and Aux through Sonar, or (when every channel already goes through it) takes them all
    /// off Sonar again. Headset and Home Theater: with the Sonar profile on, Sonar is told to play every channel on the
    /// profile's device and the channels stay on Sonar; otherwise every channel comes off Sonar and plays straight on
    /// the profile's device. Either way that device also becomes the one each channel goes back to after Sonar.
    /// </summary>
    public void ApplyProfile(Profile profile)
    {
        if (profile == Profile.Sonar)
        {
            if (!IsSonarAvailable) throw new InvalidOperationException("SteelSeries Sonar isn't installed.");
            SetAllThroughSonar(!IsSonarMode);
            return;
        }

        string target = ProfileDeviceId(profile)
            ?? throw new InvalidOperationException(profile == Profile.Headset ? "No headset found." : "No home theater device found.");

        if (IsSonarMode && OutputChannels.All(c => SonarOutputId(c) != null))
        {
            foreach (var channel in OutputChannels)
            {
                _config.Channels[channel].DirectDeviceId = target;
                SetSonarOutput(channel, target);
            }
            Save();
            return;
        }

        foreach (var channel in OutputChannels)
        {
            _config.Channels[channel].DirectDeviceId = target;
            if (channel != Channel.Game) _config.Channels[channel].DeviceId = target;
        }

        Save();
        // Game = Windows default output.
        SwitchInBackground(OutputChannels, profile == Profile.Headset ? "Headset" : "Home Theater",
                           target, Role.Console, Role.Multimedia);
    }

    // --- App settings ---

    public bool CheckForUpdates
    {
        get => _config.CheckForUpdates;
        set
        {
            _config.CheckForUpdates = value;
            Save();
        }
    }

    /// <summary>Raised when the local API is turned on or off.</summary>
    public event Action? LocalApiChanged;

    public bool LocalApiEnabled
    {
        get => _config.LocalApiEnabled;
        set
        {
            if (value == _config.LocalApiEnabled) return;
            _config.LocalApiEnabled = value;
            Save();
            LocalApiChanged?.Invoke();
        }
    }

    // --- Keep awake (quiet noise so receivers don't fall asleep) ---

    public const double DefaultKeepAwakeDb = -80;

    private readonly Dictionary<string, string> _keepAwakeErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the set of kept-awake devices or their levels change.</summary>
    public event Action? KeepAwakeChanged;

    public IReadOnlyDictionary<string, double> KeepAwakeDevices => _config.KeepAwake;

    public bool IsKeepAwake(string deviceId) => _config.KeepAwake.ContainsKey(deviceId);

    /// <summary>Real devices the output channels play on right now.</summary>
    public HashSet<string> UsedOutputDevices() =>
        OutputChannels.Select(EffectiveDeviceId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kept-awake devices that a channel is using; only these get the noise.</summary>
    public Dictionary<string, double> ActiveKeepAwake()
    {
        var used = UsedOutputDevices();
        return _config.KeepAwake.Where(kv => used.Contains(kv.Key))
                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> _noisePlaying = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keep-awake noise is audibly playing on this device right now (not paused for other audio).</summary>
    public bool IsKeepAwakePlaying(string? deviceId) => deviceId != null && _noisePlaying.Contains(deviceId);

    /// <summary>Reported by the keep-awake service when the noise on a device starts, pauses or stops.</summary>
    public void SetNoisePlaying(string deviceId, bool playing)
    {
        bool changed = playing ? _noisePlaying.Add(deviceId) : _noisePlaying.Remove(deviceId);
        if (changed) StateChanged?.Invoke();
    }

    /// <summary>Keep awake is on for this device, but no channel uses it, so no noise is playing.</summary>
    public bool IsKeepAwakePaused(string deviceId) => IsKeepAwake(deviceId) && !UsedOutputDevices().Contains(deviceId);

    public double KeepAwakeLevel(string deviceId) =>
        _config.KeepAwake.TryGetValue(deviceId, out var db) ? db : DefaultKeepAwakeDb;

    public void SetKeepAwake(string deviceId, bool on, double? levelDb = null)
    {
        if (on) _config.KeepAwake[deviceId] = levelDb ?? KeepAwakeLevel(deviceId);
        else _config.KeepAwake.Remove(deviceId);
        _keepAwakeErrors.Remove(deviceId);
        Save();
        KeepAwakeChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public string? KeepAwakeError(string deviceId) => _keepAwakeErrors.GetValueOrDefault(deviceId);

    public void SetKeepAwakeError(string deviceId, string? message)
    {
        bool changed = message == null
            ? _keepAwakeErrors.Remove(deviceId)
            : _keepAwakeErrors.GetValueOrDefault(deviceId) != message;
        if (!changed) return;
        if (message != null) _keepAwakeErrors[deviceId] = message;
        StateChanged?.Invoke();
    }

    // --- SteelSeries Sonar: any channel can go through Sonar's matching device for its EQ and effects ---

    private const string SonarProcess = "SteelSeriesSonar";

    private static bool IsSonar(DeviceInfo device) =>
        device.Name.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

    /// <summary>The part of the Sonar device name for each channel, e.g. "SteelSeries Sonar - Gaming".</summary>
    private static string SonarSuffix(Channel channel) => channel switch
    {
        Channel.Game => "Sonar - Gaming",
        Channel.Media => "Sonar - Media",
        _ => "Sonar - Aux",
    };

    // Checked on each scan: Sonar's devices stay in Windows when Sonar isn't running, but nothing plays what goes into them.
    private bool _sonarRunning;

    private static bool IsSonarRunning()
    {
        var processes = Process.GetProcessesByName(SonarProcess);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Sonar's device for this channel, if Sonar is installed.</summary>
    public string? SonarDeviceId(Channel channel) =>
        _audio.GetDevices(DataFlow.Render)
              .FirstOrDefault(d => IsSonar(d) && d.Name.Contains(SonarSuffix(channel), StringComparison.OrdinalIgnoreCase))?.Id;

    public bool IsThroughSonar(Channel channel)
    {
        string? sonar = SonarDeviceId(channel);
        string? current = channel == Channel.Game ? DeviceId(channel) : _config.Channels[channel].DeviceId;
        return sonar != null && string.Equals(current, sonar, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSonarDevice(string? deviceId) =>
        deviceId != null && _audio.GetDevices(DataFlow.Render)
            .Any(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase) && IsSonar(d));

    /// <summary>On: the channel plays into Sonar's matching device, and Sonar passes it on to your real device.
    /// Off: the channel goes back to the device it used before (remembered in DirectDeviceId).</summary>
    public void SetThroughSonar(Channel channel, bool on)
    {
        var settings = _config.Channels[channel];
        string? current = DeviceId(channel);

        if (on)
        {
            string sonar = SonarDeviceId(channel) ?? throw new InvalidOperationException("SteelSeries Sonar isn't installed.");
            if (string.Equals(current, sonar, StringComparison.OrdinalIgnoreCase)) return;
            if (current != null && !IsSonarDevice(current)) settings.DirectDeviceId = current;
            SetDevice(channel, sonar);
        }
        else
        {
            SetDevice(channel, DirectDevice(channel) ?? throw new InvalidOperationException("No other output found."));
        }
        Save();
    }

    // --- The device picker: the channel's own device, or Sonar's output for it while it goes through Sonar ---

    private readonly SonarClient _sonarClient = new();

    // Sonar's outputs and presets, read on each scan while Sonar runs (null when it can't be reached).
    private SonarState? _sonarState;

    private static string SonarChannelId(Channel channel) => channel switch
    {
        Channel.Game => "game",
        Channel.Media => "media",
        _ => "aux",
    };

    private static readonly string[] SonarChannelIds = OutputChannels.Select(SonarChannelId).ToArray();

    /// <summary>The real device Sonar plays this channel on, while the channel goes through Sonar (null otherwise,
    /// or when Sonar can't be reached).</summary>
    public string? SonarOutputId(Channel channel) =>
        _sonarRunning && IsThroughSonar(channel) && _sonarState?.Outputs.TryGetValue(SonarChannelId(channel), out var id) == true ? id : null;

    /// <summary>The Sonar preset selected for this channel, while it goes through Sonar.</summary>
    public SonarPreset? SelectedSonarPreset(Channel channel) =>
        SonarOutputId(channel) != null && _sonarState!.SelectedPresets.TryGetValue(SonarChannelId(channel), out var preset) ? preset : null;

    /// <summary>The presets offered for this channel while it goes through Sonar: your Sonar favourites, plus the
    /// selected one if it isn't a favourite. Empty when the channel doesn't go through Sonar.</summary>
    public List<SonarPreset> SonarPresets(Channel channel)
    {
        if (SelectedSonarPreset(channel) is not { } selected) return [];
        var presets = _sonarState!.FavoritePresets.TryGetValue(SonarChannelId(channel), out var favorites) ? favorites.ToList() : [];
        if (presets.All(p => p.Id != selected.Id)) presets.Insert(0, selected);
        return presets;
    }

    // --- Preset links (Media/Aux): a Sonar preset per device, selected when Sonar switches the channel to that device ---

    public static bool SupportsPresetLinks(Channel channel) => channel is Channel.Media or Channel.Aux;

    /// <summary>Every Sonar preset for this channel, in Sonar's order (hundreds for Game).</summary>
    public IReadOnlyList<SonarPreset> AllSonarPresets(Channel channel) =>
        _sonarState?.AllPresets.TryGetValue(SonarChannelId(channel), out var presets) == true ? presets : [];

    /// <summary>The presets marked as favourites in Sonar for this channel (whether or not it goes through Sonar now).</summary>
    public IReadOnlyList<SonarPreset> FavoriteSonarPresets(Channel channel) =>
        _sonarState?.FavoritePresets.TryGetValue(SonarChannelId(channel), out var favorites) == true ? favorites : [];

    /// <summary>The preset linked to this device itself (null = it uses the default preset).</summary>
    public string? LinkedPresetId(Channel channel, string deviceId) =>
        _config.Channels[channel].SonarPresetLinks.TryGetValue(deviceId, out var presetId) ? presetId : null;

    /// <summary>The preset for devices without their own link (null = leave the preset alone).</summary>
    public string? DefaultPresetId(Channel channel) => _config.Channels[channel].SonarDefaultPresetId;

    /// <summary>The preset Sonar should use on this device: its own link, otherwise the default.</summary>
    private string? PresetIdFor(Channel channel, string deviceId) => LinkedPresetId(channel, deviceId) ?? DefaultPresetId(channel);

    /// <summary>Links a preset to a device (null = use the default preset).</summary>
    public void SetPresetLink(Channel channel, string deviceId, SonarPreset? preset)
    {
        var links = _config.Channels[channel].SonarPresetLinks;
        if (preset == null) links.Remove(deviceId);
        else links[deviceId] = preset.Id;
        Save();
        ApplyPresetNow(channel);
    }

    /// <summary>Sets the preset for devices without their own link (null = leave the preset alone).</summary>
    public void SetDefaultPreset(Channel channel, SonarPreset? preset)
    {
        _config.Channels[channel].SonarDefaultPresetId = preset?.Id;
        Save();
        ApplyPresetNow(channel);
    }

    /// <summary>After a link changes: if Sonar is playing the channel on a device that now has a preset, select it.</summary>
    private void ApplyPresetNow(Channel channel)
    {
        if (SonarOutputId(channel) is not { } output || PresetIdFor(channel, output) is not { } presetId) return;
        if (SelectedSonarPreset(channel)?.Id == presetId) return;
        SelectSonarPreset(channel, FavoriteSonarPresets(channel).FirstOrDefault(p => p.Id == presetId) ?? new SonarPreset(presetId, "linked preset"));
    }

    // The Sonar output last seen per channel, so links act only when the device actually changes (a preset you pick
    // by hand stays until then).
    private readonly Dictionary<Channel, string?> _seenSonarOutput = [];
    private bool _presetLinksReady;

    /// <summary>Selects the linked preset for every Media/Aux channel whose Sonar device just changed, whether the
    /// change came from AudioDesk (picker, profile, shortcut, turning Sonar on) or from SteelSeries GG.</summary>
    private void ApplyPresetLinks()
    {
        foreach (var channel in OutputChannels.Where(SupportsPresetLinks))
        {
            string? output = SonarOutputId(channel);
            bool changed = !_seenSonarOutput.TryGetValue(channel, out var seen) || !string.Equals(seen, output, StringComparison.OrdinalIgnoreCase);
            _seenSonarOutput[channel] = output;
            // The first look after AudioDesk starts only records where things are, so it doesn't undo a preset you picked.
            if (!_presetLinksReady || !changed || output == null) continue;
            ApplyPresetNow(channel);
        }
        _presetLinksReady |= _sonarState != null;
    }

    // Bumped around every change sent to Sonar, so a scan that read Sonar before the change can't put the old state back.
    private int _sonarWrites;

    /// <summary>Selects a Sonar preset for a channel that goes through Sonar.</summary>
    public async void SelectSonarPreset(Channel channel, SonarPreset preset)
    {
        _switching[channel] = _switching.GetValueOrDefault(channel) + 1;
        _sonarWrites++;
        StateChanged?.Invoke();
        try
        {
            await _sonarClient.SelectPresetAsync(preset.Id);
            if (_sonarState is { } state)
                _sonarState = state with
                {
                    SelectedPresets = new Dictionary<string, SonarPreset>(state.SelectedPresets, StringComparer.OrdinalIgnoreCase)
                    {
                        [SonarChannelId(channel)] = preset,
                    },
                };
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't change the Sonar preset for {channel}: {ex.Message}";
        }
        finally
        {
            _sonarWrites++;
            _switching[channel]--;
            StateChanged?.Invoke();
        }
    }

    /// <summary>The format Sonar processes this channel in (its virtual device's format), while the channel goes
    /// through Sonar. That's what limits the quality, whatever the device Sonar plays it on runs at.</summary>
    public string? SonarFormat(Channel channel) =>
        IsThroughSonar(channel) && SonarDeviceId(channel) is { } id
            ? _audio.GetDevices(DataFlow.Render).FirstOrDefault(d => d.Id == id)?.Format
            : null;

    /// <summary>What the channel's device picker shows: Sonar's output while the channel goes through Sonar.</summary>
    public string? PickerDeviceId(Channel channel) => SonarOutputId(channel) ?? DeviceId(channel);

    /// <summary>The picker's devices: real devices for Sonar to play on while the channel goes through Sonar.</summary>
    public List<DeviceChoice> PickerChoices(Channel channel)
    {
        if (SonarOutputId(channel) is not { } output) return Choices(channel);
        var choices = ProfileChoices();
        if (choices.All(c => c.Id != output)) choices.Add(new DeviceChoice(output, "Unavailable device"));
        return choices;
    }

    /// <summary>A device picked for a channel: changes Sonar's output while the channel goes through Sonar, otherwise
    /// the channel's own device.</summary>
    public void PickDevice(Channel channel, string deviceId)
    {
        if (SonarOutputId(channel) == null) SetDevice(channel, deviceId);
        else SetSonarOutput(channel, deviceId);
    }

    private async void SetSonarOutput(Channel channel, string deviceId)
    {
        _switching[channel] = _switching.GetValueOrDefault(channel) + 1;
        _sonarWrites++;
        StateChanged?.Invoke();
        try
        {
            await _sonarClient.SetOutputAsync(SonarChannelId(channel), deviceId);
            if (_sonarState is { } state)
                _sonarState = state with
                {
                    Outputs = new Dictionary<string, string>(state.Outputs, StringComparer.OrdinalIgnoreCase)
                    {
                        [SonarChannelId(channel)] = deviceId,
                    },
                };
            ApplyPresetLinks();
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't change Sonar's {channel} device: {ex.Message}";
        }
        finally
        {
            _sonarWrites++;
            _switching[channel]--;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Sonar is installed (its devices exist) for at least one channel.</summary>
    public bool IsSonarAvailable => OutputChannels.Any(c => SonarDeviceId(c) != null);

    /// <summary>Every channel goes through Sonar.</summary>
    public bool AllThroughSonar => OutputChannels.All(IsThroughSonar);

    /// <summary>Sends every channel through Sonar, or every channel straight to its device.</summary>
    public void SetAllThroughSonar(bool on)
    {
        foreach (var channel in OutputChannels.Where(c => SonarDeviceId(c) != null && IsThroughSonar(c) != on))
            SetThroughSonar(channel, on);
    }

    /// <summary>The real device a channel goes back to: the remembered one if it's still connected, otherwise the headset.</summary>
    private string? DirectDevice(Channel channel)
    {
        string? remembered = _config.Channels[channel].DirectDeviceId;
        if (!string.IsNullOrEmpty(remembered) && !IsSonarDevice(remembered) &&
            _audio.GetDevices(DataFlow.Render).Any(d => d.Id.Equals(remembered, StringComparison.OrdinalIgnoreCase)))
            return remembered;
        return ProfileDeviceId(Profile.Headset) ?? ProfileChoices().FirstOrDefault()?.Id;
    }

    public Channel ChannelOf(string appKey) =>
        _config.Apps.TryGetValue(appKey, out var channel) ? channel : Channel.Game;

    // Apps that have played sound since AudioDesk started.
    private readonly HashSet<string> _hasPlayed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an app is listed in its channel: once it has played sound, or when you've moved it to a channel.
    /// Background tools (RGB software, launchers) often hold a silent audio session all day; they're still routed,
    /// just not shown until they actually play.
    /// </summary>
    public bool IsShown(MixerApp app) => app.IsPlaying || _hasPlayed.Contains(app.Key) || _config.Apps.ContainsKey(app.Key);

    public void MoveApp(MixerApp app, Channel channel)
    {
        if (channel == Channel.Game) _config.Apps.Remove(app.Key);
        else _config.Apps[app.Key] = channel;
        Save();

        // Its streams take the new channel's volume once the scan has re-sorted the app.
        _levelPids.UnionWith(app.ProcessIds);
        Update(notify: true);
    }

    // Voice/call apps are left to Sonar: AudioDesk doesn't list, route or change the volume of these.
    private static readonly string[] VoiceAppPrefixes =
    [
        "discord", "teams", "ms-teams", "msteams", "zoom", "slack", "skype", "ts3client", "teamspeak", "mumble",
        "whatsapp", "telegram", "signal", "guilded", "element",
    ];

    private static bool IsVoiceApp(string processName) =>
        VoiceAppPrefixes.Any(p => processName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The device a channel's apps actually play on. For Media/Aux: the saved device while it's connected.
    /// If it's missing (e.g. a device was unplugged), fall back to the device the channel used before Sonar,
    /// then the headset, then Game's device. Sonar's devices count as missing while Sonar isn't running.
    /// The saved choice is kept, so the channel returns to it automatically when the device comes back.
    /// </summary>
    private string? EffectiveDeviceId(Channel channel)
    {
        if (channel == Channel.Game) return DeviceId(channel);

        var settings = _config.Channels[channel];
        var connected = _audio.GetDevices(DataFlow.Render)
                              .Where(d => _sonarRunning || !IsSonar(d))
                              .Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (settings.DeviceId != null && connected.Contains(settings.DeviceId)) return settings.DeviceId;
        if (!string.IsNullOrEmpty(settings.DirectDeviceId) && connected.Contains(settings.DirectDeviceId)) return settings.DirectDeviceId;
        return ProfileDeviceId(Profile.Headset) ?? DeviceId(Channel.Game);
    }

    /// <summary>Applies channel volume to every output stream that <paramref name="include"/> selects.</summary>
    /// <remarks>Apps in a channel that goes through Sonar stay at full volume: Sonar's own slider sets the level.</remarks>
    private void ApplyLevels(Func<Channel, uint, bool> include)
    {
        var appsByPid = new Dictionary<uint, MixerApp>();
        foreach (var app in Apps)
            foreach (uint pid in app.ProcessIds)
                appsByPid[pid] = app;
        var throughSonar = OutputChannels.ToDictionary(c => c, IsThroughSonar);

        _audio.SetSessionVolumes((_, pid) =>
        {
            if (!appsByPid.TryGetValue(pid, out var app) || !include(app.Channel, pid)) return null;
            if (throughSonar[app.Channel]) return (1f, false);
            var settings = _config.Channels[app.Channel];
            return (settings.Volume, settings.Muted);
        });
    }

    private void ApplyLevel(Channel channel) => ApplyLevels((c, _) => c == channel);

    /// <summary>The channel's volume: Sonar's slider for it while it goes through Sonar, otherwise AudioDesk's own.</summary>
    public (float Volume, bool Muted) GetLevel(Channel channel)
    {
        if (SonarLevelOf(channel) is { } sonar) return (sonar.Volume, sonar.Muted);
        var settings = _config.Channels[channel];
        return (settings.Volume, settings.Muted);
    }

    /// <summary>While the channel goes through Sonar, sets Sonar's slider for it. Otherwise the volume is applied as
    /// each app's volume in the Windows mixer (a float gain inside the audio engine, so it doesn't reduce bit depth).</summary>
    public void SetLevel(Channel channel, float volume, bool muted)
    {
        if (SonarLevelOf(channel) != null)
        {
            SetSonarLevel(channel, new SonarLevel(volume, muted));
        }
        else
        {
            var settings = _config.Channels[channel];
            settings.Volume = volume;
            settings.Muted = muted;
            Save();
            ApplyLevel(channel);
        }
        LevelChanged?.Invoke(channel);
    }

    /// <summary>Sonar's own level for the channel, while the channel goes through Sonar and Sonar can be reached.</summary>
    private SonarLevel? SonarLevelOf(Channel channel) =>
        SonarOutputId(channel) != null && _sonarState!.Levels.TryGetValue(SonarChannelId(channel), out var level) ? level : null;

    // A knob or a slider sets the level many times a second: only the latest one goes to Sonar, one request at a
    // time, so they can't arrive out of order.
    private readonly Dictionary<Channel, SonarLevel> _sonarLevelPending = [];

    private async void SetSonarLevel(Channel channel, SonarLevel level)
    {
        string id = SonarChannelId(channel);
        var before = _sonarState!.Levels.GetValueOrDefault(id);
        _sonarState = _sonarState with
        {
            Levels = new Dictionary<string, SonarLevel>(_sonarState.Levels, StringComparer.OrdinalIgnoreCase) { [id] = level },
        };
        bool sending = _sonarLevelPending.ContainsKey(channel);
        _sonarLevelPending[channel] = level;
        if (sending) return;

        _sonarWrites++;
        try
        {
            while (_sonarLevelPending.TryGetValue(channel, out var next))
            {
                try { await _sonarClient.SetLevelAsync(id, next, before); }
                catch (Exception ex) { LastError = $"Couldn't change Sonar's {channel} volume: {ex.Message}"; }
                before = next;
                if (_sonarLevelPending[channel] == next) _sonarLevelPending.Remove(channel);
            }
        }
        finally
        {
            _sonarWrites++;
        }
    }

    // Whether each channel went through Sonar at the last scan: when that changes, its apps' volumes are set again
    // (full volume inside Sonar, the channel's own volume outside).
    private readonly Dictionary<Channel, bool> _wasThroughSonar = [];

    private bool _updateRunning;
    private bool _updateQueued;
    private bool _notifyQueued;
    private readonly HashSet<uint> _levelPids = [];
    private List<TaskCompletionSource> _waiters = [];

    /// <summary>Completes after a scan that started after this call has been published.</summary>
    private Task UpdateAsync()
    {
        var done = new TaskCompletionSource();
        _waiters.Add(done);
        Update(notify: true);
        return done.Task;
    }

    /// <summary>
    /// Scans apps and re-applies routing on a worker thread, then publishes the result on the UI thread.
    /// Requests that arrive while a scan is running are merged into one follow-up scan.
    /// </summary>
    private void Update(bool notify)
    {
        _notifyQueued |= notify;
        if (_updateRunning)
        {
            _updateQueued = true;
            return;
        }
        _updateRunning = true;
        bool notifyThis = _notifyQueued;
        _notifyQueued = false;
        var waiters = _waiters;
        _waiters = [];

        // The worker gets its own copies so it never reads settings the UI is changing.
        var appChannels = new Dictionary<string, Channel>(_config.Apps, StringComparer.OrdinalIgnoreCase);
        // Resolved devices (with fallbacks), so apps never get routed to a device that's gone.
        var channelDevices = OutputChannels.ToDictionary(c => c, c => c == Channel.Game ? null : EffectiveDeviceId(c));
        int sonarWrites = _sonarWrites;

        Task.Run(() => Scan(appChannels, channelDevices)).ContinueWith(task =>
        {
            _updateRunning = false;
            try
            {
                if (task.Exception == null)
                {
                    // When Sonar starts or quits, channels that go through it switch devices: route again right away.
                    if (task.Result.SonarRunning != _sonarRunning)
                    {
                        _sonarRunning = task.Result.SonarRunning;
                        _updateQueued = true;
                        notifyThis = true;
                    }
                    // A change was sent to Sonar while this scan ran: keep what's known, the next scan reads the new state.
                    var sonar = sonarWrites == _sonarWrites ? task.Result.Sonar : _sonarState;
                    Publish(task.Result.Apps, task.Result.Error, sonar, notifyThis);
                }
            }
            finally
            {
                // Always release waiters, so a loader can never get stuck.
                foreach (var waiter in waiters) waiter.TrySetResult();
            }
            if (_updateQueued || _waiters.Count > 0)
            {
                _updateQueued = false;
                Update(notify: false);
            }
        }, _ui);
    }

    /// <summary>Runs on a worker thread: touches only the audio service and the copies it's given.</summary>
    private (List<MixerApp> Apps, string? Error, bool SonarRunning, SonarState? Sonar) Scan(
        Dictionary<string, Channel> appChannels, Dictionary<Channel, string?> channelDevices)
    {
        bool sonarRunning = IsSonarRunning();
        var sonar = sonarRunning ? _sonarClient.GetState(SonarChannelIds) : null;
        var found = _audio.GetApps();
        var apps = new List<MixerApp>(found.Count);
        string? error = null;

        foreach (var a in found)
        {
            // Recording-only apps and voice apps are Sonar's.
            if (!a.HasOutput || IsVoiceApp(a.ProcessName)) continue;
            var channel = appChannels.TryGetValue(a.ProcessName, out var c) ? c : Channel.Game;
            var app = new MixerApp(a.ProcessName, a.Name, a.ExePath, a.ProcessIds, a.IsPlaying, channel);
            apps.Add(app);
            error ??= Enforce(app, channel == Channel.Game ? null : channelDevices[channel]);
        }
        return (apps, error, sonarRunning, sonar);
    }

    private void Publish(List<MixerApp> apps, string? error, SonarState? sonar, bool notify)
    {
        _sonarState = sonar;
        var pids = apps.SelectMany(a => a.ProcessIds).ToHashSet();
        var newPids = pids.Where(p => !_knownPids.Contains(p)).ToHashSet();
        _knownPids = pids;
        LastError = error;
        Apps = apps;
        foreach (var app in apps.Where(a => a.IsPlaying)) _hasPlayed.Add(app.Key);
        Scanned?.Invoke();

        foreach (var channel in OutputChannels)
        {
            bool through = IsThroughSonar(channel);
            if (_wasThroughSonar.TryGetValue(channel, out bool was) && was == through) continue;
            _wasThroughSonar[channel] = through;
            ApplyLevel(channel);
        }

        // Apps that just started get their channel's volume (only needed when it isn't plain 100%),
        // and apps that were just moved get their new channel's volume.
        if (newPids.Count > 0 || _levelPids.Count > 0)
        {
            var moved = new HashSet<uint>(_levelPids);
            _levelPids.Clear();
            ApplyLevels((channel, pid) => moved.Contains(pid) ||
                                          (newPids.Contains(pid) && (_config.Channels[channel].Muted || _config.Channels[channel].Volume < 0.999f)));
        }

        // Sonar's outputs, presets and favourites are included so changes made in SteelSeries GG show up here.
        string signature = string.Join("|", apps.Select(a => $"{a.Key}:{a.Channel}:{a.IsPlaying}")) + "#" + (sonar == null ? "" :
            string.Join(",", sonar.Outputs.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")) + "#" +
            string.Join(",", sonar.SelectedPresets.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value.Id}")) + "#" +
            string.Join(",", sonar.FavoritePresets.OrderBy(f => f.Key).Select(f => $"{f.Key}={string.Join("/", f.Value.Select(p => p.Id))}")) + "#" +
            string.Join(",", sonar.Levels.OrderBy(l => l.Key).Select(l => $"{l.Key}={l.Value.Volume:0.###}/{l.Value.Muted}")));
        bool changed = signature != _signature;
        _signature = signature;
        if (changed || notify) StateChanged?.Invoke();
        ApplyPresetLinks();
    }

    /// <summary>Routes the app to <paramref name="desired"/> (null = Windows default) if anything moved it.</summary>
    private static string? Enforce(MixerApp app, string? desired)
    {
        try
        {
            foreach (uint pid in app.ProcessIds)
            {
                if (!string.Equals(AppRouting.Get(pid, DataFlow.Render), desired, StringComparison.OrdinalIgnoreCase))
                    AppRouting.Set(pid, DataFlow.Render, desired);
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't route {app.Name}: {ex.Message}";
        }
    }

    private static MixerConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject json)
            {
                DropRemovedChannels(json);
                var config = json.Deserialize<MixerConfig>(JsonOptions);
                if (config != null)
                {
                    config.Apps = new Dictionary<string, Channel>(config.Apps ?? [], StringComparer.OrdinalIgnoreCase);
                    config.Channels ??= [];
                    return config;
                }
            }
        }
        catch { }
        return new MixerConfig { Apps = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase) };
    }

    /// <summary>
    /// Settings from before Chat and Mic were removed name those channels, which no longer parse. Drop their settings
    /// and app assignments (those apps go back to Game, or to Sonar if they're voice apps) and the old ChatMix value.
    /// </summary>
    private static void DropRemovedChannels(JsonObject json)
    {
        static bool Known(string? name) => Enum.TryParse<Channel>(name, ignoreCase: true, out _);

        if (json["Channels"] is JsonObject channels)
            foreach (var name in channels.Select(p => p.Key).Where(n => !Known(n)).ToList())
                channels.Remove(name);
        if (json["Apps"] is JsonObject apps)
            foreach (var name in apps.Where(p => p.Value is not JsonValue v || !Known(v.ToString())).Select(p => p.Key).ToList())
                apps.Remove(name);
        json.Remove("ChatMix");
    }

    private void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
        }
        catch { }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        if (_saveTimer.IsEnabled) SaveNow();
        _sonarClient.Dispose();
    }
}
