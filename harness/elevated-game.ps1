# Run elevated by the "admin" scenario (PR #20): starts TestWindow as administrator, then waits
# for signal files from the harness, which runs unelevated and so can't close elevated windows.
#   <signalDir>\close-game : close the elevated TestWindow
#   <signalDir>\stop       : close it, and end the elevated GameBackdrop copy started from <appExe>
param([string]$testExe, [string]$appExe, [string]$signalDir)
$game = Start-Process -FilePath $testExe -PassThru
Set-Content -Path (Join-Path $signalDir 'game-pid') -Value $game.Id -Encoding ascii
$deadline = (Get-Date).AddMinutes(20)
while ((Get-Date) -lt $deadline) {
    if (Test-Path (Join-Path $signalDir 'close-game')) {
        if (-not $game.HasExited) { $game.CloseMainWindow() | Out-Null; if (-not $game.WaitForExit(5000)) { $game.Kill() } }
        Remove-Item (Join-Path $signalDir 'close-game')
        Set-Content -Path (Join-Path $signalDir 'game-closed') -Value 'yes' -Encoding ascii
    }
    if (Test-Path (Join-Path $signalDir 'stop')) { break }
    Start-Sleep -Milliseconds 200
}
if (-not $game.HasExited) { $game.Kill() }
Get-Process GameBackdrop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | ForEach-Object { $_.Kill() }
Set-Content -Path (Join-Path $signalDir 'stopped') -Value 'yes' -Encoding ascii
