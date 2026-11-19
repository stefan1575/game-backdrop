# Waits for a dialog owned by a process, captures that window's own pixels (PrintWindow), then
# closes it. Nothing else on the screen is captured.
param([int] $ProcessId, [string] $Out, [int] $TimeoutSeconds = 90)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Cap -Name W -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr dc, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int L, T, R, B; }
'@
[void][Cap.W]::SetProcessDPIAware()
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$found = [IntPtr]::Zero
while ($found -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
    [void][Cap.W]::EnumWindows({ param($h, $l)
        $p = 0; [void][Cap.W]::GetWindowThreadProcessId($h, [ref]$p)
        $c = New-Object System.Text.StringBuilder 64; [void][Cap.W]::GetClassName($h, $c, 64)
        if ($p -eq $ProcessId -and $c.ToString() -eq '#32770' -and [Cap.W]::IsWindowVisible($h)) { $script:found = $h; return $false }
        return $true }, [IntPtr]::Zero)
    if ($found -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
}
if ($found -eq [IntPtr]::Zero) { throw "No dialog from process $ProcessId." }
Start-Sleep -Milliseconds 500
$r = New-Object Cap.W+RECT; [void][Cap.W]::GetWindowRect($found, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
[void][Cap.W]::PrintWindow($found, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose(); $bmp.Save($Out); $bmp.Dispose()
[void][Cap.W]::PostMessage($found, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
"captured $Out"
