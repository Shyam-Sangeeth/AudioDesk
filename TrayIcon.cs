using AudioDesk.Mixer;
using Forms = System.Windows.Forms;

namespace AudioDesk;

/// <summary>Tray icon: left-click opens the window, right-click picks each channel's device.</summary>
internal sealed class TrayIcon : IDisposable
{
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
        _icon.BalloonTipClicked += (_, _) => _hintClicked?.Invoke();
        _icon.BalloonTipClosed += (_, _) => _hintClicked = null;
        UpdateTooltip();
    }

    private Action? _hintClicked;

    /// <summary>Shows a notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void ShowHint(string title, string text, Action? onClick = null)
    {
        _hintClicked = onClick;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var active = _mixer.ActiveProfile();
        foreach (var (profile, label) in new[] { (Profile.Sonar, "Sonar"), (Profile.Headset, "Headset"), (Profile.HomeTheater, "Home Theater") })
        {
            if (profile == Profile.Sonar && !_mixer.IsSonarAvailable) continue;
            var item = new Forms.ToolStripMenuItem(label) { Checked = profile == Profile.Sonar ? _mixer.IsSonarMode : active == profile };
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
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => _exit()));
    }

    private Forms.ToolStripMenuItem ChannelMenu(Channel channel)
    {
        var choices = _mixer.PickerChoices(channel);
        string current = _mixer.PickerDeviceId(channel) ?? "";
        string currentLabel = choices.FirstOrDefault(c => c.Id == current)?.Label ?? "None";

        var item = new Forms.ToolStripMenuItem($"{channel}: {currentLabel}");
        foreach (var choice in choices)
        {
            var sub = new Forms.ToolStripMenuItem(choice.Label) { Checked = choice.Id == current };
            sub.Click += (_, _) =>
            {
                try { _mixer.PickDevice(channel, choice.Id); }
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
