using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioDesk.Audio;
using AudioDesk.Mixer;
using NAudio.CoreAudioApi;

namespace AudioDesk;

public partial class MainWindow : Window
{
    private readonly AudioService _audio;
    private readonly ChannelManager _mixer;
    private Point _dragStart;
    private AppChip? _pressedChip;
    private bool _syncQueued;

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public ChatMixViewModel ChatMix { get; }
    public ProfilesViewModel Profiles { get; }
    public ObservableCollection<DeviceItem> Outputs { get; } = [];
    public ObservableCollection<DeviceItem> Inputs { get; } = [];

    public MainWindow(AudioService audio, ChannelManager mixer, MeterService meters)
    {
        _audio = audio;
        _mixer = mixer;
        _meters = meters;
        ChatMix = new ChatMixViewModel(mixer, Report);
        Profiles = new ProfilesViewModel(mixer, Report);
        InitializeComponent();
        DataContext = this;

        foreach (var channel in Enum.GetValues<Channel>())
            Channels.Add(new ChannelViewModel(channel, mixer, Report));

        // Changes can be raised from inside a binding update; sync after it finishes.
        _mixer.StateChanged += QueueSync;
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueSync(); };

        // Level meters: run only while the window can be seen, and follow every app scan.
        _mixer.Scanned += () => _meters.SetMap(_mixer.BuildMeterMap());
        _mixer.StateChanged += () => _meters.SetMap(_mixer.BuildMeterMap());
        _meters.LevelsChanged += OnLevels;
        IsVisibleChanged += (_, _) => UpdateMetersEnabled();
        StateChanged += (_, _) => UpdateMetersEnabled();
    }

    private readonly MeterService _meters;
    private IReadOnlyDictionary<Channel, float>? _pendingLevels;

    private void UpdateMetersEnabled() => _meters.Enabled = IsVisible && WindowState != WindowState.Minimized;

    /// <summary>Called ~30×/s on the meter thread; hands only the latest levels to the UI thread.</summary>
    private void OnLevels(IReadOnlyDictionary<Channel, float> levels)
    {
        if (Interlocked.Exchange(ref _pendingLevels, levels) != null) return; // an update is already queued
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var latest = Interlocked.Exchange(ref _pendingLevels, null);
            if (latest == null) return;
            foreach (var channel in Channels)
                channel.SetMeter(latest.GetValueOrDefault(channel.Channel));
        });
    }

    private void QueueSync()
    {
        if (_syncQueued || !IsVisible) return;
        _syncQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _syncQueued = false;
            SyncAll();
        });
    }

    private void SyncAll()
    {
        foreach (var channel in Channels) channel.Sync();
        ChatMix.Sync();
        Profiles.Sync();
        Fill(Outputs, _audio.GetDevices(DataFlow.Render));
        Fill(Inputs, _audio.GetDevices(DataFlow.Capture));
        SonarBanner.Visibility = Process.GetProcessesByName("SteelSeriesSonar").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Show problems once when they appear (not on every sync, or they'd never fade out).
        string? problem = _mixer.LastError ?? (AppRouting.IsSupported ? null
            : "Per-app routing isn't available on this Windows build, so apps can't be moved between devices.");
        if (problem != null && problem != _lastProblem) Report(problem);
        _lastProblem = problem;
    }

    private void Fill(ObservableCollection<DeviceItem> target, IReadOnlyList<DeviceInfo> devices)
    {
        var items = devices.OrderByDescending(d => d.IsDefault)
            .Select(d => new DeviceItem(d, _mixer.IsKeepAwake(d.Id), _mixer.KeepAwakeLevel(d.Id), _mixer.KeepAwakeError(d.Id),
                                        _mixer.IsKeepAwakePaused(d.Id), _mixer.IsKeepAwakePlaying(d.Id)))
            .ToList();
        // Skip rebuilding the list (and redrawing the Devices tab) when nothing changed.
        if (target.Count == items.Count && target.Zip(items).All(p => p.First.Info == p.Second.Info &&
                                                                      p.First.KeepAwake == p.Second.KeepAwake &&
                                                                      p.First.KeepAwakeDb == p.Second.KeepAwakeDb &&
                                                                      p.First.KeepAwakeError == p.Second.KeepAwakeError &&
                                                                      p.First.KeepAwakePaused == p.Second.KeepAwakePaused &&
                                                                      p.First.KeepAwakePlaying == p.Second.KeepAwakePlaying))
            return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    /// <summary>Checked/Unchecked (not Click) so keyboard and accessibility toggles work too. Also fires when
    /// the list is filled, so only act when the box disagrees with the saved setting.</summary>
    private void KeepAwake_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: DeviceItem device } box) return;
        bool on = box.IsChecked == true;
        if (on == _mixer.IsKeepAwake(device.Id)) return;
        _mixer.SetKeepAwake(device.Id, on);
        Report(on ? $"Keeping {device.Name} awake with quiet noise." : $"{device.Name} is no longer kept awake.");
    }

    /// <summary>Also fires when the list is filled, so only act on a real change of level.</summary>
    private void KeepAwakeLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: DeviceItem device, SelectedValue: double db }) return;
        if (!_mixer.IsKeepAwake(device.Id) || db == _mixer.KeepAwakeLevel(device.Id)) return;
        _mixer.SetKeepAwake(device.Id, true, db);
        Report($"{device.Name} keep-awake level: {NoiseLevel.All.First(l => l.Db == db).Label}.");
    }

    private string? _lastProblem;
    private DispatcherTimer? _statusTimer;

    /// <summary>Shows a message in the status line; it clears itself 5 seconds after the latest message.</summary>
    private void Report(string message)
    {
        StatusText.Text = message;
        if (_statusTimer == null)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                StatusText.Text = "";
            };
        }
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // --- Moving apps between channels ---

    private void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _pressedChip = (sender as FrameworkElement)?.DataContext as AppChip;
    }

    private void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element ||
            element.DataContext is not AppChip chip || chip != _pressedChip || !chip.CanMove)
            return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pressedChip = null;
        DragDrop.DoDragDrop(element, new DataObject(typeof(AppChip), chip), DragDropEffects.Move);
    }

    private void Channel_DragOver(object sender, DragEventArgs e)
    {
        bool accepts = (sender as FrameworkElement)?.Tag is ChannelViewModel { AcceptsApps: true } &&
                       e.Data.GetDataPresent(typeof(AppChip));
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Channel_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChannelViewModel { AcceptsApps: true } channel &&
            e.Data.GetData(typeof(AppChip)) is AppChip chip)
            Move(chip, channel.Channel);
    }

    private void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AppChip chip, Tag: string name } && Enum.TryParse<Channel>(name, out var channel))
            Move(chip, channel);
    }

    private void Move(AppChip chip, Channel channel)
    {
        if (!chip.CanMove || chip.App.Channel == channel) return;
        try
        {
            _mixer.MoveApp(chip.App, channel);
            Report($"{chip.Name} moved to {channel}. If it keeps playing on the old device, restart it.");
        }
        catch (Exception ex)
        {
            Report($"Couldn't move {chip.Name}: {ex.Message}");
        }
    }

    private void ChatMix_DoubleClick(object sender, MouseButtonEventArgs e) => ChatMix.Center();

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name || !Enum.TryParse<Profile>(name, out var profile)) return;
        try
        {
            _mixer.ApplyProfile(profile);
            Report(profile == Profile.Headset
                ? "Headset profile: Game, Media and Aux play on the headset."
                : "Home Theater profile: Game, Media and Aux play on the home theater. Chat stays on the headset.");
        }
        catch (Exception ex)
        {
            Report($"Couldn't switch profile: {ex.Message}");
        }
    }

    // --- Shortcut recorder ---

    private ShortcutViewModel? _recording;

    private static readonly HashSet<Key> ModifierOnlyKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin,
    ];

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutViewModel shortcut) return;
        if (_recording != null && _recording != shortcut) StopRecording(_recording);
        _recording = shortcut;
        shortcut.SetLocalError(null);
        shortcut.IsRecording = true;
        _mixer.SetHotkeyRecording(true);
        ((Button)sender).Focus();
    }

    private void Shortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutViewModel { IsRecording: true } shortcut) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ModifierOnlyKeys.Contains(key)) return; // wait for the actual key

        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording(shortcut);
            return;
        }
        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            ApplyShortcut(shortcut, null);
            return;
        }

        // Plain letters would fire while typing anywhere, so they need a modifier; F-keys can stand alone.
        bool functionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !functionKey)
        {
            shortcut.SetLocalError("Hold Ctrl, Alt or Shift with the key (F-keys work on their own).");
            return;
        }
        ApplyShortcut(shortcut, new Hotkey(modifiers, key));
    }

    private void Shortcut_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as Button)?.Tag is ShortcutViewModel { IsRecording: true } shortcut)
            StopRecording(shortcut);
    }

    private void ShortcutClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ShortcutViewModel shortcut)
            ApplyShortcut(shortcut, null);
    }

    private void ApplyShortcut(ShortcutViewModel shortcut, Hotkey? hotkey)
    {
        string? error = _mixer.SetHotkey(shortcut.Slot, hotkey);
        StopRecording(shortcut);
        if (error != null)
        {
            shortcut.SetLocalError(error);
            return;
        }
        Report(hotkey == null
            ? $"Removed the shortcut for {shortcut.Slot}."
            : $"{hotkey} now does {shortcut.Slot}.");
    }

    private void StopRecording(ShortcutViewModel shortcut)
    {
        shortcut.IsRecording = false;
        if (_recording == shortcut) _recording = null;
        _mixer.SetHotkeyRecording(false);
        QueueSync();
    }

    /// <summary>Opens the classic Sound control panel on the Playback or Recording tab.</summary>
    private void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        int tab = (sender as FrameworkElement)?.Tag is ChannelViewModel { IsMic: true } ? 1 : 0;
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,Control_RunDLL mmsys.cpl,,{tab}") { UseShellExecute = false }); }
        catch (Exception ex) { Report($"Couldn't open Sound settings: {ex.Message}"); }
    }

    private void ChatMixBalance_Click(object sender, RoutedEventArgs e)
    {
        ChatMix.Center();
        Report("ChatMix balanced: Game and Chat play at full volume.");
    }

    // --- Devices tab ---

    private void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceItem device) return;
        SetDefault(device, $"{device.Name} is now the default {(device.Flow == DataFlow.Render ? "playback" : "recording")} device.",
                   Role.Console, Role.Multimedia);
    }

    private void MakeCommunications_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceItem device) return;
        SetDefault(device, $"{device.Name} is now used for calls.", Role.Communications);
    }

    private void SetDefault(DeviceItem device, string message, params Role[] roles)
    {
        try
        {
            PolicyConfig.SetDefault(device.Id, roles);
            Report(message);
            _mixer.Refresh();
        }
        catch (Exception ex)
        {
            Report($"Couldn't switch to {device.Name}: {ex.Message}");
        }
    }
}
