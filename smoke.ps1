$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
public class Win32Cap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][Win32Cap]::SetProcessDPIAware()

$Root = $PSScriptRoot
$p = Start-Process -FilePath (Join-Path $Root 'publish\PageFileMonitor.exe') -PassThru
Start-Sleep -Seconds 6
if ($p.HasExited) {
    Write-Output ("FAILED: process exited, code " + $p.ExitCode)
    exit 1
}
$p.Refresh()
$r = New-Object RECT
[void][Win32Cap]::GetWindowRect($p.MainWindowHandle, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT(2):经 DWM 渲染窗口内容,即使被其他窗口遮挡也完整
[void][Win32Cap]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$bmp.Save((Join-Path $Root 'window.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
[void]$p.CloseMainWindow()
Start-Sleep -Seconds 4
if ($p.HasExited) { Write-Output "OK: window.png saved, app closed cleanly" } else { $p.Kill(); Write-Output "WARN: app killed" }
