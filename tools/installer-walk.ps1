# Steps through the AudioDesk installer with the keyboard (its controls have no UI Automation patterns), taking a
# screenshot of each screen, and stops on the "ready to install" page without installing: cancel it there.
#
#   powershell -ExecutionPolicy Bypass -File tools\installer-walk.ps1 -Msi installer\out\AudioDesk-1.0.3-x64.msi -OutDir <dir>
#
# It sends keys to the installer window in front, so don't use the PC while it runs.
param([string]$Msi, [string]$OutDir)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W5 {
  public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string t);
}
"@
function Shot([string]$name) {
  Start-Sleep -Milliseconds 900
  $h = [W5]::GetForegroundWindow()
  $r = New-Object W5+RECT; [void][W5]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
  $bmp.Save("$OutDir\$name.png"); "saved $name"
}
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }

Start-Process msiexec.exe -ArgumentList "/i `"$Msi`""
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $h = [W5]::FindWindow($null, "AudioDesk Setup") }
if ($h -eq [IntPtr]::Zero) { "no installer window"; exit 1 }
Start-Sleep -Seconds 2
[void][W5]::SetForegroundWindow($h)
Shot "1-welcome"
Keys "{ENTER}"          # Next
Shot "2-folder"
Keys "%c"               # Change...
Shot "3-change"
Keys "{ESC}"            # close the folder picker
[void][W5]::SetForegroundWindow($h)
Keys "%n"               # Next
Shot "4-ready"
