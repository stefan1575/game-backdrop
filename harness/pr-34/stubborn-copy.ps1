# Stands in for a running copy that never quits (as one with a game it can't put back): holds the
# single-instance mutex and the scope's quit event, and ignores the signal. Ends after $Seconds.
param([string] $QuitEventName, [int] $Seconds = 90)
$quit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, $QuitEventName)
$openList = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, 'Local\GameBackdrop.OpenList')
$mutex = New-Object System.Threading.Mutex($false, 'Local\GameBackdrop.SingleInstance')
[void]$mutex.WaitOne()
Start-Sleep -Seconds $Seconds
$mutex.ReleaseMutex()
