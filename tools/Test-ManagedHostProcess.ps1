[CmdletBinding()]
param([ValidateSet('B20','GSX300')][string]$Device = 'B20')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostExe=Join-Path $root 'dist\host\Timbre.Host.exe'
if (!(Test-Path -LiteralPath $hostExe)) { throw 'Build first with tools/Test-App.ps1.' }
$reportRoot=Join-Path $root ('artifacts\apo-host\managed-process-'+$Device.ToLowerInvariant()+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$target='EPOS-CONTROL-ABSENT-PROBE-'+[Guid]::NewGuid().ToString('N')
$processes=[Collections.Generic.List[object]]::new()
$mode = if ($Device -eq 'GSX300') { '--manage-gsx' } else { '--manage-b20' }
$ownerError = if ($Device -eq 'GSX300') { 'Another GSX initializer' } else { 'Another B20 initializer/managed helper' }
function Start-Probe([string]$name) {
    $directory=Join-Path $reportRoot $name
    $stop=Join-Path $reportRoot ('stop-'+$name)
    $arguments=@($mode,'--initial-state',('"'+(Join-Path $reportRoot 'missing-seed.json')+'"'),
        '--state-directory',('"'+(Join-Path $reportRoot 'unused-state')+'"'),'--device-instance',$target,
        '--report-directory',('"'+$directory+'"'),'--stop-file',('"'+$stop+'"'),'--seconds','30')
    # Retain the native process handle from Start through ExitCode. Start-Process -PassThru
    # can lose it when the competing owner exits before the returned object is inspected.
    $process=[Diagnostics.Process]::new()
    $process.StartInfo.FileName=$hostExe
    $process.StartInfo.Arguments=($arguments -join ' ')
    $process.StartInfo.UseShellExecute=$false
    $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true
    $process.StartInfo.RedirectStandardError=$true
    if (!$process.Start()) { throw ('Cannot start helper: '+$name) }
    $entry=[pscustomobject]@{Name=$name;Process=$process;Directory=$directory;Stop=$stop}
    $processes.Add($entry); return $entry
}
function Wait-Waiting($entry) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $path=Join-Path $entry.Directory 'heartbeat.json'
    while (!(Test-Path -LiteralPath $path) -and !$entry.Process.HasExited -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $path) -or $entry.Process.HasExited) { throw ('Probe did not wait: '+$entry.Name) }
    $heartbeat=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($heartbeat.State -ne 'WaitingForDevice' -or $heartbeat.ProcessId -ne $entry.Process.Id -or (Test-Path -LiteralPath (Join-Path $entry.Directory 'ready.json'))) { throw 'Absent target incorrectly reported ready.' }
}
function Stop-Probe($entry) {
    [IO.File]::WriteAllText($entry.Stop,'stop')
    if (!$entry.Process.WaitForExit(5000)) { throw ('Probe did not stop promptly: '+$entry.Name) }
    if ($entry.Process.ExitCode) { throw ('Probe shutdown failed: '+$entry.Name) }
    $stopped=Get-Content -LiteralPath (Join-Path $entry.Directory 'stopped.json') -Raw | ConvertFrom-Json
    if (!$stopped.Success -or $stopped.LastState.State -ne 'Stopped') { throw 'Managed shutdown report is invalid.' }
}
$failure=$null
try {
    $first=Start-Probe 'first'; Wait-Waiting $first
    $second=Start-Probe 'second'
    if (!$second.Process.WaitForExit(5000) -or !$second.Process.ExitCode) { throw 'A competing managed helper was not refused.' }
    $rejected=Get-Content -LiteralPath (Join-Path $second.Directory 'stopped.json') -Raw | ConvertFrom-Json
    if ($rejected.Success -or $rejected.Error -notmatch $ownerError) { throw 'Competing helper failed for an unexpected reason.' }
    Stop-Probe $first
    $third=Start-Probe 'after-release'; Wait-Waiting $third; Stop-Probe $third
    if (Test-Path -LiteralPath (Join-Path $reportRoot 'unused-state')) { throw 'Absent-device process unexpectedly accessed settings storage.' }
} catch { $failure=$_.Exception.Message }
finally {
    foreach($entry in $processes) {
        if (!$entry.Process.HasExited) {
            [IO.File]::WriteAllText($entry.Stop,'stop')
            if (!$entry.Process.WaitForExit(5000)) { Stop-Process -Id $entry.Process.Id -ErrorAction Stop; $failure='Probe needed forced termination: '+$entry.Name }
        }
        if ($entry.Process.HasExited) {
            [IO.File]::WriteAllText((Join-Path $reportRoot ($entry.Name+'-stdout.txt')),$entry.Process.StandardOutput.ReadToEnd())
            [IO.File]::WriteAllText((Join-Path $reportRoot ($entry.Name+'-stderr.txt')),$entry.Process.StandardError.ReadToEnd())
        }
    }
    [pscustomobject]@{Passed=($null -eq $failure);Device=$Device;Target=$target;AbsentDeviceWaited=($null -eq $failure);CompetingOwnerRefused=($null -eq $failure);OwnerReleased=($null -eq $failure);SettingsWrites=$false;CreatesVendorObjects=$false;Error=$failure;Reports=$reportRoot} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'assessment.json') -Encoding UTF8
    Write-Output ('Managed process reports: '+$reportRoot)
}
if ($failure) { throw $failure }
Write-Output 'Absent-device waiting, competing-owner refusal and prompt stop/restart passed without device writes.'
