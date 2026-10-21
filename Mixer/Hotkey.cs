using System.Windows.Input;

namespace AudioDesk.Mixer;

public enum HotkeyAction
{
    // Per channel
    NextDevice, VolumeUp, VolumeDown, Mute,
    // Profiles
    ToggleProfile, HeadsetProfile, HomeTheaterProfile,
}

/// <summary>Where a shortcut lives: a channel's action, or (Channel = null) a profile action.</summary>
public readonly record struct HotkeySlot(Channel? Channel, HotkeyAction Action)
{
    public static readonly HotkeyAction[] ChannelActions = [HotkeyAction.NextDevice, HotkeyAction.VolumeUp, HotkeyAction.VolumeDown, HotkeyAction.Mute];
    public static readonly HotkeyAction[] ProfileActions = [HotkeyAction.ToggleProfile, HotkeyAction.HeadsetProfile, HotkeyAction.HomeTheaterProfile];

    public string Label => Action switch
    {
        HotkeyAction.NextDevice => Channel == Mixer.Channel.Mic ? "Switch microphone" : "Switch device",
        HotkeyAction.VolumeUp => "Volume up",
        HotkeyAction.VolumeDown => "Volume down",
        HotkeyAction.Mute => "Mute / unmute",
        HotkeyAction.ToggleProfile => "Switch profile",
        HotkeyAction.HeadsetProfile => "Headset",
        _ => "Home Theater",
    };

    /// <summary>"Game: Volume up" or "Profile: Headset".</summary>
    public override string ToString() => $"{(Channel?.ToString() ?? "Profile")}: {Label}";
}

/// <summary>A key combination such as Ctrl+Alt+M.</summary>
public sealed record Hotkey(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num {(int)(key - Key.NumPad0)}",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.OemPipe => "\\",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        _ => key.ToString(),
    };

    /// <summary>Stored as "Control, Alt+M" (enum names), which round-trips exactly.</summary>
    public string Serialize() => $"{Modifiers}+{Key}";

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int split = text.LastIndexOf('+');
        if (split <= 0) return null;
        return Enum.TryParse<ModifierKeys>(text[..split], out var modifiers) && Enum.TryParse<Key>(text[(split + 1)..], out var key)
            ? new Hotkey(modifiers, key)
            : null;
    }
}
