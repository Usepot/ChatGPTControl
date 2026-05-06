$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
'@

$dir = Join-Path (Get-Location) 'desktop_state'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$bounds = $screen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$screenshotPath = Join-Path $dir "screenshot_$ts.png"
$bmp.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()

$h = [Win32]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 1024
[void][Win32]::GetWindowText($h, $sb, $sb.Capacity)
$pidNum = 0
[void][Win32]::GetWindowThreadProcessId($h, [ref]$pidNum)
$proc = Get-Process -Id $pidNum -ErrorAction SilentlyContinue
$windows = Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName, Id, MainWindowTitle | Sort-Object ProcessName

[pscustomobject]@{
  Screenshot = $screenshotPath
  ActiveProcess = $proc.ProcessName
  ActivePid = $pidNum
  ActiveWindowTitle = $sb.ToString()
  VisibleWindows = $windows
} | ConvertTo-Json -Depth 5
