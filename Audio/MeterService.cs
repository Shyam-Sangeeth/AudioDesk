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
    /// <summary>How streams map to channels; rebuilt by the mixer after every app scan.</summary>
    public sealed record ChannelMap(
        IReadOnlyDictionary<uint, Channel> ProcessChannels,
        string? ChatDeviceId,
        IReadOnlySet<uint> VoiceProcesses,
        string? MicDeviceId);

    private const int FrameMs = 33;
    private const float Falloff = 0.035f; // per frame, so peaks fade over ~1 s instead of flickering

    private readonly Thread _thread;
    private volatile ChannelMap? _map;
    private volatile bool _mapChanged = true;
    private volatile bool _enabled;
    private volatile bool _stopping;

    /// <summary>Raised on the meter thread with a level (0–1) for every channel.</summary>
    public event Action<IReadOnlyDictionary<Channel, float>>? LevelsChanged;

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

    public void SetMap(ChannelMap map)
    {
        _map = map;
        _mapChanged = true;
    }

    private void Run()
    {
        var sources = new Sources();
        var shown = Enum.GetValues<Channel>().ToDictionary(c => c, _ => 0f);
        long lastBuild = 0;

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
        private readonly List<MMDevice> _devices = [];
        private readonly List<(Channel Channel, AudioMeterInformation Meter)> _meters = [];

        public void Rebuild(ChannelMap? map)
        {
            Clear();
            if (map == null) return;

            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(device);
                bool isChatDevice = string.Equals(device.ID, map.ChatDeviceId, StringComparison.OrdinalIgnoreCase);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    uint pid = session.GetProcessID;
                    if (!map.ProcessChannels.TryGetValue(pid, out var channel)) continue;
                    // Discord-style voice on the Chat device counts as Chat (same rule as volume and ChatMix).
                    if (isChatDevice && map.VoiceProcesses.Contains(pid)) channel = Channel.Chat;
                    _meters.Add((channel, session.AudioMeterInformation));
                }
            }

            if (map.MicDeviceId != null)
            {
                var mic = enumerator.GetDevice(map.MicDeviceId);
                _devices.Add(mic);
                _meters.Add((Channel.Mic, mic.AudioMeterInformation));
            }
        }

        public Dictionary<Channel, float> ReadPeaks()
        {
            var peaks = new Dictionary<Channel, float>();
            foreach (var (channel, meter) in _meters)
                peaks[channel] = Math.Max(peaks.GetValueOrDefault(channel), meter.MasterPeakValue);
            return peaks;
        }

        public void Clear()
        {
            _meters.Clear();
            foreach (var device in _devices)
            {
                try { device.Dispose(); } catch { }
            }
            _devices.Clear();
        }
    }
}
