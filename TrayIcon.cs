using AudioDesk.Mixer;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AudioDesk;

/// <summary>Tray icon: left-click opens the window, right-click picks each channel's device.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AudioDesk";

    private readonly Forms.NotifyIcon _icon;
    private readonly ChannelManager _mixer;
    private readonly Action _open;
    private readonly Action _exit;

    public TrayIcon(ChannelManager mixer, System.Drawing.Icon icon, Action open, Action exit)
    {
        _mixer = mixer;
        _open = open;
        _exit = exit;

        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);

        _icon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "AudioDesk",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _open();
        };
        UpdateTooltip();
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var active = _mixer.ActiveProfile();
        foreach (var (profile, label) in new[] { (Profile.Headset, "Headset"), (Profile.HomeTheater, "Home Theater") })
        {
            var item = new Forms.ToolStripMenuItem(label) { Checked = active == profile };
            item.Click += (_, _) =>
            {
                try { _mixer.ApplyProfile(profile); }
                catch (Exception ex) { ShowHint("Couldn't switch profile", ex.Message); }
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());

        foreach (var channel in Enum.GetValues<Channel>())
            menu.Items.Add(ChannelMenu(channel));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var open = new Forms.ToolStripMenuItem("Open AudioDesk", null, (_, _) => _open());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(open);

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows };
        startup.Click += (_, _) => StartsWithWindows = !StartsWithWindows;
        menu.Items.Add(startup);

        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => _exit()));
    }

    private Forms.ToolStripMenuItem ChannelMenu(Channel channel)
    {
        var choices = _mixer.Choices(channel);
        string current = _mixer.DeviceId(channel) ?? "";
        string currentLabel = choices.FirstOrDefault(c => c.Id == current)?.Label ?? "None";

        var item = new Forms.ToolStripMenuItem($"{channel}: {currentLabel}");
        foreach (var choice in choices)
        {
            var sub = new Forms.ToolStripMenuItem(choice.Label) { Checked = choice.Id == current };
            sub.Click += (_, _) =>
            {
                try { _mixer.SetDevice(channel, choice.Id); }
                catch (Exception ex) { ShowHint($"Couldn't change the {channel} device", ex.Message); }
            };
            item.DropDownItems.Add(sub);
        }
        return item;
    }

    public void UpdateTooltip()
    {
        try
        {
            string game = _mixer.Choices(Channel.Game).FirstOrDefault(c => c.Id == _mixer.DeviceId(Channel.Game))?.Label ?? "no device";
            string text = $"AudioDesk - Game: {game}";
            // NotifyIcon tooltips are limited to 127 characters.
            _icon.Text = text.Length > 127 ? text[..127] : text;
        }
        catch { }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
