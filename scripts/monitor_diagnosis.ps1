param (
    [int]$IntervalSeconds = 3,
    [string]$OutputDir = ""
)

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot "..\logs"
}

$ErrorActionPreference = "Continue"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$jsonlPath = Join-Path $OutputDir "live-diagnosis.jsonl"
$latestStatusPath = Join-Path $OutputDir "live-diagnosis-latest.json"
$logAnalysisPath = Join-Path $OutputDir "live-diagnosis-events.log"

try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class GuiMetricsDiagnostic {
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
"@ -ErrorAction SilentlyContinue
} catch {}

$stats = [PSCustomObject]@{
    SessionStartTime = (Get-Date).ToString("o")
    SamplesCount = 0
    TotalCpuSum = 0.0
    MaxCpu = 0.0
    MinWorkingSetMB = 999999.0
    MaxWorkingSetMB = 0.0
    LastWorkingSetMB = 0.0
    MinPrivateMB = 999999.0
    MaxPrivateMB = 0.0
    LastPrivateMB = 0.0
    MaxThreads = 0
    MaxHandles = 0
    MaxGdiHandles = 0
    MaxUserHandles = 0
    TotalAppErrors = 0
    TotalAppWarnings = 0
    UnmatchedEventLogsCount = 0
    EpgLoadsCount = 0
    LastErrors = [System.Collections.Generic.List[string]]::new()
    ProcessHistory = [System.Collections.Generic.List[object]]::new()
}

$lastLogFile = $null
$lastLogOffset = 0L
$prevCpuTime = $null
$prevSampleTime = $null
$coreCount = [Environment]::ProcessorCount

Write-Output "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Starting IPTV Player Live Diagnosis Monitor (Interval: ${IntervalSeconds}s)..."

while ($true) {
    try {
        $now = Get-Date
        $nowIso = $now.ToString("o")
        
        $procs = Get-Process -Name "IptvPlayer.App" -ErrorAction SilentlyContinue
        $proc = $null
        if ($procs) {
            $proc = $procs | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
        }

        # Check App Logs
        $appLogDir = Join-Path $env:LOCALAPPDATA "WhoseIPTV\logs"
        if (Test-Path $appLogDir) {
            $todayLog = Get-ChildItem -Path $appLogDir -Filter "iptv-player-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($todayLog) {
                if ($lastLogFile -ne $todayLog.FullName) {
                    $lastLogFile = $todayLog.FullName
                    $lastLogOffset = [Math]::Max(0L, $todayLog.Length - 10240L)
                }
                
                if ($todayLog.Length > $lastLogOffset) {
                    try {
                        $fs = [System.IO.File]::Open($todayLog.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                        $fs.Seek($lastLogOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
                        $reader = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
                        $newText = $reader.ReadToEnd()
                        $lastLogOffset = $fs.Position
                        $reader.Dispose()
                        $fs.Dispose()

                        $lines = $newText -split "[\r\n]+"
                        foreach ($line in $lines) {
                            if ([string]::IsNullOrWhiteSpace($line)) { continue }
                            
                            if ($line -match "\[ERR\]|\[FTL\]|Exception") {
                                $stats.TotalAppErrors++
                                if ($stats.LastErrors.Count -ge 20) { $stats.LastErrors.RemoveAt(0) }
                                $stats.LastErrors.Add("[$($now.ToString('HH:mm:ss'))] $line")
                                Add-Content -Path $logAnalysisPath -Value "[$($now.ToString('HH:mm:ss'))] [ERROR] $line"
                            }
                            elseif ($line -match "\[WRN\]") {
                                $stats.TotalAppWarnings++
                                Add-Content -Path $logAnalysisPath -Value "[$($now.ToString('HH:mm:ss'))] [WARNING] $line"
                            }
                            
                            if ($line -match "Global event unmatched group") {
                                $stats.UnmatchedEventLogsCount++
                            }
                            if ($line -match "Loading real EPG") {
                                $stats.EpgLoadsCount++
                            }
                        }
                    } catch {}
                }
            }
        }

        if ($proc -and (-not $proc.HasExited)) {
            $workingSetMb = [math]::Round($proc.WorkingSet64 / 1MB, 2)
            $privateMb = [math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
            $peakWsMb = [math]::Round($proc.PeakWorkingSet64 / 1MB, 2)
            $threads = $proc.Threads.Count
            $handles = $proc.HandleCount
            $responding = $proc.Responding

            $gdi = 0
            $user = 0
            try {
                $gdi = [GuiMetricsDiagnostic]::GetGuiResources($proc.Handle, 0)
                $user = [GuiMetricsDiagnostic]::GetGuiResources($proc.Handle, 1)
            } catch {}

            $cpuPercent = 0.0
            $curCpuTime = $proc.TotalProcessorTime.TotalMilliseconds
            $curSampleTime = [System.Diagnostics.Stopwatch]::GetTimestamp()
            if ($null -ne $prevCpuTime -and $null -ne $prevSampleTime) {
                $timeDeltaSec = ($curSampleTime - $prevSampleTime) / [System.Diagnostics.Stopwatch]::Frequency
                if ($timeDeltaSec -gt 0) {
                    $cpuDeltaMs = $curCpuTime - $prevCpuTime
                    $cpuPercent = [math]::Round(($cpuDeltaMs / ($timeDeltaSec * 1000 * $coreCount)) * 100, 2)
                    if ($cpuPercent -lt 0) { $cpuPercent = 0.0 }
                }
            }
            $prevCpuTime = $curCpuTime
            $prevSampleTime = $curSampleTime

            $tcpCount = 0
            try {
                $conns = Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue
                if ($conns) {
                    $tcpCount = $conns.Count
                }
            } catch {}

            $stats.SamplesCount++
            $stats.TotalCpuSum += $cpuPercent
            if ($cpuPercent -gt $stats.MaxCpu) { $stats.MaxCpu = $cpuPercent }
            if ($workingSetMb -lt $stats.MinWorkingSetMB) { $stats.MinWorkingSetMB = $workingSetMb }
            if ($workingSetMb -gt $stats.MaxWorkingSetMB) { $stats.MaxWorkingSetMB = $workingSetMb }
            $stats.LastWorkingSetMB = $workingSetMb

            if ($privateMb -lt $stats.MinPrivateMB) { $stats.MinPrivateMB = $privateMb }
            if ($privateMb -gt $stats.MaxPrivateMB) { $stats.MaxPrivateMB = $privateMb }
            $stats.LastPrivateMB = $privateMb

            if ($threads -gt $stats.MaxThreads) { $stats.MaxThreads = $threads }
            if ($handles -gt $stats.MaxHandles) { $stats.MaxHandles = $handles }
            if ($gdi -gt $stats.MaxGdiHandles) { $stats.MaxGdiHandles = $gdi }
            if ($user -gt $stats.MaxUserHandles) { $stats.MaxUserHandles = $user }

            $entry = [ordered]@{
                Timestamp = $nowIso
                PID = $proc.Id
                Responding = $responding
                CpuPercent = $cpuPercent
                WorkingSetMB = $workingSetMb
                PrivateMemoryMB = $privateMb
                PeakWorkingSetMB = $peakWsMb
                Threads = $threads
                Handles = $handles
                GdiHandles = $gdi
                UserHandles = $user
                TcpConnections = $tcpCount
                AppWarnings = $stats.TotalAppWarnings
                AppErrors = $stats.TotalAppErrors
                UnmatchedSportsLogs = $stats.UnmatchedEventLogsCount
                EpgLoads = $stats.EpgLoadsCount
            }

            $jsonLine = $entry | ConvertTo-Json -Compress
            Add-Content -Path $jsonlPath -Value $jsonLine

            $avgCpu = if ($stats.SamplesCount -gt 0) { [math]::Round($stats.TotalCpuSum / $stats.SamplesCount, 2) } else { 0.0 }
            $summary = [ordered]@{
                Status = "Running"
                LastUpdated = $nowIso
                AppPID = $proc.Id
                AppPath = $proc.Path
                IsResponding = $responding
                Samples = $stats.SamplesCount
                CurrentCPU = "$cpuPercent %"
                AverageCPU = "$avgCpu %"
                PeakCPU = "$($stats.MaxCpu) %"
                CurrentWorkingSetMB = "$workingSetMb MB"
                MinWorkingSetMB = "$($stats.MinWorkingSetMB) MB"
                MaxWorkingSetMB = "$($stats.MaxWorkingSetMB) MB"
                CurrentPrivateBytesMB = "$privateMb MB"
                PeakPrivateBytesMB = "$($stats.MaxPrivateMB) MB"
                Threads = $threads
                PeakThreads = $stats.MaxThreads
                Handles = $handles
                PeakHandles = $stats.MaxHandles
                GdiHandles = $gdi
                PeakGdiHandles = $stats.MaxGdiHandles
                UserHandles = $user
                PeakUserHandles = $stats.MaxUserHandles
                ActiveTcpConnections = $tcpCount
                TotalErrorsRecorded = $stats.TotalAppErrors
                TotalWarningsRecorded = $stats.TotalAppWarnings
                SportsEventUnmatchedLogs = $stats.UnmatchedEventLogsCount
                EpgLoadsTriggered = $stats.EpgLoadsCount
                RecentErrors = $stats.LastErrors
            }

            $summary | ConvertTo-Json -Depth 5 | Set-Content -Path $latestStatusPath

            if ($stats.SamplesCount % 10 -eq 0) {
                Write-Output "[$(Get-Date -Format 'HH:mm:ss')] Sample #$($stats.SamplesCount): RAM=${workingSetMb}MB (Private: ${privateMb}MB), CPU=${cpuPercent}%, Threads=${threads}, Handles=${handles}, GDI=${gdi}, USER=${user}, SportsLogsSpam=${stats.UnmatchedEventLogsCount}, Errors=${stats.TotalAppErrors}"
            }
        } else {
            $prevCpuTime = $null
            $prevSampleTime = $null
            $idleEntry = [ordered]@{
                Status = "ProcessNotRunning"
                LastUpdated = $nowIso
                TotalErrorsRecorded = $stats.TotalAppErrors
                TotalWarningsRecorded = $stats.TotalAppWarnings
                Message = "Waiting for IptvPlayer.App process..."
            }
            $idleEntry | ConvertTo-Json -Depth 3 | Set-Content -Path $latestStatusPath
            Write-Output "[$(Get-Date -Format 'HH:mm:ss')] Waiting for IptvPlayer.App to run..."
        }
    } catch {
        Write-Output "[$(Get-Date -Format 'HH:mm:ss')] Monitor Loop Error: $_"
    }

    Start-Sleep -Seconds $IntervalSeconds
}
