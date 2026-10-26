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
                               string? keepAwakeError = null, bool keepAwakePaused = false, bool keepAwakePlaying = false)
{
    public DeviceInfo Info => info;
    public bool KeepAwake => keepAwake;

    /// <summary>Keep awake is on, but no channel uses this device, so no noise is playing.</summary>
    public bool KeepAwakePaused => keepAwakePaused;

    /// <summary>Keep-awake noise is audibly playing on this device right now (not paused for other audio).</summary>
    public bool KeepAwakePlaying => keepAwakePlaying;
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
    public DataFlow Flow => info.Flow;

    public string Glyph
    {
        get
        {
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

public sealed class AppChip(MixerApp app, ImageSource? icon) : ObservableObject
{
    private string _layout = "";
    public MixerApp App => app;
    public string Key => app.Key;
    public string Name => app.Name;
    public ImageSource? Icon => icon;
    public bool IsPlaying => app.IsPlaying;
    public string Tip => $"{app.Name}: drag to another channel, or right-click.";

    /// <summary>
    /// The speakers this app fed in the last few seconds ("Stereo", "5.1", "7.1", "Mono"); empty on stereo devices,
    /// on devices whose enhancements remix every stream, or while the app is silent.
    /// </summary>
    public string Layout
    {
        get => _layout;
        set
        {
            if (value == _layout) return;
            _layout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLayout));
            OnPropertyChanged(nameof(LayoutTip));
        }
    }

    public bool HasLayout => _layout.Length > 0;

    public string LayoutTip => _layout switch
    {
        "Stereo" => $"{app.Name} is only playing on the front left and right speakers.",
        "Mono" => $"{app.Name} is only playing on the centre speaker.",
        _ => $"{app.Name} is playing {_layout} audio: sound reached the centre, subwoofer or surround speakers in the last few seconds.",
    };
}

/// <summary>Settings → Updates: check GitHub for a newer release, download it and run its installer.</summary>
public sealed class UpdateViewModel(ChannelManager mixer, Action exitForUpdate) : ObservableObject
{
    private Updates.UpdateInfo? _update;
    private string _status = $"You have AudioDesk {Updates.UpdateService.CurrentVersion}.";
    private bool _busy;
    private bool _downloading;
    private double _progress;

    /// <summary>Raised when a check finds a newer release.</summary>
    public event Action<Updates.UpdateInfo>? UpdateFound;

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public bool IsUpdateAvailable => _update != null;
    public bool CanCheck => !_busy;
    public bool CanInstall => _update != null && !_busy;
    public bool IsDownloading => _downloading;
    public string InstallText => _update == null ? "" : $"Download and install {_update.Version}";
    public string ReleaseUrl => _update?.PageUrl ?? "https://github.com/Shyam-Sangeeth/AudioDesk/releases";

    public double Progress
    {
        get => _progress;
        private set { _progress = value; OnPropertyChanged(); }
    }

    public bool AutoCheck
    {
        get => mixer.CheckForUpdates;
        set
        {
            if (value == mixer.CheckForUpdates) return;
            mixer.CheckForUpdates = value;
            OnPropertyChanged();
        }
    }

    private void SetBusy(bool busy, bool downloading = false)
    {
        _busy = busy;
        _downloading = downloading;
        foreach (var name in new[] { nameof(CanCheck), nameof(CanInstall), nameof(IsDownloading), nameof(IsUpdateAvailable),
                                     nameof(InstallText), nameof(ReleaseUrl) })
            OnPropertyChanged(name);
    }

    /// <summary>Checks GitHub. <paramref name="quiet"/> keeps error messages short (automatic checks).</summary>
    public async Task CheckAsync(bool quiet)
    {
        if (_busy) return;
        SetBusy(true);
        Status = "Checking for updates…";
        try
        {
            _update = await Updates.UpdateService.CheckAsync();
            Status = _update == null
                ? $"You're up to date (AudioDesk {Updates.UpdateService.CurrentVersion})."
                : $"AudioDesk {_update.Version} is available (you have {Updates.UpdateService.CurrentVersion}).";
            if (_update != null) UpdateFound?.Invoke(_update);
        }
        catch (Exception ex)
        {
            Status = quiet ? "Couldn't check for updates." : $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Downloads and verifies the installer, starts it, then closes AudioDesk so it can be replaced.</summary>
    public async Task InstallAsync()
    {
        if (_update == null || _busy) return;
        SetBusy(true, downloading: true);
        Progress = 0;
        Status = $"Downloading AudioDesk {_update.Version}…";
        try
        {
            string installer = await Updates.UpdateService.DownloadAsync(_update, new Progress<double>(p => Progress = p * 100));
            Status = "Installing… AudioDesk will restart when it's done.";
            Updates.UpdateService.RunInstaller(installer);
            await Task.Delay(1500);
            exitForUpdate();
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
            SetBusy(false);
        }
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

    private bool _sonarAvailable;

    /// <summary>The Sonar profile is offered when SteelSeries Sonar is installed.</summary>
    public bool SonarAvailable
    {
        get => _sonarAvailable;
        private set { _sonarAvailable = value; OnPropertyChanged(); }
    }

    private bool _sonarMode;

    // Sonar is highlighted while every channel goes through Sonar; Headset/Home Theater can be highlighted at the same
    // time, showing where Sonar plays.
    public bool ShowSonarActive => _sonarAvailable && _sonarMode;
    public bool ShowSonarInactive => _sonarAvailable && !_sonarMode;

    public static string NameOf(Profile profile) => profile switch
    {
        Profile.Sonar => "Sonar",
        Profile.Headset => "Headset",
        _ => "Home Theater",
    };

    public string SonarTip => _sonarMode
        ? "Everything goes through SteelSeries Sonar. Click to play Game, Media and Aux straight on their devices again."
        : "Send Game, Media and Aux all through SteelSeries Sonar.";

    public string HeadsetTip => _sonarMode
        ? $"Sonar plays Game, Media and Aux on {Label(_headsetId)}."
        : $"Game, Media and Aux straight on {Label(_headsetId)}, not through Sonar.";
    public string HomeTheaterTip => _sonarMode
        ? $"Sonar plays Game, Media and Aux on {Label(_homeTheaterId)}."
        : $"Game, Media and Aux straight on {Label(_homeTheaterId)}, not through Sonar.";

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
            SonarAvailable = mixer.IsSonarAvailable;
            _sonarMode = mixer.IsSonarMode;
            foreach (var name in new[] { nameof(HeadsetDeviceId), nameof(HomeTheaterDeviceId), nameof(HeadsetTip), nameof(HomeTheaterTip),
                                         nameof(IsHeadset), nameof(IsHomeTheater), nameof(IsNotHeadset), nameof(IsNotHomeTheater),
                                         nameof(ShowSonarActive), nameof(ShowSonarInactive), nameof(SonarTip) })
                OnPropertyChanged(name);
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>A row in Game's searchable Sonar preset list.</summary>
public sealed record PresetItem(SonarPreset Preset, bool IsFavorite, bool IsSelected)
{
    public string Name => Preset.Name;
}

/// <summary>
/// One row of a channel's "Sonar preset for each device" list. The first row (no device) is the default preset, for
/// devices without their own; the other rows are a device and its own preset, or "Use default".
/// </summary>
public sealed class PresetLinkViewModel(Channel channel, DeviceChoice? device, IReadOnlyList<SonarPreset> choices, string presetId,
                                        ChannelManager mixer, Action<string> report) : ObservableObject
{
    /// <summary>First choice of the default row: no default, the preset is left alone.</summary>
    public static readonly SonarPreset DontChange = new("", "Don't change");

    /// <summary>First choice of a device row: no preset of its own.</summary>
    public static readonly SonarPreset UseDefault = new("", "Use default");

    private string _presetId = presetId;

    public bool IsDefault => device == null;
    public string DeviceName => device?.Label ?? "Default";
    public IReadOnlyList<SonarPreset> Choices => choices;

    public string PresetId
    {
        get => _presetId;
        set
        {
            if (value == null || value == _presetId) return;
            _presetId = value;
            OnPropertyChanged();
            var preset = choices.FirstOrDefault(p => p.Id == value && p.Id.Length > 0);
            if (device == null)
            {
                mixer.SetDefaultPreset(channel, preset);
                report(preset == null ? $"{channel}: no default Sonar preset." : $"{channel}: default Sonar preset is {preset.Name}.");
            }
            else
            {
                mixer.SetPresetLink(channel, device.Id, preset);
                report(preset == null ? $"{channel}: {device.Label} uses the default Sonar preset."
                                      : $"{channel}: Sonar switches to {preset.Name} on {device.Label}.");
            }
        }
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
    }

    public static string GlyphFor(Channel channel) => channel switch
    {
        Channel.Game => "",  // game controller
        Channel.Media => "", // music note
        _ => "",           // speaker
    };

    public Channel Channel { get; }
    public string Title => Channel.ToString();
    public string EmptyHint => "Drag apps here";
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
                bool viaSonar = _mixer.SonarOutputId(Channel) != null;
                _mixer.PickDevice(Channel, value);
                string label = _choices.FirstOrDefault(c => c.Id == value)?.Label ?? "";
                _report(viaSonar ? $"Sonar now plays {Title} on {label}." : $"{Title} now plays on {label}.");
            }
            catch (Exception ex)
            {
                _report($"Couldn't change the {Title} device: {ex.Message}");
            }
        }
    }

    private bool _throughSonar;
    private bool _sonarAvailable;

    /// <summary>Shown when SteelSeries Sonar is installed.</summary>
    public bool SonarAvailable
    {
        get => _sonarAvailable;
        private set { _sonarAvailable = value; OnPropertyChanged(); }
    }


    public bool ThroughSonar
    {
        get => _throughSonar;
        set
        {
            if (value == _throughSonar) return;
            _throughSonar = value;
            OnPropertyChanged();
            if (_syncing) return;
            try
            {
                _mixer.SetThroughSonar(Channel, value);
                _report(value
                    ? $"{Title} now goes through SteelSeries Sonar."
                    : $"{Title} no longer goes through Sonar.");
            }
            catch (Exception ex)
            {
                _report($"Couldn't change Sonar for {Title}: {ex.Message}");
            }
        }
    }

    private List<SonarPreset> _presets = [];
    private string _presetId = "";

    /// <summary>Sonar presets for the picker (your Sonar favourites) while the channel goes through Sonar.</summary>
    public List<SonarPreset> Presets
    {
        get => _presets;
        private set
        {
            _presets = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPresets));
            OnPropertyChanged(nameof(ShowPresetCombo));
            OnPropertyChanged(nameof(ShowPresetSearch));
        }
    }

    public bool HasPresets => _presets.Count > 0;

    // Game has hundreds of Sonar presets, so it gets a searchable list (favourites only by default) instead of the
    // drop-down Media and Aux use.
    private bool UsesPresetSearch => Channel == Channel.Game;
    public bool ShowPresetCombo => HasPresets && !UsesPresetSearch;
    public bool ShowPresetSearch => HasPresets && UsesPresetSearch;

    public string SelectedPresetName =>
        _mixer.AllSonarPresets(Channel).Concat(_presets).FirstOrDefault(p => p.Id == _presetId)?.Name ?? "";

    private bool _isPresetPickerOpen;
    private string _presetSearch = "";
    private bool _favoritesOnly = true;
    private List<PresetItem> _filteredPresets = [];

    public bool IsPresetPickerOpen
    {
        get => _isPresetPickerOpen;
        set
        {
            if (value == _isPresetPickerOpen) return;
            _isPresetPickerOpen = value;
            OnPropertyChanged();
            if (value) PresetSearch = ""; // each time it opens, start with the full list
        }
    }

    public string PresetSearch
    {
        get => _presetSearch;
        set
        {
            if ((value ?? "") == _presetSearch) return;
            _presetSearch = value ?? "";
            OnPropertyChanged();
            RefreshFilteredPresets();
        }
    }

    /// <summary>On (the default): only the presets marked as favourites in Sonar. Off: every preset.</summary>
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (value == _favoritesOnly) return;
            _favoritesOnly = value;
            OnPropertyChanged();
            RefreshFilteredPresets();
        }
    }

    public List<PresetItem> FilteredPresets
    {
        get => _filteredPresets;
        private set { _filteredPresets = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoPresetsFound)); }
    }

    public bool NoPresetsFound => _filteredPresets.Count == 0;

    private void RefreshFilteredPresets()
    {
        if (!UsesPresetSearch) return;
        var favorites = _mixer.FavoriteSonarPresets(Channel);
        var favoriteIds = favorites.Select(p => p.Id).ToHashSet();
        var source = _favoritesOnly ? favorites : _mixer.AllSonarPresets(Channel);
        var items = source
            .Where(p => _presetSearch.Length == 0 || p.Name.Contains(_presetSearch, StringComparison.OrdinalIgnoreCase))
            .Select(p => new PresetItem(p, favoriteIds.Contains(p.Id), p.Id == _presetId))
            .ToList();
        if (!items.SequenceEqual(_filteredPresets)) FilteredPresets = items;
    }

    /// <summary>A preset clicked in Game's searchable list.</summary>
    public void PickPreset(SonarPreset preset)
    {
        IsPresetPickerOpen = false;
        if (preset.Id == _presetId) return;
        _presetId = preset.Id;
        OnPropertyChanged(nameof(PresetId));
        OnPropertyChanged(nameof(SelectedPresetName));
        RefreshFilteredPresets();
        _mixer.SelectSonarPreset(Channel, preset);
        _report($"Sonar preset for {Title}: {preset.Name}.");
    }

    /// <summary>Media/Aux settings: which Sonar preset each device switches to (shown while Sonar has favourites).</summary>
    public ObservableCollection<PresetLinkViewModel> PresetLinks { get; } = [];

    public bool ShowPresetLinks => PresetLinks.Count > 0;

    private string _presetLinksKey = "";

    /// <summary>Rebuilds the rows only when the devices, favourites or links change, so an open drop-down isn't reset.</summary>
    private void SyncPresetLinks()
    {
        var favorites = ChannelManager.SupportsPresetLinks(Channel) ? _mixer.FavoriteSonarPresets(Channel) : [];
        var devices = favorites.Count > 0 ? _mixer.ProfileChoices() : [];
        string defaultId = favorites.Count > 0 ? _mixer.DefaultPresetId(Channel) ?? "" : "";
        var links = devices.Select(d => _mixer.LinkedPresetId(Channel, d.Id) ?? "").ToList();
        string key = string.Join("|", devices.Select(d => d.Id)) + "#" + string.Join("|", favorites.Select(p => p.Id)) + "#" +
                     defaultId + "#" + string.Join("|", links);
        if (key == _presetLinksKey) return;
        _presetLinksKey = key;

        // A linked preset that's no longer a favourite stays listed, so the row doesn't look unlinked.
        List<SonarPreset> Choices(SonarPreset first, string linked)
        {
            var choices = new List<SonarPreset> { first };
            choices.AddRange(favorites);
            if (linked.Length > 0 && choices.All(p => p.Id != linked)) choices.Add(new SonarPreset(linked, "Linked preset (not a favourite)"));
            return choices;
        }

        PresetLinks.Clear();
        if (favorites.Count > 0)
            PresetLinks.Add(new PresetLinkViewModel(Channel, null, Choices(PresetLinkViewModel.DontChange, defaultId), defaultId, _mixer, _report));
        for (int i = 0; i < devices.Count; i++)
            PresetLinks.Add(new PresetLinkViewModel(Channel, devices[i], Choices(PresetLinkViewModel.UseDefault, links[i]), links[i], _mixer, _report));
        OnPropertyChanged(nameof(ShowPresetLinks));
    }

    public string PresetId
    {
        get => _presetId;
        set
        {
            if (value == null || value == _presetId) return;
            _presetId = value;
            OnPropertyChanged();
            if (_syncing || _presets.FirstOrDefault(p => p.Id == value) is not { } preset) return;
            _mixer.SelectSonarPreset(Channel, preset);
            _report($"Sonar preset for {Title}: {preset.Name}.");
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

    private string? _sonarFormat;

    /// <summary>What the channel actually runs at, e.g. "5.1 · 48 kHz · 24-bit": the selected device's format, or
    /// Sonar's processing format while the channel goes through Sonar.</summary>
    public string Format => _sonarFormat != null
        ? $"Sonar · {_sonarFormat}"
        : _choices.FirstOrDefault(c => c.Id == _deviceId)?.Format ?? "";

    public string SettingsTitle => $"{Title} settings";

    private bool _isKeepAwakePlaying;

    /// <summary>Keep-awake noise is playing on this channel's device (shown as a small symbol after the format).</summary>
    public bool IsKeepAwakePlaying
    {
        get => _isKeepAwakePlaying;
        private set
        {
            if (_isKeepAwakePlaying == value) return;
            _isKeepAwakePlaying = value;
            OnPropertyChanged();
        }
    }

    private double _meter;

    /// <summary>
    /// Live audio level (0–1) of this channel for the meter under the volume slider. Windows measures streams
    /// before their volume is applied, so the level is scaled by what you actually hear: the channel volume.
    /// 0 while muted.
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

    private IReadOnlyDictionary<(Channel Channel, uint ProcessId), string> _layouts =
        new Dictionary<(Channel, uint), string>();

    /// <summary>Shows which speakers each app feeds (from <see cref="MeterService.LayoutsChanged"/>).</summary>
    public void SetLayouts(IReadOnlyDictionary<(Channel Channel, uint ProcessId), string> layouts)
    {
        _layouts = layouts;
        foreach (var chip in Apps) ApplyLayout(chip);
    }

    private void ApplyLayout(AppChip chip)
    {
        // An app with several processes (browsers) takes the widest layout any of them plays.
        var found = chip.App.ProcessIds.Select(pid => _layouts.GetValueOrDefault((Channel, pid)) ?? "").ToList();
        chip.Layout = found.OrderByDescending(LayoutRank).FirstOrDefault() ?? "";
    }

    private static int LayoutRank(string layout) => layout switch
    {
        "" => 0,
        "Mono" => 1,
        "Stereo" => 2,
        "7.1" => 5,
        "5.1" => 4,
        _ => 3,
    };

    public void SetMeter(float level)
    {
        double volume = _volume / 100.0;
        if (_muted || volume <= 0)
        {
            Meter = 0;
            return;
        }

        // The meter is drawn inside the slider track, filling the coloured part (left of the knob) in proportion
        // to the level, like Windows does. That part ends at the knob's left edge, roughly 80% of the way to the
        // volume position (the track spans the full width; the knob is about a fifth of it across).
        double filledPart = volume * 0.8;
        Meter = filledPart * Math.Clamp(level, 0.0, 1.0);
    }

    private bool _isUsingFallback;

    /// <summary>The saved device is missing (e.g. unplugged, or Sonar closed), so the channel uses a stand-in.</summary>
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

    /// <summary>Pulls just the volume and mute from the mixer (they were changed elsewhere, e.g. on the knob).</summary>
    public void SyncLevel()
    {
        var (volume, muted) = _mixer.GetLevel(Channel);
        _volume = Math.Round(volume * 100);
        _muted = muted;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(MuteGlyph));
    }

    /// <summary>Pulls the current state from the mixer without echoing changes back to it.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            // While the channel goes through Sonar, the picker shows (and changes) the device Sonar plays it on.
            var choices = _mixer.PickerChoices(Channel);
            if (!choices.SequenceEqual(_choices)) Choices = choices;

            _deviceId = _mixer.PickerDeviceId(Channel) ?? "";
            _sonarFormat = _mixer.SonarFormat(Channel);

            var presets = _mixer.SonarPresets(Channel);
            if (!presets.SequenceEqual(_presets)) Presets = presets;
            _presetId = _mixer.SelectedSonarPreset(Channel)?.Id ?? "";
            OnPropertyChanged(nameof(PresetId));
            OnPropertyChanged(nameof(SelectedPresetName));
            RefreshFilteredPresets();
            SyncPresetLinks();
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(DeviceName));
            IsSwitching = _mixer.IsSwitching(Channel);
            IsUsingFallback = _mixer.IsUsingFallback(Channel);
            IsKeepAwakePlaying = _mixer.IsKeepAwakePlaying(_mixer.DeviceId(Channel));

            SonarAvailable = _mixer.SonarDeviceId(Channel) != null;
            _throughSonar = _mixer.IsThroughSonar(Channel);
            OnPropertyChanged(nameof(ThroughSonar));

            var (volume, muted) = _mixer.GetLevel(Channel);
            _volume = Math.Round(volume * 100);
            _muted = muted;
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteGlyph));

            var apps = _mixer.Apps.Where(a => a.Channel == Channel && _mixer.IsShown(a)).ToList();
            bool same = apps.Count == Apps.Count &&
                        apps.Zip(Apps).All(p => p.First.Key == p.Second.Key && p.First.IsPlaying == p.Second.IsPlaying);
            if (!same)
            {
                Apps.Clear();
                foreach (var app in apps)
                {
                    var chip = new AppChip(app, IconHelper.ForExecutable(app.ExePath));
                    ApplyLayout(chip);
                    Apps.Add(chip);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
