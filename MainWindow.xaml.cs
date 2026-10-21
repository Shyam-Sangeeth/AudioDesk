using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public BusyViewModel Busy { get; } = new();
    public ObservableCollection<DeviceItem> Outputs { get; } = [];
    public ObservableCollection<DeviceItem> Inputs { get; } = [];

    public MainWindow(AudioService audio, ChannelManager mixer)
    {
        _audio = audio;
        _mixer = mixer;
        ChatMix = new ChatMixViewModel(mixer, Report);
        Profiles = new ProfilesViewModel(mixer, Report);
        InitializeComponent();
        DataContext = this;

        foreach (var channel in Enum.GetValues<Channel>())
            Channels.Add(new ChannelViewModel(channel, mixer, Report));

        // Changes can be raised from inside a binding update; sync after it finishes.
        _mixer.StateChanged += QueueSync;
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueSync(); };
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
        Busy.Sync(_mixer);
        Fill(Outputs, _audio.GetDevices(DataFlow.Render));
        Fill(Inputs, _audio.GetDevices(DataFlow.Capture));
        SonarBanner.Visibility = Process.GetProcessesByName("SteelSeriesSonar").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_mixer.LastError != null) StatusText.Text = _mixer.LastError;
        else if (!AppRouting.IsSupported) StatusText.Text = "Per-app routing isn't available on this Windows build, so apps can't be moved between devices.";
    }

    private static void Fill(ObservableCollection<DeviceItem> target, IReadOnlyList<DeviceInfo> devices)
    {
        // Skip rebuilding the list (and redrawing the Devices tab) when nothing changed.
        if (target.Count == devices.Count && target.Zip(devices.OrderByDescending(d => d.IsDefault)).All(p => p.First.Info == p.Second))
            return;
        target.Clear();
        foreach (var device in devices.OrderByDescending(d => d.IsDefault))
            target.Add(new DeviceItem(device));
    }

    private void Report(string message) => StatusText.Text = message;

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
