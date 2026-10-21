using Microsoft.Win32;

namespace AudioDesk.Audio;

/// <summary>What Windows audio enhancements (APOs) do to a playback device's streams.</summary>
public static class AudioEffects
{
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";

    // PKEY_AudioEndpoint_Disable_SysFx: 1 when "Audio enhancements" is off for the device.
    private const string DisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    // Effects that process each stream before the mix: PKEY_FX_PreMixEffectClsid (older drivers, e.g. Dolby DS1),
    // PKEY_FX_StreamEffectClsid, PKEY_FX_ModeEffectClsid and their PKEY_CompositeFX_ lists.
    private static readonly string[] StreamEffects =
        [.. new[] { 1, 5, 6, 13, 14 }.Select(id => "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}," + id)];

    private static readonly Dictionary<string, (bool MayUpmix, long Checked)> Cache = [];

    /// <summary>
    /// True if an enhancement may remix each app's audio before Windows meters it, e.g. a Dolby or Realtek
    /// upmixer spreading stereo over every speaker. Then the per-app meters can't tell stereo from surround.
    /// </summary>
    public static bool MayUpmix(string deviceId)
    {
        lock (Cache)
        {
            long now = Environment.TickCount64;
            if (Cache.TryGetValue(deviceId, out var cached) && now - cached.Checked < 10_000) return cached.MayUpmix;
            bool result = Read(deviceId);
            Cache[deviceId] = (result, now);
            return result;
        }
    }

    private static bool Read(string deviceId)
    {
        try
        {
            // Device IDs look like "{0.0.0.00000000}.{guid}"; the guid names the device's registry key.
            string guid = deviceId[(deviceId.LastIndexOf('.') + 1)..];
            using var device = Registry.LocalMachine.OpenSubKey(RenderKey + guid);
            if (device == null) return true;
            using var properties = device.OpenSubKey("Properties");
            using var fx = device.OpenSubKey("FxProperties");
            if (IsOne(properties?.GetValue(DisableSysFx)) || IsOne(fx?.GetValue(DisableSysFx))) return false;
            return fx != null && StreamEffects.Any(name => HasEffect(fx.GetValue(name)));
        }
        catch
        {
            return true; // can't tell, so don't claim anything
        }
    }

    private static bool IsOne(object? value) => value is int i && i == 1;

    private static bool HasEffect(object? value) => value switch
    {
        string clsid => Guid.TryParse(clsid, out var g) && g != Guid.Empty,
        string[] list => list.Any(clsid => Guid.TryParse(clsid, out var g) && g != Guid.Empty),
        _ => false,
    };
}
