# Screenshots of AudioDesk's window for the README (docs/screenshots): the visible frame only, copied from the screen
# so popups and menus are included. Actions run first: -Gear "<Channel> settings" / "Profile settings",
# -Tab Mixer|Devices|Settings, -RightClick "<app name>". -Show starts the Release build first.
#
#   powershell -ExecutionPolicy Bypass -File tools\readme-shots.ps1 -Out docs\screenshots\mixer.png -Tab Mixer
#
# It brings AudioDesk's window to the front, and -RightClick moves the mouse: run it when the PC is free. Opening a
# gear popup or a context menu changes nothing; it's closed again afterwards.
param([string]$Out, [string]$Gear, [string]$Tab, [string]$RightClick, [switch]$Show, [int]$Wait = 0)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class D {
  public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
}
"@
$exe = Join-Path $PSScriptRoot "..\bin\Release\net10.0-windows\AudioDesk.exe"
if ($Show) { Start-Process $exe; Start-Sleep -Milliseconds 1500 }
$p = Get-Process AudioDesk | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "AudioDesk's window isn't open"; exit 1 }
$h = $p.MainWindowHandle
$A = [System.Windows.Automation.AutomationElement]
$win = $A::FromHandle($h)
$all = { $win.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) }

if ($Tab) {
  $item = & $all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -and $_.Current.Name -eq $Tab } | Select-Object -First 1
  if (-not $item) { "no tab $Tab"; exit 1 }
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 700
}
[void][D]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300
if ($Gear) {
  $g = & $all | Where-Object { ($_.Current.HelpText -eq $Gear -or $_.Current.Name -eq $Gear) -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
  if (-not $g) { "no button $Gear"; exit 1 }
  $g.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 800
}
if ($RightClick) {
  $e = & $all | Where-Object { $_.Current.Name -eq $RightClick -and -not $_.Current.IsOffscreen } | Select-Object -First 1
  if (-not $e) { "no element $RightClick"; exit 1 }
  $b = $e.Current.BoundingRectangle
  [void][D]::SetCursorPos([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2)); Start-Sleep -Milliseconds 150
  [D]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); [D]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 800
}

Start-Sleep -Milliseconds $Wait
$r = New-Object D+RECT
[void][D]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$gr = [System.Drawing.Graphics]::FromImage($bmp)
$gr.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out); "saved $Out ($($bmp.Width)x$($bmp.Height))"

# Close whatever was opened.
if ($Gear) { $g.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
if ($RightClick) { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300 }


