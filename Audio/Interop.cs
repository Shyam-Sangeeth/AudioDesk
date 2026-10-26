using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioDesk.Audio;

/// <summary>
/// Switches the Windows default playback/recording device (the same call the Sound
/// control panel makes). IPolicyConfig is undocumented but has been stable since Windows 7.
/// </summary>
internal static class PolicyConfig
{
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is called; the others keep the vtable layout.
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    public static void SetDefault(string deviceId, params Role[] roles)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            foreach (var role in roles)
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }
}

/// <summary>
/// Per-app device routing — the mechanism behind Settings > Sound > Volume mixer >
/// "Output/Input device" for each app. Windows remembers the choice per executable.
/// The app's audio goes straight to that endpoint: no virtual device, no extra mixing.
/// </summary>
internal static class AppRouting
{
    // Windows 10 21H2+ / Windows 11 interface id.
    private static readonly Guid FactoryIid = new("ab3d4648-e242-459f-b02f-541c70306324");
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    private const string MmDevApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterface = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        // IInspectable
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        // Unused members, declared only to keep the vtable layout.
        [PreserveSig] int AddCtxVolumeChange();
        [PreserveSig] int RemoveCtxVolumeChanged();
        [PreserveSig] int AddRingerVibrateStateChanged();
        [PreserveSig] int RemoveRingerVibrateStateChange();
        [PreserveSig] int SetVolumeGroupGainForId();
        [PreserveSig] int GetVolumeGroupGainForId();
        [PreserveSig] int GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int GetVolumeGroupsForEndpoint();
        [PreserveSig] int GetCurrentVolumeContext();
        [PreserveSig] int SetVolumeGroupMuteForId();
        [PreserveSig] int GetVolumeGroupMuteForId();
        [PreserveSig] int SetRingerVibrateState();
        [PreserveSig] int GetRingerVibrateState();
        [PreserveSig] int SetPreferredChatApplication();
        [PreserveSig] int ResetPreferredChatApplication();
        [PreserveSig] int GetPreferredChatApplication();
        [PreserveSig] int GetCurrentChatApplications();
        [PreserveSig] int AddChatContextChanged();
        [PreserveSig] int RemoveChatContextChanged();

        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    private static readonly Lazy<IAudioPolicyConfigFactory?> Factory = new(CreateFactory);

    public static bool IsSupported => Factory.Value != null;

    private static IAudioPolicyConfigFactory? CreateFactory()
    {
        IntPtr name = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out name));
            var iid = FactoryIid;
            if (RoGetActivationFactory(name, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
                return null;
            try { return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (name != IntPtr.Zero) WindowsDeleteString(name);
        }
    }

    /// <summary>Route a process to an endpoint, or back to the Windows default when <paramref name="deviceId"/> is null.</summary>
    public static void Set(uint processId, DataFlow flow, string? deviceId)
    {
        var factory = Factory.Value ?? throw new NotSupportedException("Per-app routing isn't available on this Windows build.");
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                string path = MmDevApiToken + deviceId + (flow == DataFlow.Render ? RenderInterface : CaptureInterface);
                Marshal.ThrowExceptionForHR(WindowsCreateString(path, path.Length, out hstring));
            }
            Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint(processId, flow, Role.Multimedia, hstring));
            Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint(processId, flow, Role.Console, hstring));
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    /// <summary>The endpoint id a process is routed to, or null when it follows the Windows default.</summary>
    public static string? Get(uint processId, DataFlow flow)
    {
        var factory = Factory.Value;
        if (factory == null) return null;
        if (factory.GetPersistedDefaultAudioEndpoint(processId, flow, Role.Multimedia, out var hstring) != 0 || hstring == IntPtr.Zero)
            return null;
        try
        {
            string path = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hstring, out _)) ?? "";
            if (path.StartsWith(MmDevApiToken, StringComparison.OrdinalIgnoreCase))
                path = path[MmDevApiToken.Length..];
            int suffix = path.LastIndexOf('#');
            if (suffix > 0) path = path[..suffix];
            return path.Length == 0 ? null : path;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
}
