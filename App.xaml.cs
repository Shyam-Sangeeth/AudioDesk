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
    private const string ExitEventName = "AudioDesk.Exit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private AudioService? _audio;
    private ChannelManager? _mixer;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _debounce;
    private KeepAwakeService? _keepAwake;
    private MeterService? _meters;
    private UpdateViewModel? _updates;
    private LocalApi? _api;
    private bool _exiting;

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

        // Used by the installer: --exit closes a running AudioDesk (before an upgrade or uninstall),
        // --add-startup / --remove-startup set the Start with Windows entry (installer checkbox, uninstall).
        if (e.Args.Contains("--exit") || e.Args.Contains("--add-startup") || e.Args.Contains("--remove-startup"))
        {
            if (e.Args.Contains("--add-startup") || e.Args.Contains("--remove-startup"))
            {
                try { StartupSetting.Enabled = e.Args.Contains("--add-startup"); } catch { }
            }
            if (e.Args.Contains("--exit")) CloseRunningInstance();
            Shutdown();
            return;
        }

        // Second launch: ask the running copy to show its window, then quit.
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            // The other copy may be on its way out (e.g. it just started an update that launched us), so give
            // it a moment to let go before deciding it's really still running.
            try { isFirst = _instanceMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { isFirst = true; }
            if (!isFirst)
            {
                Shutdown();
                return;
            }
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            exitEvent.WaitOne();
            Dispatcher.BeginInvoke(ExitApp);
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

        _meters = new MeterService();
        _updates = new UpdateViewModel(_mixer, ExitApp);
        _window = new MainWindow(_audio, _mixer, _meters, _updates);
        _window.Closing += (_, args) =>
        {
            // Closing the window keeps AudioDesk running (and routing apps) in the tray.
            if (_exiting) return;
            args.Cancel = true;
            _window.Hide();
        };

        // Quiet noise on devices that must not fall asleep (e.g. a soundbar on optical), but only while
        // a channel uses them; re-checked whenever channels, profiles or devices change.
        _keepAwake = new KeepAwakeService();
        _keepAwake.StatusChanged += (id, error) => Dispatcher.BeginInvoke(() => _mixer?.SetKeepAwakeError(id, error));
        _keepAwake.NoiseChanged += (id, playing) => Dispatcher.BeginInvoke(() => _mixer?.SetNoisePlaying(id, playing));
        _mixer.KeepAwakeChanged += ApplyKeepAwake;
        _mixer.StateChanged += ApplyKeepAwake;
        ApplyKeepAwake();

        _mixer.Start();
        // For Knob Hub's AudioDesk app (and anything else on this PC that wants the channels).
        _api = new LocalApi(_mixer, Dispatcher);
        _api.Start();
        CheckForUpdatesLater();
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

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _mixer?.Refresh();
    }

    /// <summary>
    /// If enabled, checks GitHub for a newer release once, a little after startup (so it doesn't slow down
    /// sign-in), and shows a tray notification that opens Settings → Updates when clicked.
    /// </summary>
    private async void CheckForUpdatesLater()
    {
        if (_updates == null || _mixer == null || !_mixer.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        _updates.UpdateFound += update =>
        {
            if (_window is { IsVisible: true }) return; // the Settings tab already shows it
            _tray?.ShowHint($"AudioDesk {update.Version} is available", "Click to open Settings and update.", () =>
            {
                ShowWindow();
                _window?.ShowSettings();
            });
        };
        await _updates.CheckAsync(quiet: true);
    }

    /// <summary>Asks a running AudioDesk to exit and waits (up to 10 s) until it has.</summary>
    private static void CloseRunningInstance()
    {
        try
        {
            EventWaitHandle.OpenExisting(ExitEventName).Set();
        }
        catch
        {
            return; // not running
        }

        try
        {
            using var mutex = Mutex.OpenExisting(InstanceName);
            if (mutex.WaitOne(TimeSpan.FromSeconds(10))) mutex.ReleaseMutex();
        }
        catch (AbandonedMutexException) { }
        catch (WaitHandleCannotBeOpenedException) { } // already gone
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _api?.Dispose();
        _keepAwake?.Dispose();
        _meters?.Dispose();
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
