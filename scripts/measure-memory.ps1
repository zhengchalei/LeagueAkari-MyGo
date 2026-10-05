param(
    [Parameter(Mandatory=$true)][int]$RootProcessId,
    [Parameter(Mandatory=$true)][string]$Application,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Scenario = 'own history loaded, main window only'
)
$ErrorActionPreference = 'Stop'
$taskProcesses = @(Get-CimInstance Win32_Process)
$taskIds = [System.Collections.Generic.HashSet[int]]::new()
[void]$taskIds.Add($RootProcessId)
do {
    $taskAdded = $false
    foreach ($taskProcess in $taskProcesses) {
        if ($taskIds.Contains([int]$taskProcess.ParentProcessId) -and $taskIds.Add([int]$taskProcess.ProcessId)) {
            $taskAdded = $true
        }
    }
} while ($taskAdded)
$taskLive = @(Get-Process -Id @($taskIds) -ErrorAction SilentlyContinue)
if (-not ($taskLive | Where-Object Id -eq $RootProcessId)) { throw 'Root process has exited' }
[ordered]@{
    application = $Application
    version = $Version
    measuredAt = [DateTimeOffset]::Now.ToString('o')
    scenario = $Scenario
    scope = 'root and all descendant processes, including WebView2/Electron subprocesses'
    processCount = $taskLive.Count
    workingSetMiB = [Math]::Round(($taskLive | Measure-Object WorkingSet64 -Sum).Sum / 1MB, 1)
    privateBytesMiB = [Math]::Round(($taskLive | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB, 1)
} | ConvertTo-Json
