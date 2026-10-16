# CI desktop only: give the real WPF window enough room for the review viewports.
# https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-changedisplaysettingsw
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This script is only for the disposable Windows CI desktop.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ReviewDesktop {
    [StructLayout(LayoutKind.Explicit, Size=220)]
    public struct Mode {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
    }
    [DllImport("user32.dll", EntryPoint="EnumDisplaySettingsW", CharSet=CharSet.Unicode)]
    public static extern bool Read(string device, int number, ref Mode mode);
    [DllImport("user32.dll", EntryPoint="ChangeDisplaySettingsW", CharSet=CharSet.Unicode)]
    public static extern int Change(ref Mode mode, uint flags);
}
'@
$mode = [ReviewDesktop+Mode]::new()
$mode.Size = 220
if (![ReviewDesktop]::Read($null, -1, [ref]$mode)) { throw 'Cannot read CI display settings' }
$mode.Width = 1920
$mode.Height = 1080
$mode.Fields = 0x180000
if ([ReviewDesktop]::Change([ref]$mode, 2) -ne 0) { throw 'CI desktop does not support 1920x1080' }
if ([ReviewDesktop]::Change([ref]$mode, 0) -ne 0) { throw 'Cannot resize CI desktop' }
Start-Sleep -Milliseconds 500
Write-Host 'CI desktop: 1920x1080; screenshots record their own WPF viewport and render DPI.'
