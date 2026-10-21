using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using AudioDesk.Audio;
using NAudio.CoreAudioApi;

namespace AudioDesk.Mixer;

public enum Channel { Game, Chat, Media, Aux, Mic }

/// <summary>One-click layouts: everything on the headset, or Game/Media/Aux on the home theater.</summary>
public enum Profile { Headset, HomeTheater }

public sealed class ChannelSettings
{
    /// <summary>Output device for Chat/Media/Aux; null means "same as Game". Unused for Game and Mic,
    /// which are the Windows default playback and recording devices.</summary>
    public string? DeviceId { get; set; }

    /// <summary>The device to go back to when noise removal is turned off ("" = same as Game).</summary>
    public string? DirectDeviceId { get; set; }

    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    /// <summary>Global shortcuts for this channel, stored with <see cref="Hotkey.Serialize"/>.</summary>
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = [];
}

public sealed class MixerConfig
{
    public Dictionary<Channel, ChannelSettings> Channels { get; set; } = [];

    /// <summary>Process name → channel. Apps not listed are on Game.</summary>
    public Dictionary<string, Channel> Apps { get; set; } = [];

    /// <summary>Balance between Game and Chat: -1 = only Game, 0 = both at full volume, 1 = only Chat.</summary>
    public float ChatMix { get; set; }

    /// <summary>Devices the profiles use; null means "pick automatically".</summary>
    public string? HeadsetDeviceId { get; set; }
    public string? HomeTheaterDeviceId { get; set; }

    /// <summary>Global shortcuts for the profile actions.</summary>
    public Dictionary<HotkeyAction, string> ProfileHotkeys { get; set; } = [];

    /// <summary>Playback devices kept awake with quiet noise: device id → noise level in dBFS.</summary>
    public Dictionary<string, double> KeepAwake { get; set; } = [];
}

public sealed record MixerApp(
    string Key,
    string Name,
    string? ExePath,
    IReadOnlyList<uint> ProcessIds,
    bool IsPlaying,
    bool HasOutput,
    bool HasInput,
    Channel Channel,
    IReadOnlySet<string> OutputDeviceIds);

/// <summary>
/// Sonar-style channels without virtual devices. Each channel is a set of apps plus a physical
/// device; apps are routed straight to their channel's device with Windows' per-app routing,
/// so the audio engine never resamples or downmixes them through a middle device.
/// </summary>
public sealed class ChannelManager : IDisposable
{
    public static readonly Channel[] OutputChannels = [Channel.Game, Channel.Chat, Channel.Media, Channel.Aux];

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

    public ChannelManager(AudioService audio)
    {
        _audio = audio;
        _config = Load();
        foreach (var channel in Enum.GetValues<Channel>())
            _config.Channels.TryAdd(channel, new ChannelSettings());
        foreach (var settings in _config.Channels.Values)
            settings.Hotkeys ??= [];
        _config.ProfileHotkeys ??= [];
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
        Update(notify: true);
        _pollTimer.Start();
    }

    public void Refresh() => Update(notify: true);

    public static DataFlow FlowOf(Channel channel) => channel == Channel.Mic ? DataFlow.Capture : DataFlow.Render;

    /// <summary>The device a channel plays on (or records from). Null for Chat/Media/Aux means "same as Game".</summary>
    public string? DeviceId(Channel channel) => channel switch
    {
        Channel.Game => _audio.DefaultId(DataFlow.Render, Role.Multimedia),
        Channel.Mic => _audio.DefaultId(DataFlow.Capture, Role.Multimedia),
        _ => _config.Channels[channel].DeviceId,
    };

    public List<DeviceChoice> Choices(Channel channel)
    {
        var devices = _audio.GetDevices(FlowOf(channel));
        var choices = new List<DeviceChoice>();
        if (channel is Channel.Chat or Channel.Media or Channel.Aux)
        {
            string? gameId = DeviceId(Channel.Game);
            choices.Add(new DeviceChoice("", "Same as Game", devices.FirstOrDefault(d => d.Id == gameId)?.Format ?? ""));
        }
        choices.AddRange(devices.Select(d => new DeviceChoice(d.Id, d.Name, d.Format)));

        string? current = DeviceId(channel);
        if (current != null && choices.All(c => c.Id != current))
            choices.Add(new DeviceChoice(current, "Unavailable device"));
        return choices;
    }

    public void SetDevice(Channel channel, string deviceId)
    {
        string label = DeviceLabel(channel, deviceId);
        switch (channel)
        {
            case Channel.Game:
                SwitchInBackground([channel], label, deviceId, Role.Console, Role.Multimedia);
                break;
            case Channel.Mic:
                SwitchInBackground([channel], label, deviceId, Role.Console, Role.Multimedia, Role.Communications);
                break;
            default:
                _config.Channels[channel].DeviceId = deviceId.Length == 0 ? null : deviceId;
                Save();
                // Apps that only know "the communications device" (Teams, Zoom...) follow Chat too.
                if (channel == Channel.Chat && deviceId.Length > 0)
                    SwitchInBackground([channel], label, deviceId, Role.Communications);
                else
                    SwitchInBackground([channel], label, null);
                break;
        }
    }

    private string DeviceLabel(Channel channel, string deviceId) =>
        deviceId.Length == 0 ? "Same as Game" : Choices(channel).FirstOrDefault(c => c.Id == deviceId)?.Label ?? "the new device";

    // --- Switching state (drives the loaders in the window) ---

    private readonly Dictionary<Channel, int> _switching = [];
    private readonly List<string> _switchLabels = [];

    public bool IsSwitching(Channel channel) => _switching.GetValueOrDefault(channel) > 0;

    /// <summary>"Switching to …" while any switch is in progress, otherwise null.</summary>
    public string? SwitchingText => _switchLabels.Count > 0 ? $"Switching to {_switchLabels[^1]}…" : null;

    /// <summary>
    /// Runs a switch without freezing the window: changes the Windows default device on a worker thread when
    /// <paramref name="defaultDeviceId"/> is set (that call waits while the device restarts, and the S/PDIF
    /// output also restarts its Dolby encoder), refreshes the device list, and re-routes apps. The affected
    /// channels show a loader until all of that is done.
    /// </summary>
    private async void SwitchInBackground(Channel[] channels, string label, string? defaultDeviceId, params Role[] roles)
    {
        foreach (var channel in channels) _switching[channel] = _switching.GetValueOrDefault(channel) + 1;
        _switchLabels.Add(label);
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
            _switchLabels.Remove(label);
            StateChanged?.Invoke();
        }
    }

    // --- Profiles ---

    private static readonly string[] HeadsetWords = ["Headset", "Headphone", "Earphone"];

    private static bool IsHeadsetName(string name) =>
        HeadsetWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Real playback devices a profile can use (no virtual devices, no Broadcast).</summary>
    public List<DeviceChoice> ProfileChoices() =>
        _audio.GetDevices(DataFlow.Render)
            .Where(d => !d.IsVirtual && !d.Name.Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase))
            .Select(d => new DeviceChoice(d.Id, d.Name, d.Format))
            .ToList();

    /// <summary>The profile's chosen device if it's connected, otherwise a best guess (a headset, or the first other device).</summary>
    public string? ProfileDeviceId(Profile profile)
    {
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

    /// <summary>The profile that matches where Game, Media and Aux play right now, or null for a custom layout.</summary>
    public Profile? ActiveProfile()
    {
        foreach (var profile in Enum.GetValues<Profile>())
        {
            string? target = ProfileDeviceId(profile);
            if (target != null && new[] { Channel.Game, Channel.Media, Channel.Aux }
                    .All(c => string.Equals(EffectiveDeviceId(c), target, StringComparison.OrdinalIgnoreCase)))
                return profile;
        }
        return null;
    }

    /// <summary>
    /// Moves Game, Media and Aux to the profile's device. The Headset profile never touches Chat. The Home Theater
    /// profile keeps Chat on the headset: if Chat was following Game or sitting on the home theater, it's pinned
    /// to the headset; any other Chat device (e.g. Broadcast for noise removal) is left alone.
    /// </summary>
    public void ApplyProfile(Profile profile)
    {
        string target = ProfileDeviceId(profile)
            ?? throw new InvalidOperationException(profile == Profile.Headset ? "No headset found." : "No home theater device found.");
        string? headset = ProfileDeviceId(Profile.Headset);
        string? homeTheater = ProfileDeviceId(Profile.HomeTheater);

        _config.Channels[Channel.Media].DeviceId = target;
        _config.Channels[Channel.Aux].DeviceId = target;

        var chat = _config.Channels[Channel.Chat];
        if (profile == Profile.HomeTheater && headset != null &&
            (chat.DeviceId == null || string.Equals(chat.DeviceId, homeTheater, StringComparison.OrdinalIgnoreCase)))
            chat.DeviceId = headset;

        Save();
        // Game = Windows default output.
        SwitchInBackground([Channel.Game, Channel.Media, Channel.Aux], profile == Profile.Headset ? "Headset" : "Home Theater",
                           target, Role.Console, Role.Multimedia);
    }

    // --- Shortcut actions ---

    /// <summary>Moves the channel to its next device and returns that device's name.</summary>
    public string CycleDevice(Channel channel)
    {
        var devices = Choices(channel).Where(c => c.Id.Length > 0 && c.Label != "Unavailable device").ToList();
        if (devices.Count == 0) throw new InvalidOperationException("No devices to switch to.");

        string current = EffectiveDeviceId(channel) ?? "";
        int index = devices.FindIndex(d => string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase));
        var next = devices[(index + 1) % devices.Count];
        SetDevice(channel, next.Id);
        return next.Label;
    }

    /// <summary>Changes the channel volume by <paramref name="delta"/> (snapped to 5% steps) and unmutes it.</summary>
    public float StepVolume(Channel channel, float delta)
    {
        var (volume, _) = GetLevel(channel);
        float next = Math.Clamp(MathF.Round((volume + delta) * 20f) / 20f, 0f, 1f);
        SetLevel(channel, next, muted: false);
        StateChanged?.Invoke();
        return next;
    }

    /// <summary>Toggles mute and returns the new state.</summary>
    public bool ToggleMute(Channel channel)
    {
        var (volume, muted) = GetLevel(channel);
        SetLevel(channel, volume, !muted);
        StateChanged?.Invoke();
        return !muted;
    }

    /// <summary>Switches to the other profile (Headset when the current layout matches neither) and returns it.</summary>
    public Profile ToggleProfile()
    {
        var next = ActiveProfile() == Profile.Headset ? Profile.HomeTheater : Profile.Headset;
        ApplyProfile(next);
        return next;
    }

    // --- Shortcut settings ---

    private readonly Dictionary<HotkeySlot, string> _hotkeyErrors = [];

    private Dictionary<HotkeyAction, string> HotkeyStore(HotkeySlot slot) =>
        slot.Channel is { } channel ? _config.Channels[channel].Hotkeys : _config.ProfileHotkeys;

    /// <summary>Raised when shortcuts change or recording starts/stops; the app re-registers them.</summary>
    public event Action? HotkeysChanged;

    /// <summary>While a shortcut is being recorded, all shortcuts are released so the keys reach the recorder.</summary>
    public bool IsRecordingHotkey { get; private set; }

    public void SetHotkeyRecording(bool recording)
    {
        if (IsRecordingHotkey == recording) return;
        IsRecordingHotkey = recording;
        HotkeysChanged?.Invoke();
    }

    public Hotkey? GetHotkey(HotkeySlot slot) =>
        HotkeyStore(slot).TryGetValue(slot.Action, out var text) ? Hotkey.Parse(text) : null;

    public IEnumerable<(HotkeySlot Slot, Hotkey Hotkey)> AllHotkeys()
    {
        foreach (var (channel, settings) in _config.Channels)
            foreach (var (action, text) in settings.Hotkeys)
                if (Hotkey.Parse(text) is { } hotkey)
                    yield return (new HotkeySlot(channel, action), hotkey);
        foreach (var (action, text) in _config.ProfileHotkeys)
            if (Hotkey.Parse(text) is { } hotkey)
                yield return (new HotkeySlot(null, action), hotkey);
    }

    /// <summary>Sets or clears (null) a shortcut. Returns an error message if the keys are already used here.</summary>
    public string? SetHotkey(HotkeySlot slot, Hotkey? hotkey)
    {
        if (hotkey != null)
        {
            foreach (var (other, otherHotkey) in AllHotkeys())
            {
                if (otherHotkey == hotkey && other != slot)
                    return $"{hotkey} is already used for {other}.";
            }
        }

        if (hotkey == null) HotkeyStore(slot).Remove(slot.Action);
        else HotkeyStore(slot)[slot.Action] = hotkey.Serialize();
        Save();
        HotkeysChanged?.Invoke();
        return null;
    }

    public string? HotkeyError(HotkeySlot slot) => _hotkeyErrors.GetValueOrDefault(slot);

    /// <summary>Shortcuts Windows refused because another app already uses those keys.</summary>
    public void SetHotkeyErrors(IEnumerable<(HotkeySlot Slot, string Message)> errors)
    {
        _hotkeyErrors.Clear();
        foreach (var (slot, message) in errors)
            _hotkeyErrors[slot] = message;
        StateChanged?.Invoke();
    }

    // --- Keep awake (quiet noise so receivers don't fall asleep) ---

    public const double DefaultKeepAwakeDb = -80;

    private readonly Dictionary<string, string> _keepAwakeErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the set of kept-awake devices or their levels change.</summary>
    public event Action? KeepAwakeChanged;

    public IReadOnlyDictionary<string, double> KeepAwakeDevices => _config.KeepAwake;

    public bool IsKeepAwake(string deviceId) => _config.KeepAwake.ContainsKey(deviceId);

    /// <summary>Real devices the output channels play on right now ("same as Game" resolved).</summary>
    public HashSet<string> UsedOutputDevices() =>
        OutputChannels.Select(EffectiveDeviceId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kept-awake devices that a channel is using; only these get the noise.</summary>
    public Dictionary<string, double> ActiveKeepAwake()
    {
        var used = UsedOutputDevices();
        return _config.KeepAwake.Where(kv => used.Contains(kv.Key))
                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
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

    // --- Noise removal (NVIDIA Broadcast) for Chat and Mic ---

    public static bool SupportsNoiseRemoval(Channel channel) => channel is Channel.Chat or Channel.Mic;

    /// <summary>Broadcast's virtual speakers (for Chat) or microphone (for Mic), if Broadcast is installed.</summary>
    public string? BroadcastDeviceId(Channel channel) =>
        SupportsNoiseRemoval(channel)
            ? _audio.GetDevices(FlowOf(channel)).FirstOrDefault(d => d.Name.Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase))?.Id
            : null;

    public bool IsNoiseRemovalOn(Channel channel)
    {
        string? broadcast = BroadcastDeviceId(channel);
        return broadcast != null && string.Equals(DeviceId(channel), broadcast, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>On: the channel uses Broadcast's device, which cleans the audio and passes it to your real device.
    /// Off: the channel goes back to the device it used before.</summary>
    public void SetNoiseRemoval(Channel channel, bool on)
    {
        string broadcast = BroadcastDeviceId(channel) ?? throw new InvalidOperationException("NVIDIA Broadcast isn't installed.");
        var settings = _config.Channels[channel];

        if (on)
        {
            if (IsNoiseRemovalOn(channel)) return;
            settings.DirectDeviceId = DeviceId(channel) ?? "";
            SetDevice(channel, broadcast);
        }
        else
        {
            string? target = settings.DirectDeviceId;
            if (string.IsNullOrEmpty(target) || target == broadcast)
            {
                // Chat can fall back to "same as Game"; Mic needs a real microphone.
                target = channel == Channel.Chat
                    ? ""
                    : _audio.GetDevices(DataFlow.Capture).FirstOrDefault(d => d.Id != broadcast && !d.IsVirtual)?.Id
                      ?? throw new InvalidOperationException("No other microphone found.");
            }
            SetDevice(channel, target);
        }
        Save();
    }

    public Channel ChannelOf(string appKey) =>
        _config.Apps.TryGetValue(appKey, out var channel) ? channel : Channel.Game;

    public void MoveApp(MixerApp app, Channel channel)
    {
        if (channel == Channel.Mic || !app.HasOutput) return;
        if (channel == Channel.Game) _config.Apps.Remove(app.Key);
        else _config.Apps[app.Key] = channel;
        Save();

        // Its streams take the new channel's volume once the scan has re-sorted the app.
        _levelPids.UnionWith(app.ProcessIds);
        Update(notify: true);
    }

    /// <summary>
    /// True when an app in another channel plays some of its audio on the Chat device itself, like Discord
    /// with its voice output set to the Chat device and its other sounds in Media. That audio counts as Chat.
    /// </summary>
    public bool PlaysOnChat(MixerApp app)
    {
        string? chat = _config.Channels[Channel.Chat].DeviceId;
        return chat != null && app.Channel != Channel.Chat && app.OutputDeviceIds.Contains(chat) &&
               !string.Equals(chat, EffectiveDeviceId(app.Channel), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The device a channel's apps actually end up on ("same as Game" resolved).</summary>
    private string? EffectiveDeviceId(Channel channel) =>
        channel is Channel.Game or Channel.Mic ? DeviceId(channel) : _config.Channels[channel].DeviceId ?? DeviceId(Channel.Game);

    /// <summary>Which channel's volume a single audio stream follows.</summary>
    private Channel SessionChannel(MixerApp app, string deviceId) =>
        PlaysOnChat(app) && string.Equals(deviceId, _config.Channels[Channel.Chat].DeviceId, StringComparison.OrdinalIgnoreCase)
            ? Channel.Chat
            : app.Channel;

    /// <summary>Applies channel volume (with ChatMix) to every output stream that <paramref name="include"/> selects.</summary>
    private void ApplyLevels(Func<Channel, uint, bool> include)
    {
        var appsByPid = new Dictionary<uint, MixerApp>();
        foreach (var app in Apps.Where(a => a.HasOutput))
            foreach (uint pid in app.ProcessIds)
                appsByPid[pid] = app;

        _audio.SetSessionVolumes((deviceId, pid) =>
        {
            if (!appsByPid.TryGetValue(pid, out var app)) return null;
            var channel = SessionChannel(app, deviceId);
            if (!include(channel, pid)) return null;
            return (EffectiveVolume(channel), _config.Channels[channel].Muted);
        });
    }

    public float ChatMix => _config.ChatMix;

    public void SetChatMix(float mix)
    {
        _config.ChatMix = Math.Clamp(mix, -1f, 1f);
        Save();
        ApplyLevel(Channel.Game);
        ApplyLevel(Channel.Chat);
    }

    /// <summary>Channel volume with ChatMix applied: moving toward Game turns Chat down, and the other way round.</summary>
    private float EffectiveVolume(Channel channel)
    {
        float volume = _config.Channels[channel].Volume;
        float mix = _config.ChatMix;
        return channel switch
        {
            Channel.Game when mix > 0 => volume * (1 - mix),
            Channel.Chat when mix < 0 => volume * (1 + mix),
            _ => volume,
        };
    }

    private void ApplyLevel(Channel channel) => ApplyLevels((c, _) => c == channel);

    public (float Volume, bool Muted) GetLevel(Channel channel)
    {
        if (channel == Channel.Mic)
        {
            string? id = DeviceId(channel);
            return id == null ? (0f, false) : _audio.GetDeviceLevel(id);
        }
        var settings = _config.Channels[channel];
        return (settings.Volume, settings.Muted);
    }

    /// <summary>Channel volume is applied as each app's volume in the Windows mixer (a float gain inside
    /// the audio engine, so it doesn't reduce bit depth). Mic volume is the microphone's own level.</summary>
    public void SetLevel(Channel channel, float volume, bool muted)
    {
        if (channel == Channel.Mic)
        {
            string? id = DeviceId(channel);
            if (id != null) _audio.SetDeviceLevel(id, volume, muted);
            return;
        }

        var settings = _config.Channels[channel];
        settings.Volume = volume;
        settings.Muted = muted;
        Save();
        ApplyLevel(channel);
    }

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
        var channelDevices = OutputChannels.ToDictionary(c => c, c => _config.Channels[c].DeviceId);

        Task.Run(() => Scan(appChannels, channelDevices)).ContinueWith(task =>
        {
            _updateRunning = false;
            try
            {
                if (task.Exception == null) Publish(task.Result.Apps, task.Result.Error, notifyThis);
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
    private (List<MixerApp> Apps, string? Error) Scan(
        Dictionary<string, Channel> appChannels, Dictionary<Channel, string?> channelDevices)
    {
        var found = _audio.GetApps();
        var apps = new List<MixerApp>(found.Count);
        string? error = null;

        foreach (var a in found)
        {
            var channel = !a.HasOutput ? Channel.Mic : appChannels.TryGetValue(a.ProcessName, out var c) ? c : Channel.Game;
            var app = new MixerApp(a.ProcessName, a.Name, a.ExePath, a.ProcessIds, a.IsPlaying, a.HasOutput, a.HasInput, channel, a.OutputDeviceIds);
            apps.Add(app);
            if (a.HasOutput) error ??= Enforce(app, channel == Channel.Game ? null : channelDevices[channel]);
        }
        return (apps, error);
    }

    private void Publish(List<MixerApp> apps, string? error, bool notify)
    {
        var pids = apps.SelectMany(a => a.ProcessIds).ToHashSet();
        var newPids = pids.Where(p => !_knownPids.Contains(p)).ToHashSet();
        _knownPids = pids;
        LastError = error;
        Apps = apps;

        // Apps that just started get their channel's volume (only needed when it isn't plain 100%),
        // and apps that were just moved get their new channel's volume.
        if (newPids.Count > 0 || _levelPids.Count > 0)
        {
            var moved = new HashSet<uint>(_levelPids);
            _levelPids.Clear();
            ApplyLevels((channel, pid) => moved.Contains(pid) ||
                                          (newPids.Contains(pid) && (_config.Channels[channel].Muted || EffectiveVolume(channel) < 0.999f)));
        }

        string signature = string.Join("|", apps.Select(a => $"{a.Key}:{a.Channel}:{a.IsPlaying}:{a.HasOutput}:{a.HasInput}:{PlaysOnChat(a)}"));
        bool changed = signature != _signature;
        _signature = signature;
        if (changed || notify) StateChanged?.Invoke();
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
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<MixerConfig>(File.ReadAllText(ConfigPath), JsonOptions);
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
    }
}
