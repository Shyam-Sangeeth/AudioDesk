# Tools

Scripts for checking AudioDesk and taking the README's screenshots. Run them from the repository root.

| Script | What it does |
|---|---|
| `readme-shots.ps1 -Out <png>` | A screenshot of AudioDesk's window, after optionally switching tab (`-Tab`), opening a gear popup (`-Gear`) or an app's context menu (`-RightClick`). It brings the window to the front, and `-RightClick` moves the mouse. |
| `installer-walk.ps1 -Msi <msi> -OutDir <dir>` | Steps through the installer with the keyboard, taking a screenshot of each page, and stops before installing. |
| `local-api-state.py` | Prints what AudioDesk's local API reports (channels, volumes, devices, Sonar). Read only. Needs Knob support on. |
| `sonar-state.py [--watch <s>] [--raw <path>]` | Prints what Sonar reports through GG's local web server: each channel's output device, preset and slider, or one line a second with `--watch`, or one response as it comes with `--raw`. Read only. |
| `config-diff.py <a.json> <b.json>` | Lists the settings that differ between two `mixer.json` files, to see what changed before restoring a backup. |
| `shortcuts-tab-test.ps1` | Drives the Shortcuts tab with the keyboard: records, refuses, clashes with another app, moves and removes shortcuts. Changes the saved shortcuts. |
| `shortcut-keys-test.py [--setup]` | Presses the global shortcuts (F13-F20, set up with `--setup` while AudioDesk is closed) and prints what changed after each. **Changes real audio and Sonar routing**: run it only when that's wanted. It puts Sonar back at the end, not AudioDesk's settings. |

Before a test that changes settings, back up `%APPDATA%\AudioDesk\mixer.json` and restore it afterwards
(`config-diff.py` shows what changed meanwhile).
