param([string]$Out = "$PSScriptRoot\..\shots\shot.png", [string]$Proc = "Voxel Playground", [int]$Index = 0)
# Captures the game window via PrintWindow (works for D3D windows with PW_RENDERFULLCONTENT).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  public struct RECT { public int L, T, R, B; }
}
"@
[W]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$p = Get-Process -Name $Proc -ErrorAction Stop | ? { $_.MainWindowHandle -ne 0 } | sort StartTime | select -Index $Index
$h = $p.MainWindowHandle
# Client area only (no title bar): PW_CLIENTONLY | PW_RENDERFULLCONTENT.
$r = New-Object W+RECT; [W]::GetClientRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 3) | Out-Null; $g.ReleaseHdc($dc)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
"$Out ${w}x$hh"
