# Works in Windows PowerShell 5.1 and PowerShell 7.
# Launches Hub and samples it (plus every child process, e.g. WebView2) while idle in the tray.
# Usage: powershell -ExecutionPolicy Bypass -File tools\measure-hub.ps1 -Exe <path\Hub.exe> [-Settle 30] [-Seconds 120] [-ExtraArgs ...]
param([Parameter(Mandatory)][string]$Exe, [int]$Settle = 30, [int]$Seconds = 120, [string[]]$ExtraArgs = @())

function Tree([int]$root) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
  $ids = @($root); $added = $true
  while ($added) { $added = $false; foreach ($p in $all) { if ($ids -contains $p.ParentProcessId -and -not ($ids -contains $p.ProcessId)) { $ids += $p.ProcessId; $added = $true } } }
  $ids | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = if ($ExtraArgs.Count) { Start-Process $Exe -ArgumentList $ExtraArgs -PassThru } else { Start-Process $Exe -PassThru }
$null = $p.WaitForInputIdle(30000)
$ready = $sw.Elapsed.TotalMilliseconds
Start-Sleep -Seconds $Settle

$cores = [Environment]::ProcessorCount
$t0 = (Tree $p.Id | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum; $w0 = [Diagnostics.Stopwatch]::StartNew()
$ws = @(); $priv = @(); $count = 0; $threads = 0
for ($i = 0; $i -lt $Seconds; $i += 2) {
  $procs = @(Tree $p.Id)
  $ws += ($procs | Measure-Object WorkingSet64 -Sum).Sum
  $priv += ($procs | Measure-Object PrivateMemorySize64 -Sum).Sum
  $count = [math]::Max($count, $procs.Count); $threads = ($procs | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum
  Start-Sleep -Seconds 2
}
$t1 = (Tree $p.Id | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum
[ordered]@{
  startupToIdleMs   = [math]::Round($ready)
  processes         = $count
  threads           = $threads
  workingSetMB      = [math]::Round(($ws | Measure-Object -Average).Average / 1MB, 1)
  privateMB         = [math]::Round(($priv | Measure-Object -Average).Average / 1MB, 1)
  cpuPercentOfOneCore = [math]::Round(($t1 - $t0) / $w0.Elapsed.TotalMilliseconds * 100, 3)
  cpuPercentOfMachine = [math]::Round(($t1 - $t0) / $w0.Elapsed.TotalMilliseconds * 100 / $cores, 4)
  sampledSeconds    = $Seconds
} | ConvertTo-Json
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
