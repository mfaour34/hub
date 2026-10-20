# Read-only footprint of the vendor peripheral suites and of Hub: disk, services, drivers, autostart, tasks, executables.
# Usage: powershell -ExecutionPolicy Bypass -File tools\footprint.ps1 [-HubDir <folder with Hub.exe>] > footprint.json
param([string]$HubDir = "$env:LOCALAPPDATA\Programs\Hub")

$suites = [ordered]@{
  'SteelSeries GG (Sonar)' = @{ Match = 'SteelSeries|Sonar'; Drivers = 'SteelSeries'; Dirs = @("$env:ProgramFiles\SteelSeries", "$env:ProgramData\SteelSeries", "$env:LOCALAPPDATA\SteelSeries", "$env:APPDATA\SteelSeries") }
  'Razer Synapse'          = @{ Match = 'Razer|Rz'; Drivers = 'Razer';           Dirs = @("$env:ProgramFiles\Razer", "${env:ProgramFiles(x86)}\Razer", "$env:ProgramData\Razer", "$env:LOCALAPPDATA\Razer", "$env:APPDATA\Razer") }
  'Logitech G HUB'         = @{ Match = 'LGHUB|lghub|logi_lamparray'; Drivers = 'Logitech';  Dirs = @("$env:ProgramFiles\LGHUB", "$env:ProgramData\LGHUB", "$env:LOCALAPPDATA\LGHUB", "$env:APPDATA\LGHUB") }
  'Hub'                    = @{ Match = '^Hub$'; Drivers = '^$';              Dirs = @($HubDir, "$env:APPDATA\Hub") }
}

function DirSize($d) { if (Test-Path $d) { (Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } else { 0 } }

$run = @()
foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
  $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
  if ($p) { $p.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { $run += [pscustomobject]@{ Name = $_.Name; Value = "$($_.Value)" } } }
}
$drivers = (pnputil /enum-drivers) -join "`n" -split "`r?`n`r?`n" | Where-Object { $_ -match 'Provider Name:\s+(.+)' } |
  ForEach-Object { if ($_ -match 'Provider Name:\s+(.+)') { $Matches[1].Trim() } }
$services = Get-CimInstance Win32_Service | Select-Object Name, DisplayName, StartMode, State, PathName
$tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Select-Object TaskName, TaskPath, State

$out = [ordered]@{}
foreach ($name in $suites.Keys) {
  $s = $suites[$name]; $m = $s.Match
  $dirs = $s.Dirs | Where-Object { Test-Path $_ }
  $svc = @($services | Where-Object { $_.Name -match $m -or $_.DisplayName -match $m -or $_.PathName -match $m })
  $out[$name] = [ordered]@{
    installed      = [bool]$dirs
    diskMB         = [math]::Round((($dirs | ForEach-Object { DirSize $_ }) | Measure-Object -Sum).Sum / 1MB)
    executables    = @($dirs | ForEach-Object { Get-ChildItem $_ -Recurse -Filter *.exe -File -Force -ErrorAction SilentlyContinue }).Count
    services       = $svc.Count
    servicesAuto   = @($svc | Where-Object StartMode -eq 'Auto').Count
    serviceNames   = @($svc | ForEach-Object { "$($_.Name) ($($_.StartMode), $($_.State))" })
    driversByVendor = @($drivers | Where-Object { $_ -match $s.Drivers }).Count
    autostart      = @($run | Where-Object { $_.Name -match $m -or $_.Value -match $m } | ForEach-Object Name)
    scheduledTasks = @($tasks | Where-Object { $_.TaskName -match $m -or $_.TaskPath -match $m } | ForEach-Object TaskName)
  }
}
$out | ConvertTo-Json -Depth 4
