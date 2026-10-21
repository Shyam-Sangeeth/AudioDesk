using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AudioDesk.Audio;
using AudioDesk.Mixer;
using NAudio.CoreAudioApi;

namespace AudioDesk;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A keep-awake noise level choice.</summary>
public sealed record NoiseLevel(double Db, string Label)
{
    public static readonly IReadOnlyList<NoiseLevel> All =
    [
        new(-80, "Inaudible (−80 dB)"),
        new(-70, "Very quiet (−70 dB)"),
        new(-60, "Quiet (−60 dB)"),
    ];
}

public sealed class DeviceItem(DeviceInfo info, bool keepAwake = false, double keepAwakeDb = ChannelManager.DefaultKeepAwakeDb,
                               string? keepAwakeError = null, bool keepAwakePaused = false)
{
    public DeviceInfo Info => info;
    public bool KeepAwake => keepAwake;

    /// <summary>Keep awake is on, but no channel uses this device, so no noise is playing.</summary>
    public bool KeepAwakePaused => keepAwakePaused;
    public double KeepAwakeDb => keepAwakeDb;
    public string? KeepAwakeError => keepAwakeError;
    public bool HasKeepAwakeError => !string.IsNullOrEmpty(keepAwakeError);
    public IReadOnlyList<NoiseLevel> NoiseLevels => NoiseLevel.All;
    public bool IsPlayback => info.Flow == DataFlow.Render;
    public string Id => info.Id;
    public string Name => info.Name;
    public string Format => info.Format;
    public bool IsDefault => info.IsDefault;
    public bool IsCommunications => info.IsCommunications;
    public bool IsVirtual => info.IsVirtual;
    public bool CanMakeDefault => !info.IsDefault;
    public bool CanMakeCommunications => !info.IsCommunications;
    public DataFlow Flow => info.Flow;

    public string Glyph
    {
        get
        {
            if (info.Flow == DataFlow.Capture) return ""; // microphone
            if (ContainsAny(info.Name, "Headphone", "Headset", "Earphone")) return ""; // headphones
            if (ContainsAny(info.Name, "Digital", "SPDIF", "S/PDIF", "HDMI", "Optical", "NVIDIA")) return ""; // TV/receiver
            return ""; // speaker
        }
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry in a device drop-down. An empty id means "same as Game".</summary>
public sealed record DeviceChoice(string Id, string Label, string Format = "");

public sealed class AppChip(MixerApp app, ImageSource? icon, bool canMove, string badge = "")
{
    public MixerApp App => app;
    public string Key => app.Key;
    public string Name => app.Name;
    public ImageSource? Icon => icon;
    public bool IsPlaying => app.IsPlaying;
    public bool CanMove => canMove;

    /// <summary>Short note shown on the chip, e.g. "Voice" for an app whose voice plays on the Chat device.</summary>
    public string Badge => badge;
    public bool HasBadge => badge.Length > 0;
    public string Tip => HasBadge
        ? $"{app.Name} plays its voice on the Chat device, so it follows Chat's volume and ChatMix. Its other sounds are in {app.Channel}."
        : $"{app.Name}: drag to another channel, or right-click.";
}

/// <summary>ChatMix slider: -100 = only Game, 0 = both at full volume, 100 = only Chat.</summary>
public sealed class ChatMixViewModel(ChannelManager mixer, Action<string> report) : ObservableObject
{
    private double _value;
    private bool _syncing;

    public double Value
    {
        get => _value;
        set
        {
            value = Math.Round(value);
            if (value == _value) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsUnbalanced));
            OnPropertyChanged(nameof(CanBalance));
            if (_syncing) return;
            try { mixer.SetChatMix((float)(value / 100.0)); }
            catch (Exception ex) { report($"Couldn't change ChatMix: {ex.Message}"); }
        }
    }

    public string Text => !_isEnabled ? "Off" : _value switch
    {
        0 => "Balanced",
        < 0 => $"Chat {100 + _value:0}%",
        _ => $"Game {100 - _value:0}%",
    };

    public bool IsUnbalanced => _value != 0;

    private bool _isEnabled;

    /// <summary>Only while Game and Chat both play on the headset (Chat directly or through NVIDIA Broadcast).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(CanBalance));
            OnPropertyChanged(nameof(Text));
        }
    }

    public bool IsDisabled => !_isEnabled;
    public bool CanBalance => _isEnabled && IsUnbalanced;

    public void Center() => Value = 0;

    public void Sync()
    {
        _syncing = true;
        try
        {
            Value = mixer.ChatMix * 100;
            IsEnabled = mixer.IsChatMixActive;
            OnPropertyChanged(nameof(CanBalance));
        }
        finally { _syncing = false; }
    }
}


/// <summary>The Headset / Home Theater switch and its device pickers.</summary>
public sealed class ProfilesViewModel(ChannelManager mixer, Action<string> report) : ObservableObject
{
    private bool _syncing;
    private Profile? _active;
    private List<DeviceChoice> _choices = [];
    private string _headsetId = "";
    private string _homeTheaterId = "";

    public IReadOnlyList<ShortcutViewModel> Shortcuts { get; } =
        HotkeySlot.ProfileActions.Select(a => new ShortcutViewModel(new HotkeySlot(null, a))).ToList();

    public bool IsHeadset => _active == Profile.Headset;
    public bool IsHomeTheater => _active == Profile.HomeTheater;
    public bool IsNotHeadset => !IsHeadset;
    public bool IsNotHomeTheater => !IsHomeTheater;

    public List<DeviceChoice> Choices
    {
        get => _choices;
        private set { _choices = value; OnPropertyChanged(); }
    }

    public string HeadsetDeviceId
    {
        get => _headsetId;
        set => SetDevice(Profile.Headset, ref _headsetId, value);
    }

    public string HomeTheaterDeviceId
    {
        get => _homeTheaterId;
        set => SetDevice(Profile.HomeTheater, ref _homeTheaterId, value);
    }

    public string HeadsetTip => $"Game, Media and Aux on {Label(_headsetId)}. Chat isn't changed.";
    public string HomeTheaterTip => $"Game, Media and Aux on {Label(_homeTheaterId)}. Chat stays on the headset.";

    private string Label(string id) => _choices.FirstOrDefault(c => c.Id == id)?.Label ?? "no device";

    private void SetDevice(Profile profile, ref string field, string value)
    {
        if (value == null || value == field) return;
        field = value;
        OnPropertyChanged(profile == Profile.Headset ? nameof(HeadsetDeviceId) : nameof(HomeTheaterDeviceId));
        OnPropertyChanged(nameof(HeadsetTip));
        OnPropertyChanged(nameof(HomeTheaterTip));
        if (_syncing) return;
        mixer.SetProfileDevice(profile, value);
        report($"{(profile == Profile.Headset ? "Headset" : "Home Theater")} profile now uses {Label(value)}.");
    }

    public void Sync()
    {
        _syncing = true;
        try
        {
            var choices = mixer.ProfileChoices();
            if (!choices.SequenceEqual(_choices)) Choices = choices;
            _headsetId = mixer.ProfileDeviceId(Profile.Headset) ?? "";
            _homeTheaterId = mixer.ProfileDeviceId(Profile.HomeTheater) ?? "";
            _active = mixer.ActiveProfile();
            foreach (var shortcut in Shortcuts) shortcut.Sync(mixer);
            foreach (var name in new[] { nameof(HeadsetDeviceId), nameof(HomeTheaterDeviceId), nameof(HeadsetTip), nameof(HomeTheaterTip),
                                         nameof(IsHeadset), nameof(IsHomeTheater), nameof(IsNotHeadset), nameof(IsNotHomeTheater) })
                OnPropertyChanged(name);
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>One shortcut row in a channel's or the profiles' settings popup.</summary>
public sealed class ShortcutViewModel(HotkeySlot slot) : ObservableObject
{
    private string _text = "Not set";
    private bool _hasHotkey;
    private bool _isRecording;
    private string? _error;

    public HotkeySlot Slot => slot;
    public string Label => slot.Label;

    public void Sync(ChannelManager mixer)
    {
        if (!_isRecording) Show(mixer.GetHotkey(slot), mixer.HotkeyError(slot));
    }

    public bool HasHotkey
    {
        get => _hasHotkey;
        private set { _hasHotkey = value; OnPropertyChanged(); }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set { _isRecording = value; OnPropertyChanged(); OnPropertyChanged(nameof(ButtonText)); }
    }

    public string ButtonText => _isRecording ? "Press keys…" : _text;

    private string? _localError;

    public string? Error
    {
        get => _error;
        private set { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>A message from the recorder itself (e.g. keys already used); kept until the next recording.</summary>
    public void SetLocalError(string? message)
    {
        _localError = message;
        Error = message;
    }

    /// <summary>Shows the stored shortcut and any registration problem reported by Windows.</summary>
    public void Show(Hotkey? hotkey, string? registrationError)
    {
        _text = hotkey?.ToString() ?? "Not set";
        HasHotkey = hotkey != null;
        OnPropertyChanged(nameof(ButtonText));
        Error = registrationError ?? _localError;
    }
}

public sealed class ChannelViewModel : ObservableObject
{
    private readonly ChannelManager _mixer;
    private readonly Action<string> _report;
    private bool _syncing;
    private List<DeviceChoice> _choices = [];
    private string _deviceId = "";
    private double _volume;
    private bool _muted;

    public ChannelViewModel(Channel channel, ChannelManager mixer, Action<string> report)
    {
        Channel = channel;
        _mixer = mixer;
        _report = report;
        Apps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        Shortcuts = HotkeySlot.ChannelActions.Select(a => new ShortcutViewModel(new HotkeySlot(channel, a))).ToList();
    }

    public IReadOnlyList<ShortcutViewModel> Shortcuts { get; }

    public static string GlyphFor(Channel channel) => channel switch
    {
        Channel.Game => "",  // game controller
        Channel.Chat => "",  // message
        Channel.Media => "", // music note
        Channel.Aux => "",   // speaker
        _ => "",             // microphone
    };

    public Channel Channel { get; }
    public string Title => Channel.ToString();
    public bool IsMic => Channel == Channel.Mic;
    public bool AcceptsApps => !IsMic;
    public string EmptyHint => IsMic ? "No apps are using the mic" : "Drag apps here";
    public ObservableCollection<AppChip> Apps { get; } = [];
    public bool IsEmpty => Apps.Count == 0;

    public string Glyph => GlyphFor(Channel);

    public List<DeviceChoice> Choices
    {
        get => _choices;
        private set { _choices = value; OnPropertyChanged(); }
    }

    public string DeviceId
    {
        get => _deviceId;
        set
        {
            if (value == null || value == _deviceId) return;
            _deviceId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(DeviceName));
            if (_syncing) return;
            try
            {
                _mixer.SetDevice(Channel, value);
                string label = _choices.FirstOrDefault(c => c.Id == value)?.Label ?? "";
                _report(IsMic ? $"Mic now uses {label}." : $"{Title} now plays on {label}.");
            }
            catch (Exception ex)
            {
                _report($"Couldn't change the {Title} device: {ex.Message}");
            }
        }
    }

    private bool _noiseRemoval;
    private bool _noiseRemovalAvailable;

    /// <summary>Shown on Chat and Mic when NVIDIA Broadcast is installed.</summary>
    public bool NoiseRemovalAvailable
    {
        get => _noiseRemovalAvailable;
        private set { _noiseRemovalAvailable = value; OnPropertyChanged(); }
    }

    public bool NoiseRemoval
    {
        get => _noiseRemoval;
        set
        {
            if (value == _noiseRemoval) return;
            _noiseRemoval = value;
            OnPropertyChanged();
            if (_syncing) return;
            try
            {
                _mixer.SetNoiseRemoval(Channel, value);
                _report(value
                    ? $"Noise removal is on for {Title}. Its audio now goes through NVIDIA Broadcast."
                    : $"Noise removal is off for {Title}.");
            }
            catch (Exception ex)
            {
                _report($"Couldn't change noise removal for {Title}: {ex.Message}");
            }
        }
    }

    private bool _isSwitching;

    /// <summary>True while this channel's device switch is in progress (shows the loader).</summary>
    public bool IsSwitching
    {
        get => _isSwitching;
        private set
        {
            if (_isSwitching == value) return;
            _isSwitching = value;
            OnPropertyChanged();
        }
    }

    /// <summary>What the selected device actually runs at, e.g. "5.1 · 48 kHz · 24-bit".</summary>
    public string Format => _choices.FirstOrDefault(c => c.Id == _deviceId)?.Format ?? "";

    public string SettingsTitle => $"{Title} settings";

    private double _meter;

    /// <summary>
    /// Live audio level (0–1) of this channel for the meter under the volume slider. Windows measures streams
    /// before their volume is applied, so the level is scaled by what you actually hear: the channel volume
    /// (with ChatMix) for outputs, the mic level for Mic. 0 while muted.
    /// </summary>
    public double Meter
    {
        get => _meter;
        private set
        {
            if (Math.Abs(value - _meter) < 0.002) return;
            _meter = value;
            OnPropertyChanged();
        }
    }

    public void SetMeter(float level)
    {
        double heard = IsMic ? _volume / 100.0 : _mixer.HeardVolume(Channel);
        Meter = _muted ? 0 : Math.Clamp(level * heard, 0.0, 1.0);
    }

    private bool _isUsingFallback;

    /// <summary>The saved device is missing (e.g. NVIDIA Broadcast closed), so the channel uses a stand-in.</summary>
    public bool IsUsingFallback
    {
        get => _isUsingFallback;
        private set
        {
            if (_isUsingFallback == value) return;
            _isUsingFallback = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Full device name for the settings popup (the drop-down cuts long names off).</summary>
    public string DeviceName => _choices.FirstOrDefault(c => c.Id == _deviceId)?.Label ?? "No device";

    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(value - _volume) < 0.01) return;
            _volume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            if (!_syncing) ApplyLevel();
        }
    }

    public string VolumeText => $"{Math.Round(_volume)}%";

    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (value == _muted) return;
            _muted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteGlyph));
            if (!_syncing) ApplyLevel();
        }
    }

    public string MuteGlyph => _muted ? "" : "";

    private void ApplyLevel()
    {
        try { _mixer.SetLevel(Channel, (float)(_volume / 100.0), _muted); }
        catch (Exception ex) { _report($"Couldn't change the {Title} volume: {ex.Message}"); }
    }

    /// <summary>Pulls the current state from the mixer without echoing changes back to it.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var choices = _mixer.Choices(Channel);
            if (!choices.SequenceEqual(_choices)) Choices = choices;

            _deviceId = _mixer.DeviceId(Channel) ?? "";
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(DeviceName));
            IsSwitching = _mixer.IsSwitching(Channel);
            IsUsingFallback = _mixer.IsUsingFallback(Channel);

            foreach (var shortcut in Shortcuts) shortcut.Sync(_mixer);

            NoiseRemovalAvailable = _mixer.BroadcastDeviceId(Channel) != null;
            _noiseRemoval = _mixer.IsNoiseRemovalOn(Channel);
            OnPropertyChanged(nameof(NoiseRemoval));

            var (volume, muted) = _mixer.GetLevel(Channel);
            _volume = Math.Round(volume * 100);
            _muted = muted;
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteGlyph));

            // Chat also lists apps from other channels whose voice plays on the Chat device (Discord).
            var apps = _mixer.Apps
                .Where(a => IsMic ? a.HasInput : a.HasOutput && (a.Channel == Channel || (Channel == Channel.Chat && _mixer.PlaysOnChat(a))))
                .ToList();
            bool same = apps.Count == Apps.Count &&
                        apps.Zip(Apps).All(p => p.First.Key == p.Second.Key && p.First.IsPlaying == p.Second.IsPlaying &&
                                                p.First.Channel == p.Second.App.Channel);
            if (!same)
            {
                Apps.Clear();
                foreach (var app in apps)
                {
                    bool voiceOnly = app.Channel != Channel && !IsMic;
                    Apps.Add(new AppChip(app, IconHelper.ForExecutable(app.ExePath), canMove: !IsMic && !voiceOnly, badge: voiceOnly ? "Voice" : ""));
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
