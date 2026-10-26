using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioDesk.Mixer;

namespace AudioDesk;

/// <summary>
/// Lets other apps on this PC (Knob Hub's AudioDesk app) show and change AudioDesk's channels: a named pipe,
/// "AudioDesk.Api", that only the signed-in user can open, with one JSON object per line each way.
/// <para>AudioDesk sends <c>state</c> when a client connects and whenever something changes:
/// <c>{t: "state", sonar_available, sonar, profile: "headset" | "home_theater" | null, channels: [{id: "game" | "media" |
/// "aux", volume (0-1), muted, sonar_available, through_sonar, switching, device, devices: [{id, name, format}]}]}</c>, and
/// <c>{t: "error", text}</c> when a command fails.</para>
/// <para>Commands: <c>{t: "volume", channel, value}</c>, <c>{t: "mute", channel, muted}</c>,
/// <c>{t: "device", channel, id}</c>, <c>{t: "sonar", on}</c> (every channel), <c>{t: "channel_sonar", channel, on}</c>,
/// <c>{t: "profile", profile}</c>.</para>
/// </summary>
internal sealed class LocalApi : IDisposable
{
    public const string PipeName = "AudioDesk.Api";

    private readonly ChannelManager _mixer;
    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<StreamWriter> _clients = [];
    // Changes come in bursts (a switch updates several times); send the state once they settle.
    private readonly DispatcherTimer _debounce;

    public LocalApi(ChannelManager mixer, Dispatcher ui)
    {
        _mixer = mixer;
        _ui = ui;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Broadcast(), ui);
        _debounce.Stop();
        _mixer.StateChanged += StateChanged;
        _mixer.LevelChanged += LevelChanged;
    }

    private void LevelChanged(Channel channel) => StateChanged();

    public void Start() => Task.Run(() => AcceptAsync(_stop.Token));

    private void StateChanged()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                 PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
            }
            // Stopped (turned off in Settings): close the waiting pipe too, so nothing can connect to it.
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (IOException) { pipe?.Dispose(); await Task.Delay(1000, CancellationToken.None); continue; }
            _ = ServeAsync(pipe, stop);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            string state = await _ui.InvokeAsync(State);
            lock (_clients) _clients.Add(writer);
            Send(writer, state);
            while (await reader.ReadLineAsync(stop) is { } line)
            {
                string? error = await _ui.InvokeAsync(() => Handle(line));
                if (error != null) Send(writer, new JsonObject { ["t"] = "error", ["text"] = error }.ToJsonString());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }  // the client went away
        finally
        {
            lock (_clients) _clients.Remove(writer);
            await pipe.DisposeAsync();
        }
    }

    /// <summary>Runs a command on the UI thread; returns why it failed, or null.</summary>
    private string? Handle(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject msg) return null;
            string? Text(string name) => msg[name]?.GetValue<string>();
            Channel ChannelArg() => Enum.Parse<Channel>(Text("channel") ?? "", ignoreCase: true);

            switch (Text("t"))
            {
                case "volume":
                {
                    var channel = ChannelArg();
                    float value = Math.Clamp(msg["value"]!.GetValue<float>(), 0f, 1f);
                    _mixer.SetLevel(channel, value, _mixer.GetLevel(channel).Muted);
                    break;
                }
                case "mute":
                {
                    var channel = ChannelArg();
                    _mixer.SetLevel(channel, _mixer.GetLevel(channel).Volume, msg["muted"]!.GetValue<bool>());
                    break;
                }
                case "device":
                    _mixer.PickDevice(ChannelArg(), Text("id") ?? "");
                    break;
                case "sonar":
                    if (!_mixer.IsSonarAvailable) return "SteelSeries Sonar isn't installed.";
                    _mixer.SetAllThroughSonar(msg["on"]!.GetValue<bool>());
                    break;
                case "channel_sonar":
                    _mixer.SetThroughSonar(ChannelArg(), msg["on"]!.GetValue<bool>());
                    break;
                case "profile":
                    _mixer.ApplyProfile(Text("profile") == "home_theater" ? Profile.HomeTheater : Profile.Headset);
                    break;
                default:
                    return null;
            }
            StateChanged();
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return ex.Message;
        }
    }

    /// <summary>The state message (on the UI thread).</summary>
    private string State()
    {
        var channels = new JsonArray();
        foreach (var channel in ChannelManager.OutputChannels)
        {
            var (volume, muted) = _mixer.GetLevel(channel);
            var devices = new JsonArray();
            foreach (var choice in _mixer.PickerChoices(channel))
                devices.Add(new JsonObject { ["id"] = choice.Id, ["name"] = choice.Label, ["format"] = choice.Format });
            channels.Add(new JsonObject
            {
                ["id"] = channel.ToString().ToLowerInvariant(),
                ["volume"] = Math.Round(volume, 3),
                ["muted"] = muted,
                ["sonar_available"] = _mixer.SonarDeviceId(channel) != null,
                ["through_sonar"] = _mixer.IsThroughSonar(channel),
                ["switching"] = _mixer.IsSwitching(channel),
                ["device"] = _mixer.PickerDeviceId(channel),
                ["devices"] = devices,
            });
        }
        return new JsonObject
        {
            ["t"] = "state",
            ["sonar_available"] = _mixer.IsSonarAvailable,
            ["sonar"] = _mixer.IsSonarMode,
            ["profile"] = _mixer.ActiveProfile() switch
            {
                Profile.Headset => "headset",
                Profile.HomeTheater => "home_theater",
                _ => null,
            },
            ["channels"] = channels,
        }.ToJsonString();
    }

    private void Broadcast()
    {
        _debounce.Stop();
        List<StreamWriter> clients;
        lock (_clients)
        {
            if (_clients.Count == 0) return;
            clients = [.. _clients];
        }
        string state = State();
        foreach (var writer in clients) Send(writer, state);
    }

    // Off the UI thread, so a client that stopped reading can't hold AudioDesk up.
    private static void Send(StreamWriter writer, string line) => Task.Run(() =>
    {
        try
        {
            lock (writer) writer.WriteLine(line);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });

    public void Dispose()
    {
        _stop.Cancel();
        _debounce.Stop();
        _mixer.StateChanged -= StateChanged;
        _mixer.LevelChanged -= LevelChanged;
    }
}
