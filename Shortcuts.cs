using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using AudioDesk.Mixer;

namespace AudioDesk;

/// <summary>A key with modifiers, saved as text such as "Ctrl+Alt+Up" (WPF <see cref="Key"/> names).</summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Modifier, string Name)[] ModifierNames =
        [(ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win")];

    // Windows' own: taking these would break switching and closing windows everywhere.
    private static readonly Shortcut[] Reserved =
    [
        new(ModifierKeys.Alt, Key.F4), new(ModifierKeys.Alt, Key.Tab), new(ModifierKeys.Alt | ModifierKeys.Shift, Key.Tab),
        new(ModifierKeys.Alt, Key.Escape), new(ModifierKeys.Control, Key.Escape),
    ];

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>Why these keys can't be a shortcut, or null. Ctrl, Alt or Win is needed (Shift alone would take a
    /// key from typing), except for F13 to F24, which keyboards with extra keys send.</summary>
    public string? Problem()
    {
        if (IsModifierKey(Key) || Key == Key.None) return "Press a key with the modifiers.";
        if (Reserved.Contains(this)) return $"{this} belongs to Windows.";
        if (Key is >= Key.F13 and <= Key.F24) return null;
        return (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0
            ? "Use Ctrl, Alt or Win with the key."
            : null;
    }

    public override string ToString() => string.Join("+", Parts(Modifiers).Append(Key.ToString()));

    /// <summary>For people: "Ctrl+Alt+1" rather than "Ctrl+Alt+D1".</summary>
    public string Display => string.Join("+", Parts(Modifiers).Append(KeyName(Key)));

    /// <summary>The modifiers held so far, while a shortcut is being typed.</summary>
    public static string DisplayModifiers(ModifierKeys modifiers) => string.Join("+", Parts(modifiers).Append(""));

    private static IEnumerable<string> Parts(ModifierKeys modifiers) =>
        ModifierNames.Where(m => modifiers.HasFlag(m.Modifier)).Select(m => m.Name);

    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        var parts = (text ?? "").Split('+');
        if (parts.Length < 1 || !Enum.TryParse<Key>(parts[^1], out var key)) return false;
        var modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            var match = ModifierNames.FirstOrDefault(m => m.Name == part);
            if (match.Name == null) return false;
            modifiers |= match.Modifier;
        }
        shortcut = new Shortcut(modifiers, key);
        return true;
    }

    private static string KeyName(Key key)
    {
        switch (key)
        {
            case >= Key.D0 and <= Key.D9: return ((int)(key - Key.D0)).ToString();
            case >= Key.NumPad0 and <= Key.NumPad9: return $"Num {(int)(key - Key.NumPad0)}";
            case Key.Prior: return "Page Up";
            case Key.Next: return "Page Down";
            case Key.Return: return "Enter";
            case Key.Back: return "Backspace";
            case Key.Escape: return "Esc";
            case Key.Multiply: return "Num *";
            case Key.Add: return "Num +";
            case Key.Subtract: return "Num -";
            case Key.Divide: return "Num /";
            case Key.Decimal: return "Num .";
        }
        // Punctuation keys: the character this keyboard layout prints.
        uint ch = MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), 2) & 0x7FFF;
        return key.ToString().StartsWith("Oem", StringComparison.Ordinal) && ch > ' ' ? ((char)ch).ToString() : key.ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}

/// <summary>Something a shortcut can do. <see cref="Repeats"/>: holding the keys repeats it (volume).</summary>
public sealed record ShortcutAction(string Id, string Group, string Label, string Description, bool Repeats = false)
{
    /// <summary>For messages: "Headset", "Game Volume up".</summary>
    public string FullName => Group == "Profiles" ? Label : $"{Group} {Label}";

    public static readonly IReadOnlyList<ShortcutAction> All =
    [
        new("sonar", "Profiles", "Sonar", "Sends Game, Media and Aux through Sonar, or takes them all off Sonar again."),
        new("headset", "Profiles", "Headset", "Game, Media and Aux on the Headset profile's device."),
        new("home_theater", "Profiles", "Home Theater", "Game, Media and Aux on the Home Theater profile's device."),
        .. ChannelManager.OutputChannels.SelectMany(ChannelActions),
    ];

    private static IEnumerable<ShortcutAction> ChannelActions(Channel channel)
    {
        string id = channel.ToString().ToLowerInvariant();
        string name = channel.ToString();
        yield return new($"{id}.volume_up", name, "Volume up", $"Raises {name}'s volume by the volume step.", Repeats: true);
        yield return new($"{id}.volume_down", name, "Volume down", $"Lowers {name}'s volume by the volume step.", Repeats: true);
        yield return new($"{id}.mute", name, "Mute or unmute", $"Mutes {name}, or unmutes it.");
        yield return new($"{id}.next_device", name, "Next device",
                         $"Moves {name} to the next playback device. While {name} goes through Sonar, changes the device Sonar plays it on.");
        yield return new($"{id}.sonar", name, "Through Sonar on or off", $"Sends {name} through Sonar, or takes it off Sonar.");
    }
}

/// <summary>
/// Global keyboard shortcuts: registered with Windows (RegisterHotKey) on a hidden message window, so they work in any
/// app and while AudioDesk is in the tray. Each one runs the same <see cref="ChannelManager"/> call as the window's
/// buttons do.
/// </summary>
public sealed class ShortcutService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly ChannelManager _mixer;
    private readonly Action<string, string, bool> _notify;
    private readonly HwndSource _window;
    private readonly Dictionary<int, ShortcutAction> _registered = [];
    private readonly Dictionary<string, string> _errors = [];
    // The device a Next device shortcut picked, while that switch is still running, so pressing again moves on from it.
    private readonly Dictionary<Channel, string> _picked = [];
    private bool _paused;

    /// <summary>Raised when which shortcuts work changes (one couldn't be registered, or now can).</summary>
    public event Action? Changed;

    /// <param name="notify">Shows what a shortcut did: title, text, and whether it's an error.</param>
    public ShortcutService(ChannelManager mixer, Action<string, string, bool> notify)
    {
        _mixer = mixer;
        _notify = notify;
        // A message-only window: never shown, only receives WM_HOTKEY.
        _window = new HwndSource(new HwndSourceParameters("AudioDesk shortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
        _mixer.ShortcutsChanged += Register;
        Register();
    }

    /// <summary>Paused while a shortcut is being typed in the Shortcuts tab, so the keys reach the window.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (value == _paused) return;
            _paused = value;
            Register();
        }
    }

    /// <summary>Why the action's shortcut doesn't work (another app has it), or null.</summary>
    public string? Error(string action) => _errors.GetValueOrDefault(action);

    private void Register()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        var errors = new Dictionary<string, string>();

        if (!_paused)
        {
            int next = 1;
            foreach (var action in ShortcutAction.All)
            {
                if (!_mixer.Shortcuts.TryGetValue(action.Id, out string? text)) continue;
                if (!Shortcut.TryParse(text, out var shortcut) || shortcut.Problem() != null)
                {
                    errors[action.Id] = "These keys can't be a shortcut.";
                    continue;
                }
                uint modifiers = (shortcut.Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) |
                                 (shortcut.Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0) |
                                 (shortcut.Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) |
                                 (shortcut.Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0) |
                                 (action.Repeats ? 0 : MOD_NOREPEAT);
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key);
                if (RegisterHotKey(_window.Handle, next, modifiers, vk)) _registered[next++] = action;
                else errors[action.Id] = "Windows or another app already uses these keys.";
            }
        }

        bool changed = errors.Count != _errors.Count || errors.Any(e => _errors.GetValueOrDefault(e.Key) != e.Value);
        _errors.Clear();
        foreach (var (id, error) in errors) _errors[id] = error;
        if (changed) Changed?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Run(action);
        }
        return IntPtr.Zero;
    }

    private void Run(ShortcutAction action)
    {
        try
        {
            string[] parts = action.Id.Split('.');
            if (parts.Length == 1) RunProfile(action.Id);
            else RunChannel(Enum.Parse<Channel>(parts[0], ignoreCase: true), parts[1]);
        }
        // A shortcut must never take AudioDesk down: whatever goes wrong is shown instead.
        catch (Exception ex)
        {
            _notify($"Couldn't do {action.FullName}", ex.Message, true);
        }
    }

    private void RunProfile(string id)
    {
        bool sonarMode = _mixer.IsSonarMode;
        var profile = id switch { "sonar" => Profile.Sonar, "headset" => Profile.Headset, _ => Profile.HomeTheater };
        _mixer.ApplyProfile(profile);
        string where = profile == Profile.Headset ? "the headset" : "the home theater";
        if (profile == Profile.Sonar)
            _notify(sonarMode ? "Sonar off" : "Sonar on", sonarMode ? "Game, Media and Aux play straight on their devices."
                                                                    : "Game, Media and Aux go through SteelSeries Sonar.", false);
        else
            _notify(profile == Profile.Headset ? "Headset" : "Home Theater",
                    sonarMode ? $"Sonar now plays Game, Media and Aux on {where}." : $"Game, Media and Aux play straight on {where}.", false);
    }

    private void RunChannel(Channel channel, string verb)
    {
        var (volume, muted) = _mixer.GetLevel(channel);
        float step = _mixer.ShortcutVolumeStep / 100f;
        switch (verb)
        {
            // Volume moves to the next whole step (so 37% goes to 40% or 35% with a 5% step). Raising it unmutes.
            case "volume_up":
                _mixer.SetLevel(channel, Math.Min(1f, (MathF.Floor(volume / step + 0.01f) + 1) * step), false);
                break;
            case "volume_down":
                _mixer.SetLevel(channel, Math.Max(0f, (MathF.Ceiling(volume / step - 0.01f) - 1) * step), muted);
                break;
            case "mute":
                _mixer.SetLevel(channel, volume, !muted);
                break;
            case "next_device":
                NextDevice(channel);
                break;
            case "sonar":
                if (_mixer.SonarDeviceId(channel) == null) throw new InvalidOperationException("SteelSeries Sonar isn't installed.");
                bool on = !_mixer.IsThroughSonar(channel);
                _mixer.SetThroughSonar(channel, on);
                _notify(channel.ToString(), on ? $"{channel} goes through SteelSeries Sonar." : $"{channel} plays straight on its device.", false);
                break;
        }
    }

    private void NextDevice(Channel channel)
    {
        var devices = _mixer.ProfileChoices();
        if (devices.Count == 0) throw new InvalidOperationException("No playback devices found.");
        string? current = _mixer.IsSwitching(channel) && _picked.TryGetValue(channel, out var picked) ? picked : _mixer.PickerDeviceId(channel);
        int index = devices.FindIndex(d => string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase));
        var target = devices[(index + 1) % devices.Count];
        bool throughSonar = _mixer.SonarOutputId(channel) != null;
        if (target.Id != current) _mixer.PickDevice(channel, target.Id);
        _picked[channel] = target.Id;
        _notify(channel.ToString(), throughSonar ? $"Sonar plays {channel} on {target.Label}." : $"{channel} plays on {target.Label}.", false);
    }

    public void Dispose()
    {
        _mixer.ShortcutsChanged -= Register;
        foreach (int id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
