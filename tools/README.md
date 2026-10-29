# Tools

Scripts for checking AudioDesk and taking the README's screenshots. Run them from the repository root.

| Script | What it does |
|---|---|
| `readme-shots.ps1 -Out <png>` | A screenshot of AudioDesk's window, after optionally switching tab (`-Tab`), opening a gear popup (`-Gear`) or an app's context menu (`-RightClick`). It brings the window to the front, and `-RightClick` moves the mouse. |
| `installer-walk.ps1 -Msi <msi> -OutDir <dir>` | Steps through the installer with the keyboard, taking a screenshot of each page, and stops before installing. |
| `local-api-state.py` | Prints what AudioDesk's local API reports (channels, volumes, devices, Sonar). Read only. Needs Knob support on. |

Before a test that changes settings, back up `%APPDATA%\AudioDesk\mixer.json` and restore it afterwards.
