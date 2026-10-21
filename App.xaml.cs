using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AudioDesk.Audio;
using AudioDesk.Mixer;
using NAudio.CoreAudioApi;

namespace AudioDesk;

public partial class App : Application
{
    private const string InstanceName = "AudioDesk.SingleInstance";
    private const string ShowEventName = "AudioDesk.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private AudioService? _audio;
    private ChannelManager? _mixer;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _debounce;
    private HotkeyService? _hotkeys;
    private KeepAwakeService? _keepAwake;
    private OsdWindow? _osd;
    private bool _hintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF's GPU path draws an empty white window on this machine; the software renderer
        // is plenty fast for a small settings window.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        int selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
        {
            RunSelfTest(selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : Path.Combine(Path.GetTempPath(), "AudioDesk-selftest.txt"));
            Shutdown();
            return;
        }

        // Second launch: ask the running copy to show its window, then quit.
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();

        _audio = new AudioService();
        _mixer = new ChannelManager(_audio);

        // Device notifications arrive on a COM thread and in bursts; refresh once they settle.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _mixer.Refresh();
            _tray?.UpdateTooltip();
            _keepAwake?.DevicesChanged();
        };
        _audio.DevicesChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

        _tray = new TrayIcon(_mixer, IconHelper.LoadTrayIcon(), ShowWindow, ExitApp);

        _window = new MainWindow(_audio, _mixer);
        _window.Closing += (_, args) =>
        {
            // Closing the window keeps AudioDesk running (and routing apps) in the tray.
            args.Cancel = true;
            _window.Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray.ShowHint("AudioDesk is still running", "It keeps your channels working. Right-click the tray icon to change devices, or choose Exit.");
            }
        };

        _osd = new OsdWindow();
        _hotkeys = new HotkeyService();
        _mixer.HotkeysChanged += RegisterHotkeys;

        // Quiet noise on devices that must not fall asleep (e.g. a soundbar on optical), but only while
        // a channel uses them; re-checked whenever channels, profiles or devices change.
        _keepAwake = new KeepAwakeService();
        _keepAwake.StatusChanged += (id, error) => Dispatcher.BeginInvoke(() => _mixer?.SetKeepAwakeError(id, error));
        _mixer.KeepAwakeChanged += ApplyKeepAwake;
        _mixer.StateChanged += ApplyKeepAwake;
        ApplyKeepAwake();

        _mixer.Start();
        RegisterHotkeys();
        if (!e.Args.Contains("--minimized"))
            ShowWindow();
    }

    private string _keepAwakeApplied = "";

    /// <summary>Plays keep-awake noise on the kept-awake devices that a channel uses; only touches the
    /// streams when that set (or a level) actually changes.</summary>
    private void ApplyKeepAwake()
    {
        if (_mixer == null || _keepAwake == null) return;
        var active = _mixer.ActiveKeepAwake();
        string signature = string.Join("|", active.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        if (signature == _keepAwakeApplied) return;
        _keepAwakeApplied = signature;
        _keepAwake.Apply(active);
    }

    /// <summary>(Re)registers every channel shortcut with Windows and reports combinations other apps own.</summary>
    private void RegisterHotkeys()
    {
        if (_hotkeys == null || _mixer == null) return;
        _hotkeys.UnregisterAll();
        if (_mixer.IsRecordingHotkey) return; // let the keys reach the recorder

        var errors = new List<(HotkeySlot, string)>();
        foreach (var (slot, hotkey) in _mixer.AllHotkeys())
        {
            bool repeat = slot.Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown; // holding the keys keeps changing volume
            if (!_hotkeys.Register(hotkey, repeat, () => OnHotkey(slot)))
                errors.Add((slot, $"{hotkey} is already used by another app."));
        }
        _mixer.SetHotkeyErrors(errors);
    }

    private void OnHotkey(HotkeySlot slot)
    {
        if (_mixer == null || _osd == null) return;
        if (slot.Channel is not { } channel)
        {
            OnProfileHotkey(slot.Action);
            return;
        }

        var action = slot.Action;
        string glyph = ChannelViewModel.GlyphFor(channel);
        try
        {
            switch (action)
            {
                case HotkeyAction.NextDevice:
                    _osd.ShowMessage(glyph, channel.ToString(), _mixer.CycleDevice(channel));
                    break;
                case HotkeyAction.VolumeUp:
                case HotkeyAction.VolumeDown:
                    float volume = _mixer.StepVolume(channel, action == HotkeyAction.VolumeUp ? 0.05f : -0.05f);
                    _osd.ShowMessage(glyph, $"{channel} volume", $"{Math.Round(volume * 100)}%");
                    break;
                case HotkeyAction.Mute:
                    bool muted = _mixer.ToggleMute(channel);
                    _osd.ShowMessage(muted ? "" : glyph, channel.ToString(), muted ? "Muted" : "Unmuted");
                    break;
            }
        }
        catch (Exception ex)
        {
            _osd.ShowMessage("", channel.ToString(), ex.Message);
        }
    }

    private void OnProfileHotkey(HotkeyAction action)
    {
        if (_mixer == null || _osd == null) return;
        try
        {
            Profile profile = action switch
            {
                HotkeyAction.HeadsetProfile => Profile.Headset,
                HotkeyAction.HomeTheaterProfile => Profile.HomeTheater,
                _ => _mixer.ActiveProfile() == Profile.Headset ? Profile.HomeTheater : Profile.Headset,
            };
            _mixer.ApplyProfile(profile);
            _osd.ShowMessage(profile == Profile.Headset ? "" : "", "Profile",
                             profile == Profile.Headset ? "Headset" : "Home Theater");
        }
        catch (Exception ex)
        {
            _osd.ShowMessage("", "Profile", ex.Message);
        }
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _mixer?.Refresh();
    }

    private void ExitApp()
    {
        _hotkeys?.Dispose();
        _keepAwake?.Dispose();
        _mixer?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _audio?.Dispose();
        _instanceMutex?.ReleaseMutex();
        Shutdown();
    }

    /// <summary>Headless check of device reading and per-app routing, written to a text file.</summary>
    private static void RunSelfTest(string outputPath)
    {
        var log = new StringBuilder();
        try
        {
            using var audio = new AudioService();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                log.AppendLine($"== {flow} devices ==");
                foreach (var d in audio.GetDevices(flow))
                    log.AppendLine($"  {d.Name} | {d.Format}{(d.IsDefault ? " | DEFAULT" : "")}{(d.IsCommunications ? " | COMMS" : "")}{(d.IsVirtual ? " | virtual" : "")}");
            }

            log.AppendLine("== Apps with audio ==");
            foreach (var a in audio.GetApps())
                log.AppendLine($"  {a.Name} ({a.ProcessName}) | pids {string.Join(",", a.ProcessIds)} | out={a.HasOutput} in={a.HasInput} playing={a.IsPlaying} | routed to {AppRouting.Get(a.ProcessIds[0], DataFlow.Render) ?? "default"}");

            log.AppendLine($"== Per-app routing supported: {AppRouting.IsSupported} ==");
            var target = audio.GetDevices(DataFlow.Render).FirstOrDefault(d => !d.IsDefault) ?? audio.GetDevices(DataFlow.Render).FirstOrDefault();
            if (AppRouting.IsSupported && target != null)
            {
                uint self = (uint)Environment.ProcessId;
                AppRouting.Set(self, DataFlow.Render, target.Id);
                string? readBack = AppRouting.Get(self, DataFlow.Render);
                log.AppendLine($"  set self -> {target.Name}: read back {(readBack == target.Id ? "OK" : $"MISMATCH ({readBack})")}");
                AppRouting.Set(self, DataFlow.Render, null);
                log.AppendLine($"  cleared: read back {AppRouting.Get(self, DataFlow.Render) ?? "default (OK)"}");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("ERROR: " + ex);
        }
        File.WriteAllText(outputPath, log.ToString());
    }
}
