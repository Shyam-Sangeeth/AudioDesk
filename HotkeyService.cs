using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using AudioDesk.Mixer;

namespace AudioDesk;

/// <summary>System-wide shortcuts (RegisterHotKey) received on a hidden message-only window.</summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("AudioDesk.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        });
        _window.AddHook(WndProc);
    }

    /// <summary>Returns false when another app already owns this combination.</summary>
    public bool Register(Hotkey hotkey, bool repeat, Action handler)
    {
        uint modifiers = (repeat ? 0 : ModNoRepeat)
            | (hotkey.Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
            | (hotkey.Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
            | (hotkey.Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
            | (hotkey.Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);

        int id = _nextId++;
        if (!RegisterHotKey(_window.Handle, id, modifiers, vk)) return false;
        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys) UnregisterHotKey(_window.Handle, id);
        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
