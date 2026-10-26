using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioDesk.Audio;

public sealed record DeviceInfo(
    string Id,
    string Name,
    DataFlow Flow,
    string Format,
    bool IsDefault,
    bool IsVirtual);

/// <summary>An app (all processes of one executable) that has playback sessions.</summary>
public sealed record AudioApp(
    string ProcessName,
    string Name,
    string? ExePath,
    IReadOnlyList<uint> ProcessIds,
    bool IsPlaying,
    IReadOnlySet<string> OutputDeviceIds); // devices the app is actively playing on right now

/// <summary>Reads devices and app audio sessions, and reports device changes.</summary>
public sealed class AudioService : IDisposable
{
    // PKEY_AudioEngine_DeviceFormat: the format the audio engine actually sends to the hardware.
    private static readonly PropertyKey DeviceFormatKey = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeDolbyDigital = new("00000092-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeDts = new("00000008-0000-0010-8000-00aa00389b71");

    private static readonly string[] VirtualMarkers = ["Sonar", "Virtual", "CABLE", "VB-Audio", "Voicemeeter", "Nahimic", "NVIDIA Broadcast"];

    // Processes that hold audio sessions but are never worth routing.
    private static readonly HashSet<string> HiddenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "LockApp", "SystemSettingsBroker", "WidgetBoard", "Widgets",
        // Broadcast and Sonar play their processed audio on a device they pick themselves; routing them could loop it
        // into their own input, and a channel volume on them would change everything that goes through them.
        "NVIDIA Broadcast", "SteelSeriesSonar",
    };

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notifications;

    // Reading devices opens each one to get its format, which can stall for a second while a device
    // restarts (e.g. after a default-device switch). The window reads from this cache instead, and the
    // cache refreshes on a background thread whenever Windows reports a change.
    private readonly object _cacheLock = new();
    private readonly Dictionary<DataFlow, IReadOnlyList<DeviceInfo>> _cache = [];
    private bool _cacheDirty = true;
    private int _rebuilding;

    /// <summary>Raised (on any thread) when Windows reports a device change or the device cache has new data.</summary>
    public event Action? DevicesChanged;

    public AudioService()
    {
        _notifications = new NotificationClient(() =>
        {
            InvalidateDevices();
            DevicesChanged?.Invoke();
        });
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    /// <summary>Active devices from the cache. Returns at once; stale data is refreshed in the background.</summary>
    public IReadOnlyList<DeviceInfo> GetDevices(DataFlow flow)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(flow, out var cached))
            {
                if (_cacheDirty) StartRebuild();
                return cached;
            }
        }

        // Nothing cached yet (first call at startup): read now.
        var fresh = ReadDevices(flow);
        lock (_cacheLock) _cache[flow] = fresh;
        return fresh;
    }

    /// <summary>Marks the cache stale (after a device switch or a Windows notification) and starts a refresh.</summary>
    public void InvalidateDevices()
    {
        lock (_cacheLock)
        {
            _cacheDirty = true;
            StartRebuild();
        }
    }

    private void StartRebuild()
    {
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return;
        Task.Run(() =>
        {
            bool changed = false;
            try
            {
                lock (_cacheLock) _cacheDirty = false;
                var render = ReadDevices(DataFlow.Render);
                lock (_cacheLock)
                {
                    changed = !Same(DataFlow.Render, render);
                    _cache[DataFlow.Render] = render;
                }
            }
            catch
            {
                // Keep the old data; the next change notification retries.
            }
            finally
            {
                Interlocked.Exchange(ref _rebuilding, 0);
            }

            if (changed) DevicesChanged?.Invoke();
            bool again;
            lock (_cacheLock) again = _cacheDirty;
            if (again) InvalidateDevices();
        });
    }

    /// <summary>Reads the playback devices on a worker thread and updates the cache; completes when the cache is current.</summary>
    public async Task RefreshDevicesAsync()
    {
        var render = await Task.Run(() => ReadDevices(DataFlow.Render));
        bool changed;
        lock (_cacheLock)
        {
            changed = !Same(DataFlow.Render, render);
            _cache[DataFlow.Render] = render;
        }
        if (changed) DevicesChanged?.Invoke();
    }

    private bool Same(DataFlow flow, IReadOnlyList<DeviceInfo> devices) =>
        _cache.TryGetValue(flow, out var old) && old.SequenceEqual(devices);

    private static IReadOnlyList<DeviceInfo> ReadDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = ReadDefaultId(enumerator, flow, Role.Multimedia);
        var devices = new List<DeviceInfo>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                string name;
                try { name = device.FriendlyName; } catch { continue; }
                devices.Add(new DeviceInfo(
                    device.ID,
                    name,
                    flow,
                    DescribeFormat(device),
                    device.ID == defaultId,
                    VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))));
            }
        }
        return devices.OrderBy(d => d.IsVirtual).ThenBy(d => d.Name).ToList();
    }

    private static string? ReadDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The Windows default playback device, from the cache.</summary>
    public string? DefaultOutputId() => GetDevices(DataFlow.Render).FirstOrDefault(d => d.IsDefault)?.Id;

    /// <summary>Apps with a playback session on any active device, grouped by executable.</summary>
    public List<AudioApp> GetApps()
    {
        var sessions = new Dictionary<uint, bool>();  // pid → playing
        var outputDevices = new Dictionary<uint, HashSet<string>>();

        ForEachSession(DataFlow.Render, (deviceId, pid, session) =>
        {
            bool active = session.State == AudioSessionState.AudioSessionStateActive;
            sessions[pid] = sessions.GetValueOrDefault(pid) || active;
            // Only devices the app is playing on right now: Windows keeps an app's old, idle sessions on devices
            // it used before (e.g. before it was moved to another channel), which must not count.
            if (!active) return;
            if (!outputDevices.TryGetValue(pid, out var set)) outputDevices[pid] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(deviceId);
        });
        // Apps like browsers and Discord use several processes; show them as one app.
        var groups = new Dictionary<string, (string ProcessName, string? Path, List<uint> Pids, bool Playing, HashSet<string> Devices)>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;

        foreach (var (pid, playing) in sessions)
        {
            if (pid == self) continue;
            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                continue; // exited
            }
            if (HiddenProcesses.Contains(processName)) continue;

            var devices = outputDevices.GetValueOrDefault(pid);
            if (groups.TryGetValue(processName, out var g))
            {
                g.Pids.Add(pid);
                if (devices != null) g.Devices.UnionWith(devices);
                groups[processName] = g with { Playing = g.Playing || playing };
            }
            else
            {
                groups[processName] = (processName, GetProcessPath(pid), [pid], playing,
                                       devices != null ? new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        return groups.Values
            .Select(g => new AudioApp(g.ProcessName, DisplayName(g.Path, g.ProcessName), g.Path, g.Pids, g.Playing, g.Devices))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sets the per-app volume (the one in the Windows volume mixer) of output sessions. <paramref name="level"/>
    /// gets each session's device id and process id and returns the volume to apply, or null to leave it alone.
    /// </summary>
    public void SetSessionVolumes(Func<string, uint, (float Volume, bool Muted)?> level)
    {
        ForEachSession(DataFlow.Render, (deviceId, pid, session) =>
        {
            if (level(deviceId, pid) is not { } target) return;
            session.SimpleAudioVolume.Volume = Math.Clamp(target.Volume, 0f, 1f);
            session.SimpleAudioVolume.Mute = target.Muted;
        });
    }

    /// <summary>Visits every audio session on the active devices. Safe on any thread (uses its own enumerator).</summary>
    private static void ForEachSession(DataFlow flow, Action<string, uint, AudioSessionControl> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    string deviceId = device.ID;
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var list = manager.Sessions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var session = list[i];
                        if (session.IsSystemSoundsSession) continue;
                        uint pid = session.GetProcessID;
                        if (pid != 0) action(deviceId, pid, session);
                    }
                }
                catch
                {
                    // Some drivers refuse session enumeration; skip that device.
                }
            }
        }
    }

    private static string DisplayName(string? path, string processName)
    {
        if (path != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch { }
        }
        return processName;
    }

    private static string DescribeFormat(MMDevice device)
    {
        int channels = 0, rate = 0, bits = 0;
        string? encoding = null;

        try
        {
            var mix = device.AudioClient.MixFormat;
            channels = mix.Channels;
            rate = mix.SampleRate;
        }
        catch { }

        try
        {
            if (device.Properties.Contains(DeviceFormatKey) && device.Properties[DeviceFormatKey].Value is byte[] wfx && wfx.Length >= 18)
            {
                ushort tag = BitConverter.ToUInt16(wfx, 0);
                rate = BitConverter.ToInt32(wfx, 4);
                bits = BitConverter.ToUInt16(wfx, 14);
                if (channels == 0) channels = BitConverter.ToUInt16(wfx, 2);

                if (tag == 0xFFFE && wfx.Length >= 40)
                {
                    ushort validBits = BitConverter.ToUInt16(wfx, 18);
                    if (validBits > 0) bits = validBits;
                    var subtype = new Guid(wfx.AsSpan(24, 16));
                    if (subtype == SubtypeDolbyDigital) encoding = "Dolby Digital";
                    else if (subtype == SubtypeDts) encoding = "DTS";
                    else if (subtype == SubtypeFloat) encoding = $"{bits}-bit float";
                    else if (subtype == SubtypePcm) encoding = $"{bits}-bit";
                }
                else if (tag == 0x0092) encoding = "Dolby Digital";
                else if (tag == 0x0003) encoding = $"{bits}-bit float";
                else if (tag == 0x0001) encoding = $"{bits}-bit";
            }
        }
        catch { }

        var parts = new List<string>();
        if (channels > 0)
        {
            parts.Add(channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                6 => "5.1",
                8 => "7.1",
                _ => $"{channels} ch",
            });
        }
        if (rate > 0) parts.Add($"{rate / 1000.0:0.#} kHz");
        if (encoding != null) parts.Add(encoding);
        else if (bits > 0) parts.Add($"{bits}-bit");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Format unknown";
    }

    private static string? GetProcessPath(uint pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        _enumerator.Dispose();
    }

    private sealed class NotificationClient(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => changed();
    }
}
