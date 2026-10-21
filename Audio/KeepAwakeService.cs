using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AudioDesk.Audio;

/// <summary>
/// Keeps chosen playback devices awake by playing very quiet white noise to them whenever nothing else is
/// playing. Receivers and soundbars (especially over optical/HDMI) go to sleep when the signal stops and need
/// a moment to wake up, which cuts off the start of the next sound. While another app plays on the device
/// its audio keeps the receiver awake, so the noise pauses and resumes as soon as the device goes quiet.
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _monitor;
    private bool _disposed;

    public KeepAwakeService()
    {
        // Twice a second: pause the noise while other audio plays, resume it when the device goes quiet.
        _monitor = new Timer(_ => PauseWhileOthersPlay(), null, 500, 500);
    }

    /// <summary>Raised on a worker thread: device id and an error message, or null once it plays fine.</summary>
    public event Action<string, string?>? StatusChanged;

    /// <summary>Raised on a worker thread when the noise on a device starts (true) or stops/pauses (false).</summary>
    public event Action<string, bool>? NoiseChanged;

    private void PauseWhileOthersPlay()
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var (id, player) in _players)
            {
                bool othersPlaying;
                try { othersPlaying = OthersPlaying(id); }
                catch { continue; } // device restarting; check again next time

                if (othersPlaying && !player.IsPaused)
                {
                    player.Pause();
                    NoiseChanged?.Invoke(id, false);
                }
                else if (!othersPlaying && player.IsPaused)
                {
                    player.Resume();
                    NoiseChanged?.Invoke(id, true);
                }
            }
        }
    }

    /// <summary>True when any app other than AudioDesk has an active stream on the device.</summary>
    private static bool OthersPlaying(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        var sessions = device.AudioSessionManager.Sessions;
        uint self = (uint)Environment.ProcessId;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.GetProcessID != self && session.State == AudioSessionState.AudioSessionStateActive)
                return true;
        }
        return false;
    }

    /// <summary>Plays noise on exactly these devices (device id → level in dBFS).</summary>
    public void Apply(IReadOnlyDictionary<string, double> devices)
    {
        lock (_lock) _wanted = new Dictionary<string, double>(devices, StringComparer.OrdinalIgnoreCase);
        Task.Run(Reconcile);
    }

    /// <summary>Call when devices change: starts streams on devices that came back, drops dead ones.</summary>
    public void DevicesChanged() => Task.Run(Reconcile);

    private void Reconcile()
    {
        lock (_lock)
        {
            if (_disposed) return;

            foreach (var id in _players.Keys.ToList())
            {
                if (!_wanted.ContainsKey(id) || !_players[id].IsPlaying)
                {
                    _players[id].Dispose();
                    _players.Remove(id);
                    NoiseChanged?.Invoke(id, false);
                }
            }

            foreach (var (id, db) in _wanted)
            {
                float amplitude = (float)Math.Pow(10, db / 20.0);
                if (_players.TryGetValue(id, out var player))
                {
                    player.Amplitude = amplitude;
                    continue;
                }
                try
                {
                    var started = Player.Start(id, amplitude, OnStopped);
                    if (started != null)
                    {
                        _players[id] = started;
                        StatusChanged?.Invoke(id, null);
                        // Don't add noise on top of audio that's already playing; the monitor resumes it later.
                        bool othersPlaying;
                        try { othersPlaying = OthersPlaying(id); } catch { othersPlaying = false; }
                        if (othersPlaying) started.Pause();
                        NoiseChanged?.Invoke(id, !othersPlaying);
                    }
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(id, ex is COMException { HResult: unchecked((int)0x8889000A) }
                        ? "Another app is using this device exclusively."
                        : ex.Message);
                }
            }
        }
    }

    // A stream stops when its device restarts (default switch, format change, unplug); try again shortly.
    private void OnStopped() => Task.Delay(1500).ContinueWith(_ => Reconcile());

    public void Dispose()
    {
        _monitor.Dispose();
        lock (_lock)
        {
            _disposed = true;
            foreach (var player in _players.Values) player.Dispose();
            _players.Clear();
        }
    }

    private sealed class Player : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly NoiseProvider _noise;
        private volatile bool _stopped;
        private bool _disposing;

        private Player(MMDevice device, WasapiOut output, NoiseProvider noise)
        {
            _device = device;
            _output = output;
            _noise = noise;
        }

        /// <summary>The stream is alive (paused counts as alive); false once the device stopped it.</summary>
        public bool IsPlaying => !_stopped;

        /// <summary>Paused because another app is playing on the device.</summary>
        public bool IsPaused { get; private set; }

        public float Amplitude
        {
            set => _noise.Amplitude = value;
        }

        public void Pause()
        {
            _output.Pause();
            IsPaused = true;
        }

        public void Resume()
        {
            _output.Play();
            IsPaused = false;
        }

        /// <summary>Starts the noise stream, or returns null when the device isn't connected right now.</summary>
        public static Player? Start(string deviceId, float amplitude, Action onStopped)
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                return null;
            }

            WasapiOut? output = null;
            try
            {
                // Use the device's own shared-mode format (always 32-bit float), so Windows plays it without conversion.
                var noise = new NoiseProvider(device.AudioClient.MixFormat, amplitude);
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                var player = new Player(device, output, noise);
                output.PlaybackStopped += (_, _) =>
                {
                    player._stopped = true;
                    if (!player._disposing) onStopped();
                };
                output.Init(noise);
                output.Play();
                return player;
            }
            catch
            {
                output?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            try { _output.Stop(); } catch { }
            _output.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>Endless white noise in the device's mix format (32-bit float, any channel count).</summary>
    private sealed class NoiseProvider(WaveFormat format, float amplitude) : IWaveProvider
    {
        private uint _state = 0x9E3779B9;

        public WaveFormat WaveFormat => format;

        public volatile float Amplitude = amplitude;

        public int Read(byte[] buffer, int offset, int count)
        {
            count -= count % 4;
            var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            float amplitude = Amplitude;
            for (int i = 0; i < samples.Length; i++)
            {
                // xorshift32: fast, good enough for noise.
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                samples[i] = ((_state / (float)uint.MaxValue) * 2f - 1f) * amplitude;
            }
            return count;
        }
    }
}
