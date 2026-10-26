using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public ProfilesViewModel Profiles { get; }
    public ObservableCollection<DeviceItem> Outputs { get; } = [];

    public UpdateViewModel Updates { get; }

    public MainWindow(AudioService audio, ChannelManager mixer, MeterService meters, UpdateViewModel updates)
    {
        _audio = audio;
        _mixer = mixer;
        _meters = meters;
        Updates = updates;
        Profiles = new ProfilesViewModel(mixer, Report);
        InitializeComponent();
        DataContext = this;

        foreach (var channel in Enum.GetValues<Channel>())
            Channels.Add(new ChannelViewModel(channel, mixer, Report));

        // Changes can be raised from inside a binding update; sync after it finishes.
        _mixer.StateChanged += QueueSync;
        _mixer.LevelChanged += changed =>
        {
            foreach (var channel in Channels.Where(c => c.Channel == changed)) channel.SyncLevel();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) { QueueSync(); LoadSettings(); } };

        // Level meters: run only while the window can be seen, and follow every app scan.
        _mixer.Scanned += () => _meters.SetMap(_mixer.BuildMeterMap());
        _mixer.StateChanged += () => _meters.SetMap(_mixer.BuildMeterMap());
        _meters.LevelsChanged += OnLevels;
        _meters.LayoutsChanged += layouts => Dispatcher.BeginInvoke(() =>
        {
            foreach (var channel in Channels) channel.SetLayouts(layouts);
        });
        IsVisibleChanged += (_, _) => UpdateMetersEnabled();
        StateChanged += (_, _) => UpdateMetersEnabled();
    }

    // --- The title bar ---

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
        Profiles.Sync();
        // Real devices only; Sonar's and other virtual devices aren't picked here.
        Fill(Outputs, _audio.GetDevices(DataFlow.Render).Where(d => !d.IsVirtual).ToList());
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
            element.DataContext is not AppChip chip || chip != _pressedChip)
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
        bool accepts = (sender as FrameworkElement)?.Tag is ChannelViewModel && e.Data.GetDataPresent(typeof(AppChip));
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Channel_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChannelViewModel channel &&
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
        if (chip.App.Channel == channel) return;
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

    // --- Settings tab ---

    public void ShowSettings() => SettingsTab.IsSelected = true;

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAsync(quiet: false);

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await Updates.InstallAsync();

    private void ReleaseLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Report($"Couldn't open the release page: {ex.Message}"); }
        e.Handled = true;
    }

    private bool _loadingSettings;

    /// <summary>Shows the saved settings (read from Windows, in case they were changed elsewhere).</summary>
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            StartWithWindowsBox.IsChecked = StartupSetting.Enabled;
            KnobSupportBox.IsChecked = _mixer.LocalApiEnabled;
        }
        finally { _loadingSettings = false; }

        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version == null ? "AudioDesk" : $"AudioDesk {version.Major}.{version.Minor}.{version.Build}";
    }

    private void KnobSupport_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = KnobSupportBox.IsChecked == true;
        _mixer.LocalApiEnabled = on;
        Report(on ? "Knob support is on: Knob Hub's AudioDesk app can connect." : "Knob support is off: the knob can't control AudioDesk.");
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = StartWithWindowsBox.IsChecked == true;
        try
        {
            StartupSetting.Enabled = on;
            Report(on ? "AudioDesk will start in the tray when you sign in." : "AudioDesk won't start with Windows.");
        }
        catch (Exception ex)
        {
            Report($"Couldn't change Start with Windows: {ex.Message}");
            LoadSettings();
        }
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name || !Enum.TryParse<Profile>(name, out var profile)) return;
        try
        {
            bool sonarMode = _mixer.IsSonarMode;
            _mixer.ApplyProfile(profile);
            string where = profile == Profile.Headset ? "the headset" : "the home theater";
            Report(profile == Profile.Sonar
                ? sonarMode ? "Sonar off: Game, Media and Aux play straight on their devices."
                            : "Sonar: Game, Media and Aux go through SteelSeries Sonar."
                : sonarMode ? $"Sonar now plays Game, Media and Aux on {where}."
                            : $"Game, Media and Aux play straight on {where}.");
        }
        catch (Exception ex)
        {
            Report($"Couldn't switch profile: {ex.Message}");
        }
    }

    // --- Game's searchable Sonar preset list ---

    /// <summary>Puts the cursor in the search box as soon as the list opens.</summary>
    private void PresetPopup_Opened(object sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement root } && root.FindName("PresetSearchBox") is TextBox box)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { box.Focus(); Keyboard.Focus(box); });
    }

    /// <summary>Enter picks the first match; Esc closes the list.</summary>
    private void PresetSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChannelViewModel channel) return;
        if (e.Key == Key.Enter && channel.FilteredPresets.FirstOrDefault() is { } first)
        {
            channel.PickPreset(first.Preset);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            channel.IsPresetPickerOpen = false;
            e.Handled = true;
        }
    }

    private void PresetList_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox { DataContext: ChannelViewModel channel } list) return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: PresetItem item })
            channel.PickPreset(item.Preset);
    }

    /// <summary>Opens the classic Sound control panel on the Playback tab.</summary>
    private void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,0") { UseShellExecute = false }); }
        catch (Exception ex) { Report($"Couldn't open Sound settings: {ex.Message}"); }
    }

    // --- Devices tab ---

    private void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceItem device) return;
        SetDefault(device, $"{device.Name} is now the default playback device.", Role.Console, Role.Multimedia);
    }

    private async void SetDefault(DeviceItem device, string message, params Role[] roles)
    {
        try
        {
            await Task.Run(() => PolicyConfig.SetDefault(device.Id, roles));
            Report(message);
            _mixer.Refresh();
        }
        catch (Exception ex)
        {
            Report($"Couldn't switch to {device.Name}: {ex.Message}");
        }
    }
}
