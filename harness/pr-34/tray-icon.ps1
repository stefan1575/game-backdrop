# Captures the app's notification-area icon and nothing else: starts the exe with a scratch
# --settings file, promotes only this exe's own NotifyIconSettings entry so Windows 11 shows the
# icon on the taskbar instead of the overflow, asks the shell for that icon's rect
# (Shell_NotifyIconGetRect; WinForms' first NotifyIcon has uID 1), and copies that rect from the
# screen. Then it stops the app and removes the entry it promoted.
param([string] $Exe, [string] $Out, [string] $Caption)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Tray -Name W -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct NOTIFYICONIDENTIFIER { public uint cbSize; public System.IntPtr hWnd; public uint uID; public System.Guid guidItem; }
public struct RECT { public int L, T, R, B; }
[System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
'@
[void][Tray.W]::SetProcessDPIAware()

$dir = Join-Path $env:TEMP ('gb-tray-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $dir | Out-Null
[IO.File]::WriteAllText("$dir\settings.json", '{"games":[],"startAtLogon":false}')
$app = Start-Process $Exe -ArgumentList '--settings', "`"$dir\settings.json`"" -PassThru
$key = 'HKCU:\Control Panel\NotifyIconSettings'
$entry = $null
try {
    $leaf = Split-Path (Split-Path $Exe) -Leaf
    for ($i = 0; $i -lt 40 -and -not $entry; $i++) {
        Start-Sleep -Milliseconds 250
        $entry = Get-ChildItem $key | Where-Object { (Get-ItemProperty $_.PSPath).ExecutablePath -like "*\$leaf\GameBackdrop.exe" } | Select-Object -First 1
    }
    if (-not $entry) { throw 'The shell made no NotifyIconSettings entry for this exe.' }
    Set-ItemProperty $entry.PSPath -Name IsPromoted -Value 1 -Type DWord
    Start-Sleep -Seconds 2

    $rect = $null
    $windows = New-Object System.Collections.Generic.List[IntPtr]
    [void][Tray.W]::EnumWindows({ param($h, $l) $p = 0; [void][Tray.W]::GetWindowThreadProcessId($h, [ref]$p); if ($p -eq $app.Id) { $windows.Add($h) }; $true }, [IntPtr]::Zero)
    foreach ($h in $windows) {
        $id = New-Object Tray.W+NOTIFYICONIDENTIFIER
        $id.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($id); $id.hWnd = $h; $id.uID = 1
        $r = New-Object Tray.W+RECT
        if ([Tray.W]::Shell_NotifyIconGetRect([ref]$id, [ref]$r) -eq 0) { $rect = $r; break }
    }
    if (-not $rect) { throw 'The shell gave no rect for the icon.' }
    $w = $rect.R - $rect.L; $h = $rect.B - $rect.T
    $icon = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($icon); $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size $w, $h)); $g.Dispose()

    # The icon at its real size and at 4x (nearest neighbour, so its pixels show), captioned.
    $scale = 4; $pad = 16; $top = 40
    $canvas = New-Object System.Drawing.Bitmap ([Math]::Max(520, $w + $w * $scale + 3 * $pad)), ($top + $h * $scale + $pad)
    $g = [System.Drawing.Graphics]::FromImage($canvas); $g.Clear([System.Drawing.Color]::FromArgb(28, 28, 30))
    $g.TextRenderingHint = 'AntiAlias'
    $g.DrawString($Caption, (New-Object System.Drawing.Font 'Segoe UI', 10), [System.Drawing.Brushes]::White, $pad, 10)
    $g.DrawImage($icon, $pad, $top, $w, $h)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($icon, (2 * $pad + $w), $top, $w * $scale, $h * $scale)
    $g.Dispose(); $canvas.Save($Out); $canvas.Dispose(); $icon.Dispose()
    "saved $Out (icon rect $($rect.L),$($rect.T) ${w}x$h)"
}
finally {
    Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    if ($entry) { Remove-Item $entry.PSPath -ErrorAction SilentlyContinue }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
