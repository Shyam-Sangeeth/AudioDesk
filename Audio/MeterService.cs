using AudioDesk.Mixer;
using NAudio.CoreAudioApi;

namespace AudioDesk.Audio;

/// <summary>
/// Live level meters per channel. Reads the peak level Windows already measures for every audio stream
/// (IAudioMeterInformation), sums them up per channel and reports ~30 times a second. Runs on its own
/// thread and only while <see cref="Enabled"/> (the window is visible).
/// </summary>
public sealed class MeterService : IDisposable
{
    private const int FrameMs = 33;
    private const float Falloff = 0.035f; // per frame, so peaks fade over ~1 s instead of flickering

    private readonly Thread _thread;

    // Which channel each process's streams belong to; rebuilt by the mixer after every app scan.
    private volatile IReadOnlyDictionary<uint, Channel>? _map;
    private volatile bool _mapChanged = true;
    private volatile bool _enabled;
    private volatile bool _stopping;

    /// <summary>Raised on the meter thread with a level (0–1) for every channel.</summary>
    public event Action<IReadOnlyDictionary<Channel, float>>? LevelsChanged;

    /// <summary>
    /// Raised on the meter thread when the speakers an app feeds change: "Stereo", "5.1", "7.1" or "Mono" per
    /// channel and process. Only for multichannel devices where Windows' per-app meters can tell (see
    /// <see cref="AudioEffects.MayUpmix"/>); other apps aren't listed.
    /// </summary>
    public event Action<IReadOnlyDictionary<(Channel Channel, uint ProcessId), string>>? LayoutsChanged;

    public MeterService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioDesk meters" };
        _thread.Start();
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void SetMap(IReadOnlyDictionary<uint, Channel> map)
    {
        _map = map;
        _mapChanged = true;
    }

    private void Run()
    {
        var sources = new Sources();
        var shown = Enum.GetValues<Channel>().ToDictionary(c => c, _ => 0f);
        IReadOnlyDictionary<(Channel, uint), string> layouts = new Dictionary<(Channel, uint), string>();
        long lastBuild = 0;
        int frame = 0;

        while (!_stopping)
        {
            if (!_enabled)
            {
                if (shown.Values.Any(v => v > 0))
                {
                    foreach (var c in shown.Keys.ToList()) shown[c] = 0;
                    LevelsChanged?.Invoke(new Dictionary<Channel, float>(shown));
                }
                Thread.Sleep(200);
                continue;
            }

            try
            {
                // New streams appear all the time; refresh the list now and then, and right after a re-scan.
                if (_mapChanged || Environment.TickCount64 - lastBuild > 1000)
                {
                    _mapChanged = false;
                    lastBuild = Environment.TickCount64;
                    sources.Rebuild(_map);
                }

                var peaks = sources.ReadPeaks();
                foreach (var channel in shown.Keys.ToList())
                {
                    float peak = peaks.GetValueOrDefault(channel);
                    shown[channel] = Math.Max(peak, shown[channel] - Falloff);
                }
                LevelsChanged?.Invoke(new Dictionary<Channel, float>(shown));

                // Speaker layouts change slowly; ~10 reads a second are plenty.
                if (++frame % 3 == 0)
                {
                    var latest = sources.ReadLayouts();
                    if (latest.Count != layouts.Count || latest.Any(p => layouts.GetValueOrDefault(p.Key) != p.Value))
                    {
                        layouts = latest;
                        LayoutsChanged?.Invoke(latest);
                    }
                }
            }
            catch
            {
                sources.Clear(); // a device went away mid-read; rebuild on the next frame
                lastBuild = 0;
            }

            Thread.Sleep(FrameMs);
        }

        sources.Clear();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(500);
    }

    /// <summary>The meters currently being read, all owned by the meter thread.</summary>
    private sealed class Sources
    {
        // A speaker counts as fed if it had sound in the last few seconds, so quiet moments don't flip the tag.
        private const float Heard = 0.0005f; // about -66 dB; speakers an app doesn't feed read exactly 0
        private const long HoldMs = 5000;

        private readonly List<MMDevice> _devices = [];
        private readonly List<(Channel Channel, AudioMeterInformation Meter)> _meters = [];
        private readonly List<(Channel Channel, uint Pid, int Speakers, AudioMeterInformation Meter)> _surround = [];
        private readonly Dictionary<(Channel, uint), long[]> _lastHeard = [];

        public void Rebuild(IReadOnlyDictionary<uint, Channel>? map)
        {
            Clear();
            if (map == null) return;

            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(device);
                // Per-app meters see each stream in the device's own speaker layout, after any per-stream effect.
                // An effect like the Dolby upmixer spreads stereo over all speakers, so skip those devices.
                int speakers = device.AudioMeterInformation.PeakValues.Count;
                bool readLayouts = speakers > 2 && !AudioEffects.MayUpmix(device.ID);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    uint pid = session.GetProcessID;
                    if (!map.TryGetValue(pid, out var channel)) continue;
                    _meters.Add((channel, session.AudioMeterInformation));
                    if (readLayouts) _surround.Add((channel, pid, speakers, session.AudioMeterInformation));
                }
            }

            // Forget apps that are gone.
            foreach (var key in _lastHeard.Keys.Where(k => !_surround.Any(s => (s.Channel, s.Pid) == k)).ToList())
                _lastHeard.Remove(key);
        }

        public Dictionary<Channel, float> ReadPeaks()
        {
            var peaks = new Dictionary<Channel, float>();
            foreach (var (channel, meter) in _meters)
                peaks[channel] = Math.Max(peaks.GetValueOrDefault(channel), meter.MasterPeakValue);
            return peaks;
        }

        public Dictionary<(Channel, uint), string> ReadLayouts()
        {
            long now = Environment.TickCount64;
            foreach (var (channel, pid, speakers, meter) in _surround)
            {
                if (!_lastHeard.TryGetValue((channel, pid), out var heard))
                    _lastHeard[(channel, pid)] = heard = new long[speakers];
                try
                {
                    // A stream that was closed reports no channels until the next rebuild drops it.
                    var peaks = meter.PeakValues;
                    for (int i = 0; i < Math.Min(peaks.Count, heard.Length); i++)
                        if (peaks[i] > Heard) heard[i] = now;
                }
                catch { }
            }

            var layouts = new Dictionary<(Channel, uint), string>();
            foreach (var (key, heard) in _lastHeard)
            {
                string layout = Describe(heard, now);
                if (layout.Length > 0) layouts[key] = layout;
            }
            return layouts;
        }

        /// <summary>Names the speakers fed recently, in Windows' order: FL, FR, FC, LFE, back L/R, side L/R.</summary>
        private static string Describe(long[] heard, long now)
        {
            bool Fed(int speaker) => speaker < heard.Length && now - heard[speaker] < HoldMs;
            var fed = Enumerable.Range(0, heard.Length).Where(Fed).ToList();
            if (fed.Count == 0) return "";
            if (fed is [2]) return "Mono"; // a mono stream goes to the centre speaker only
            if (fed.All(s => s < 2)) return "Stereo";
            if (heard.Length >= 8 && (Fed(6) || Fed(7))) return "7.1";
            return heard.Length switch
            {
                4 => "4.0",
                >= 6 => "5.1",
                _ => $"{heard.Length} ch",
            };
        }

        public void Clear()
        {
            _meters.Clear();
            _surround.Clear();
            foreach (var device in _devices)
            {
                try { device.Dispose(); } catch { }
            }
            _devices.Clear();
        }
    }
}
