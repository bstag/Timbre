[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$StartupState,[Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$StartupState=[IO.Path]::GetFullPath($StartupState)
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Choose a fresh report directory.' }
if ((Get-Service Audiosrv).Status -ne 'Running' -or (Get-Service EPOSGamingSuiteService).Status -ne 'Running') { throw 'This read-only preparation test requires both services Running.' }
if (@(Get-Process EposControl.Host -ErrorAction SilentlyContinue).Count) { throw 'Close existing experimental helpers first.' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$startup=Get-Content -LiteralPath $StartupState -Raw | ConvertFrom-Json
$hostExe=Join-Path $root 'dist\host\EposControl.Host.exe'
$runs=[Collections.Generic.List[object]]::new()
function Launch([string]$name) {
    $dir=Join-Path $ReportDirectory $name
    $stop=Join-Path $ReportDirectory ($name+'-stop')
    $process=Start-Process -FilePath $hostExe -ArgumentList @('--initialize-gsx-paused','--initial-state',('"'+$StartupState+'"'),'--report-directory',('"'+$dir+'"'),'--stop-file',('"'+$stop+'"'),'--seconds','30') -WindowStyle Hidden -PassThru
    $entry=[pscustomobject]@{Process=$process;Directory=$dir;Stop=$stop}
    $runs.Add($entry)
    $watch=[Diagnostics.Stopwatch]::StartNew(); $path=Join-Path $dir 'prepared.json'
    while (!(Test-Path -LiteralPath $path) -and !$process.HasExited -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $path) -or $process.HasExited) { throw ('Live preparation failed; see '+$dir) }
    $prepared=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($prepared.ProcessId -ne $process.Id -or $prepared.SettingsWrites -or $prepared.CreatedFresh -or $prepared.CreatesMissingObjects -or
        !$prepared.WaitingForExplicitInitializationRequest -or $prepared.Endpoint.Id -ne $startup.MicrophoneEndpointId -or
        $prepared.PlaybackEndpoint.Id -ne $startup.PlaybackEndpointId -or (Test-Path (Join-Path $dir 'ready.json'))) { throw 'Preparation must be read-only and match the exact captured pair.' }
    return $entry
}
$passed=$false; $failure=$null
try {
    $cancel=Launch 'cancel'
    [IO.File]::WriteAllText($cancel.Stop,'stop')
    if (!$cancel.Process.WaitForExit(5000) -or $cancel.Process.ExitCode -ne 0 -or (Test-Path (Join-Path $cancel.Directory 'ready.json'))) { throw 'Cancelled preparation did not exit cleanly without readiness.' }
    $guard=Launch 'running-service-guard'
    $requestPath=Join-Path $guard.Directory 'initialize-request.json'
    $request=[ordered]@{ProcessId=$guard.Process.Id;DeviceInstance=$startup.DeviceInstance}
    [IO.File]::WriteAllText(($requestPath+'.tmp'),($request | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($requestPath+'.tmp') -Destination $requestPath
    if (!$guard.Process.WaitForExit(5000)) { throw 'Running-service guard did not finish.' }
    $stopped=Get-Content (Join-Path $guard.Directory 'stopped.json') -Raw | ConvertFrom-Json
    if ($guard.Process.ExitCode -ne 1 -or (Test-Path (Join-Path $guard.Directory 'ready.json')) -or
        $stopped.Error -notmatch 'EPOSGamingSuiteService must be stopped') { throw 'Explicit request must be refused while vendor support is Running.' }
    $passed=$true
} catch { $failure=$_.Exception.ToString() }
finally {
    foreach($run in $runs) {
        if (!$run.Process.HasExited) {
            [IO.File]::WriteAllText($run.Stop,'stop')
            if (!$run.Process.WaitForExit(5000)) { Stop-Process -Id $run.Process.Id; $passed=$false; $failure='Our helper needed forced termination.' }
        }
    }
    $services=@(Get-Service Audiosrv,EPOSGamingSuiteService | Select-Object Name,@{Name='State';Expression={$_.Status.ToString()}})
    if (@($services | Where-Object State -ne 'Running').Count) { $passed=$false; $failure='Service state changed during read-only preparation.' }
    [ordered]@{Passed=$passed;Scope='Live preparation, cancellation and running-service refusal only; no service changes or actual GSX creation';SettingsWrites=$false;ServiceStates=$services;Error=$failure} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ReportDirectory 'summary.json') -Encoding UTF8
}
if(!$passed){throw $failure}
Write-Output ('GSX prepared-host cancellation and running-service guards passed: '+$ReportDirectory)
