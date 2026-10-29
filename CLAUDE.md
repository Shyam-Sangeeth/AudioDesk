# AudioDesk

A WPF tray app (.NET 10, C#) that's a **helper for SteelSeries Sonar**: Game, Media and Aux channels that each go
through Sonar (for its EQ and effects) or bypass it and play straight on a real device at full quality, plus more
control over Sonar (its device, presets and volume per channel). Not a Sonar alternative: voice apps and the mic are
deliberately left to Sonar. The README is the user documentation; keep it in step with the app.

## Commands

```powershell
dotnet build -c Debug                      # quick check; bin\Debug\net10.0-windows\AudioDesk.exe
dotnet build -c Release                    # bin\Release\net10.0-windows\AudioDesk.exe
bin\Debug\net10.0-windows\AudioDesk.exe --selftest <file>   # headless: devices, apps, routing test; writes a report
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1   # MSI in installer\out (version from the csproj)
```

There are no unit tests: check changes with `--selftest`, a build, and by driving the real window (UI Automation).
`tools/` has the README screenshot script, an installer walk-through and a read-only local API reader (see its
README).
A running AudioDesk locks its files: `AudioDesk.exe --exit` closes it (and waits), `--minimized` starts it in the tray.

## Layout

- `Mixer/ChannelManager.cs`: the core. Channels, routing enforcement (a 1.5 s scan), through-Sonar, profiles
  (Sonar, Headset, Home Theater), keep awake, and the settings file (`MixerConfig`, `%APPDATA%\AudioDesk\mixer.json`).
- `Audio/`: `AudioService` (device cache, sessions, formats), `SonarClient` (Sonar's local web server: device, presets,
  volume and mute per Sonar channel), `Interop` (undocumented `IPolicyConfig` / `IAudioPolicyConfigFactory`),
  `MeterService`, `KeepAwakeService`, `AudioEffects`.
- `LocalApi.cs`: named pipe `AudioDesk.Api` (JSON lines), used by Knob Hub's AudioDesk app. Started and stopped by the
  **Knob support** setting (`MixerConfig.LocalApiEnabled`).
- `ViewModels.cs`, `MainWindow.xaml(.cs)`: Mixer, Devices and Settings tabs, under its own title bar (`WindowChrome`,
  like Knob Hub's; fixed size, so only Minimize and Close). `TrayIcon.cs`, `App.xaml.cs` (startup,
  single instance, `--selftest`), `Updates/UpdateService.cs` (GitHub releases, verified MSI download).

## Rules

- **Keep slow audio work off the UI thread.** Default-device switches, session scans and device reads run on workers
  (`SwitchInBackground`, `SetDefaultInBackground`, the device cache); the UI reads the cache. The user noticed 2 s
  freezes before this.
- **Keep `RenderMode.SoftwareOnly`.** WPF's GPU path draws a blank white window on the development PC.
- **The local API is a contract** with Knob Hub's AudioDesk app (`KnobHub` repo, `hub/Plugins/AudioDesk`). Keep it
  backward compatible, update the doc comment at the top of `LocalApi.cs` and the README's Local API section, and
  change the plugin with it.
- **Sonar** is reached through GG's local web server (Classic mode only, not an official API). Sonar's own devices are
  chosen with the Sonar button, not the device pickers; `SteelSeriesSonar` and voice apps (`VoiceAppPrefixes`) are
  never listed or routed.
- **Settings stay loadable:** `mixer.json` from older versions must still load (see the migration that drops the old
  Chat/Mic/ChatMix keys). Add new settings with defaults.
- Bind read-only view-model properties with `Mode=OneWay` (e.g. `ProgressBar.Value` defaults to TwoWay and crashed a
  release at startup). Use `Checked`/`Unchecked`, not `Click`, on checkboxes: UI Automation's Toggle doesn't raise
  `Click`.

## Testing on the user's PC

AudioDesk runs every day on this PC with the user's real setup:

- **Before tests, back up `%APPDATA%\AudioDesk\mixer.json`, and restore it after**, keeping any change the user made
  meanwhile (diff first).
- **Don't toggle real Sonar or device settings**, or press play in their apps, unless asked.
- **Don't steal focus without need:** the user may be gaming. Note whether AudioDesk's window was open and put it
  back the same way (window open, or `--minimized` in the tray).

## Releases

1. Bump `<Version>` in `AudioDesk.csproj`.
2. Build the installer.
3. Create a GitHub release `v<version>` with `AudioDesk-<version>-x64.msi` attached.

The installer is per-machine since 1.0.2: Program Files by default, a folder page to choose another, the folder kept
for upgrades (`HKLM\Software\AudioDesk\Installer\InstallFolder`, which Knob Hub's AudioDesk app also reads), and
AudioDesk started through Explorer so it doesn't run elevated. 1.0.0 and 1.0.1 were per-user; the installer refuses
to install over one (launch condition), so they must be uninstalled first.

The in-app updater reads the latest release and checks the asset's SHA-256 digest. Releases restarted at 1.0.0 on
2026-10-26 (earlier 1.0.x were deleted), and the installer won't downgrade, so never publish a lower version than the
latest. Commit and publish only when the user asks.

## Docs

- README screenshots are in `docs/screenshots`: retake them when the UI changes.
- Write user-facing text and docs in plain, specific English, and describe AudioDesk as a Sonar helper.
- In this environment, write commit messages to a file and use `git commit -F` (Windows PowerShell adds a BOM
  otherwise).
