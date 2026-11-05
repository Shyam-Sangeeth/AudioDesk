# Drives the Shortcuts tab with the keyboard: records shortcuts by typing them, checks keys that are refused (no Ctrl, Alt
# or Win), a clash with another app, moving keys between actions, and removing them. Only types into the window; never
# presses a shortcut while it is active. AudioDesk must be running with its window open; it comes to the front.
# It changes the saved shortcuts: back up %APPDATA%\AudioDesk\mixer.json first and restore it afterwards.
#
#   powershell -ExecutionPolicy Bypass -File tools\shortcuts-tab-test.ps1
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
}
"@
$json = "$env:APPDATA\AudioDesk\mixer.json"
$p = Get-Process AudioDesk | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$h = $p.MainWindowHandle
$A = [System.Windows.Automation.AutomationElement]
$win = $A::FromHandle($h)
$all = { $win.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) }
$tab = & $all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -and $_.Current.Name -eq 'Shortcuts' }
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 700
[void][K]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300

function Box($name) { & $all | Where-Object { $_.Current.Name -eq "$name shortcut" } | Select-Object -First 1 }
function Toggle($name) { (Box $name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
function State($name) { (Box $name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Keys($name) { $t = (Box $name).FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))); if ($t) { $t.Current.Name } }
function Press([byte[]]$vks) {
  foreach ($v in $vks) { [K]::keybd_event($v, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
  [array]::Reverse($vks); foreach ($v in $vks) { [K]::keybd_event($v, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
  Start-Sleep -Milliseconds 400
}
function Saved { Start-Sleep -Milliseconds 900; (Get-Content $json -Raw | ConvertFrom-Json).Shortcuts }
function Texts { & $all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } | ForEach-Object { $_.Current.Name } }
$CTRL = 0x11; $ALT = 0x12; $SHIFT = 0x10; $KK = 0x4B; $JJ = 0x4A; $ESC = 0x1B
$MODS = 0x1 -bor 0x2 -bor 0x4  # Alt+Ctrl+Shift

"1. Record Ctrl+Alt+Shift+K for Game Volume up"
Toggle 'Game Volume up'; "   listening: $(State 'Game Volume up') / '$(Keys 'Game Volume up')'"
Press @($CTRL, $ALT, $SHIFT, $KK)
"   box: '$(Keys 'Game Volume up')', listening: $(State 'Game Volume up'), saved: $((Saved).'game.volume_up')"
"   another app can take it too: $([K]::RegisterHotKey([IntPtr]::Zero, 7, $MODS, $KK)) (expect False: AudioDesk holds it)"

"2. Shift+K (no Ctrl/Alt/Win) for Media Mute"
Toggle 'Media Mute or unmute'; Press @($SHIFT, $KK)
"   listening: $(State 'Media Mute or unmute'), message: $((Texts) -match 'Use Ctrl')"
Press @($ESC)
"   after Esc: listening: $(State 'Media Mute or unmute'), box: '$(Keys 'Media Mute or unmute')'"

"3. Clash: Ctrl+Alt+Shift+J for Aux Next device, then another app takes those keys"
Toggle 'Aux Next device'; Press @($CTRL, $ALT, $SHIFT, $JJ)
"   saved: $((Saved).'aux.next_device')"
Toggle 'Media Mute or unmute'   # listening pauses AudioDesk's shortcuts
"   other app registers it while paused: $([K]::RegisterHotKey([IntPtr]::Zero, 8, $MODS, $JJ))"
Press @($ESC); Start-Sleep -Milliseconds 500
"   message: $((Texts) -match 'already uses')"
[void][K]::UnregisterHotKey([IntPtr]::Zero, 8)

"4. Ctrl+Alt+Shift+K again, for Headset: moves from Game Volume up"
Toggle 'Profiles Headset'; Press @($CTRL, $ALT, $SHIFT, $KK)
$s = Saved
"   headset: $($s.headset), game.volume_up: '$($s.'game.volume_up')', Game box: '$(Keys 'Game Volume up')'"
"   status: $((Texts) -match 'now does')"

"5. Remove the Headset and Aux shortcuts"
foreach ($b in (& $all | Where-Object { $_.Current.Name -eq 'Remove shortcut' })) {
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
"   saved now: '$((Saved | ConvertTo-Json -Compress))'"
