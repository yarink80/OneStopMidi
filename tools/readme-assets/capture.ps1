<#
.SYNOPSIS
    Captures the OneStopMidi main window (window contents only, via PrintWindow) to a PNG for the README.

.EXAMPLE
    ./tools/readme-assets/capture.ps1 -ProcessId 1234 -OutPath docs/images/main-window.png
#>
param([Parameter(Mandatory)] [int] $ProcessId, [Parameter(Mandatory)] [string] $OutPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
"@
[Win]::SetProcessDPIAware() | Out-Null
$hwnd = (Get-Process -Id $ProcessId).MainWindowHandle
$win = New-Object Win+RECT
$vis = New-Object Win+RECT
[Win]::GetWindowRect($hwnd, [ref]$win) | Out-Null
# DWMWA_EXTENDED_FRAME_BOUNDS (9): the visible frame, without the invisible resize border.
[Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$vis, 16) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($win.R - $win.L), ($win.B - $win.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[Win]::PrintWindow($hwnd, $dc, 2) | Out-Null   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($dc)
$crop = New-Object System.Drawing.Rectangle ($vis.L - $win.L), ($vis.T - $win.T), ($vis.R - $vis.L), ($vis.B - $vis.T)
$out = $bmp.Clone($crop, $bmp.PixelFormat)
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath -Parent) | Out-Null
$out.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $OutPath ($($crop.Width)x$($crop.Height))"
