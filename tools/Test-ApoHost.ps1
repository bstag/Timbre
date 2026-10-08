[CmdletBinding()]
param([switch]$StopSuiteTemporarily, [switch]$HardwareAudio, [switch]$FreshInitialization, [switch]$ManagedLifecycle)
$ErrorActionPreference='Stop'
if ($ManagedLifecycle) { $FreshInitialization=$true }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostExe=Join-Path $root 'dist\host\Timbre.Host.exe'
$testDll=Join-Path $root 'tests\Timbre.Tests\bin\Release\net9.0\Timbre.Tests.dll'
if (!(Test-Path -LiteralPath $hostExe) -or !(Test-Path -LiteralPath $testDll)) { throw 'Build first with tools/Test-App.ps1.' }
if ($HardwareAudio -and !$StopSuiteTemporarily) { throw '-HardwareAudio requires -StopSuiteTemporarily.' }
if ($FreshInitialization -and !$StopSuiteTemporarily) { throw '-FreshInitialization requires -StopSuiteTemporarily.' }
if ($StopSuiteTemporarily) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The temporary service-stop check needs an administrator PowerShell window. No service changes were made.'
    }
}
$reportRoot=Join-Path $root ('artifacts\apo-host\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$hostReports=Join-Path $reportRoot 'host'
$stopFile=Join-Path $reportRoot 'stop-host'
$startupPath=Join-Path $reportRoot 'startup-state.json'
$stateDirectory=Join-Path $reportRoot 'processing-state'
$events=[Collections.Generic.List[object]]::new()
$differences=[Collections.Generic.List[string]]::new()
$errors=[Collections.Generic.List[string]]::new()
$warnings=[Collections.Generic.List[string]]::new()
$audioOutcome=if ($HardwareAudio) { 'NotRun' } else { 'NotRequested' }
$controlOutcome='NotRun'
$service=Get-Service EPOSGamingSuiteService
$serviceWasRunning=$service.Status -eq 'Running'
$suiteProcesses=@(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue)
$suitePath='C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuite.exe'
$hostProcess=$null
$baseline=$null
$restored=$null
$stoppedSnapshot=$null
$uiClosed=$false
$serviceStopAttempted=$false
$effectsTestRan=$false
$createdFresh=$false
$savedRestoreVerified=$false
$startupRestorePolicy='None'
function Get-ProcessingIdentity($endpoint) { return ('1395|009F|Microphone|'+$endpoint.Usb.InstanceId.ToUpperInvariant()) }
function Save-PilotProcessingState {
    # Isolated opt-in store with the baseline values: the real app's state/policy is untouched.
    New-Item -ItemType Directory -Path $stateDirectory | Out-Null
    $deviceIdentity=Get-ProcessingIdentity $before.Endpoint
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { $fileName=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($deviceIdentity))).Replace('-','')+'.json' }
    finally { $algorithm.Dispose() }
    $json=[pscustomobject]@{SchemaVersion=1;DeviceIdentity=$deviceIdentity;SavedAtUtc=[DateTime]::UtcNow.ToString('o');Effects=$before.Effects;RestoreOnConnect=$true} | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Join-Path $stateDirectory $fileName),$json,[Text.UTF8Encoding]::new($false))
}
function Save-Snapshot([string]$name) {
    $path=Join-Path $reportRoot ($name+'.json')
    & dotnet $testDll --dependencies --report $path | Out-Null
    if ($LASTEXITCODE) { throw ('Dependency snapshot failed: '+$name) }
    $snapshot=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $events.Add([pscustomobject]@{Stage=$name;ServiceStatus=(Get-Service EPOSGamingSuiteService).Status.ToString();HostRunning=($null -ne $hostProcess -and !$hostProcess.HasExited);Snapshot=$path})
    return $snapshot
}
function Get-B20($snapshot) {
    $rows=@($snapshot.Endpoints | Where-Object {$_.Endpoint.Direction -eq 'Microphone' -and $_.Endpoint.Usb.VendorId -eq '1395' -and $_.Endpoint.Usb.ProductId -eq '009F'})
    if ($rows.Count -ne 1) { throw 'Exactly one B20 microphone is required.' }
    if (!$rows[0].Effects) { throw ('B20 processing is unavailable: '+$rows[0].EffectsError) }
    return $rows[0]
}
function Start-TestHost {
    $hostArguments=if ($ManagedLifecycle) {
        @('--manage-b20','--initial-state',('"'+$startupPath+'"'),'--state-directory',('"'+$stateDirectory+'"'),'--device-instance',('"'+$before.Endpoint.Usb.InstanceId+'"'),'--report-directory',('"'+$hostReports+'"'),'--stop-file',('"'+$stopFile+'"'),'--seconds','300')
    } elseif ($FreshInitialization) {
        @('--initialize-b20','--initial-state',('"'+$startupPath+'"'),'--report-directory',('"'+$hostReports+'"'),'--stop-file',('"'+$stopFile+'"'),'--seconds','300')
    } else {
        @('--retain-b20','--report-directory',('"'+$hostReports+'"'),'--stop-file',('"'+$stopFile+'"'),'--seconds','300')
    }
    $script:hostProcess=Start-Process -FilePath $hostExe -ArgumentList $hostArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $reportRoot 'host-stdout.txt') -RedirectStandardError (Join-Path $reportRoot 'host-stderr.txt')
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $readyPath=Join-Path $hostReports 'ready.json'
    while (!(Test-Path -LiteralPath $readyPath) -and !$hostProcess.HasExited -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $readyPath) -or $hostProcess.HasExited) { throw ('Host startup failed. See '+(Join-Path $reportRoot 'host-stderr.txt')) }
    $ready=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.ProcessId -ne $hostProcess.Id -or $ready.Endpoint.ProfileIdentity -ne $before.Endpoint.ProfileIdentity) { throw 'Host readiness identity does not match the baseline.' }
    if ($FreshInitialization -and !$ready.CreatedFresh) { throw 'The experiment requires fresh objects; existing objects were not reused.' }
    $script:createdFresh=[bool]$ready.CreatedFresh
    $script:startupRestorePolicy=$ready.StartupRestorePolicy
    if ($ManagedLifecycle) {
        if ($ready.State -ne 'Connected' -or !$ready.AutomaticRestore -or $ready.StartupRestorePolicy -ne 'LastSavedProcessing') { throw 'Managed helper did not restore from its isolated opted-in processing store.' }
        if (($ready.Effects | ConvertTo-Json -Depth 10 -Compress) -ne ($before.Effects | ConvertTo-Json -Depth 10 -Compress)) { throw 'Managed saved-state restore differs from the starting settings.' }
        $script:savedRestoreVerified=$true
    }
    $events.Add([pscustomobject]@{Stage='host-ready';ProcessId=$hostProcess.Id;SettingsWrites=[bool]$FreshInitialization;CreatedFresh=$createdFresh})
}
function Save-StartupInput {
    # Read the full current layout under its mutex. Diagnostic replay into new
    # objects preserves unowned configuration while its fields are still unmapped.
    $name='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f'
    $mutex=[Threading.Mutex]::OpenExisting($name+'_mutex')
    $owned=$false; $map=$null; $view=$null
    try {
        try { $owned=$mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $owned=$true; throw 'Abandoned effects mutex; startup snapshot cancelled.' }
        if (!$owned) { throw 'Effects mutex timeout; startup snapshot cancelled.' }
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'_memory',[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $view=$map.CreateViewAccessor(0,0,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        if ($view.Capacity -ne 4096) { throw 'Unknown startup memory capacity.' }
        $bytes=[byte[]]::new(4096)
        if ($view.ReadArray(0,$bytes,0,$bytes.Length) -ne $bytes.Length) { throw 'Incomplete startup snapshot.' }
    } finally { if($view){$view.Dispose()};if($map){$map.Dispose()};if($owned){$mutex.ReleaseMutex()};$mutex.Dispose() }
    [pscustomobject]@{SchemaVersion=1;ProfileIdentity=$before.Endpoint.ProfileIdentity;DeviceIdentity=(Get-ProcessingIdentity $before.Endpoint);Effects=$before.Effects;DiagnosticSeed=[Convert]::ToBase64String($bytes)} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $startupPath -Encoding UTF8
}
try {
    if ($StopSuiteTemporarily -and !$serviceWasRunning) { throw 'Start the EPOS service before the pilot so its current processing and diagnostic snapshot can be captured.' }
    $baseline=Save-Snapshot 'baseline'
    $before=Get-B20 $baseline
    if ($ManagedLifecycle) { Save-PilotProcessingState }
    if ($FreshInitialization) { Save-StartupInput } else { Start-TestHost }
    if ($StopSuiteTemporarily) {
        $uiClosed=$true
        foreach($process in $suiteProcesses) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $serviceStopAttempted=$true
        Stop-Service EPOSGamingSuiteService -ErrorAction Stop
        (Get-Service EPOSGamingSuiteService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
        if ($FreshInitialization) {
            Save-Snapshot 'service-stopped-before-initialization' | Out-Null
            Start-TestHost
        }
        $stoppedSnapshot=Save-Snapshot 'service-stopped-with-host'
        $stoppedB20=Get-B20 $stoppedSnapshot
        if ($stoppedB20.Endpoint.ProfileIdentity -ne $before.Endpoint.ProfileIdentity) { throw 'B20 identity changed before hardware checks; no test writes attempted.' }
        if ($hostProcess.HasExited) { throw 'Host exited before the service-stopped checks.' }
        if ((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped') { throw 'The vendor service restarted before hardware checks; independence was not tested.' }
        $effectsTestRan=$true
        & dotnet $testDll --hardware-effects 009f --report (Join-Path $reportRoot 'effects-service-stopped.json')
        $effectsExit=$LASTEXITCODE
        $controlOutcome=if ($effectsExit -eq 0) { 'Passed' } else { 'Failed' }
        $events.Add([pscustomobject]@{Stage='effects-service-stopped';ExitCode=$effectsExit})
        if ($effectsExit) { throw 'Service-stopped effect checks failed; see their report.' }
        if ($HardwareAudio) {
            & dotnet $testDll --hardware-audio 009f --report (Join-Path $reportRoot 'audio-service-stopped.json')
            $audioExit=$LASTEXITCODE
            $events.Add([pscustomobject]@{Stage='audio-service-stopped';ExitCode=$audioExit})
            $audioOutcome=if ($audioExit -eq 0) { 'Passed' } elseif ($audioExit -eq 2) { 'Inconclusive' } else { 'Failed' }
            if ($audioExit -eq 2) { $warnings.Add('Audio check inconclusive; steady quiet input is needed.'); Write-Warning $warnings[$warnings.Count-1] }
            elseif ($audioExit) { throw 'Service-stopped audio check failed; see its report.' }
        }
        Get-B20 (Save-Snapshot 'checks-restored-with-host') | Out-Null
        if ((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped') { throw 'The vendor service restarted during the checks; service-stopped independence cannot be claimed.' }
    } else {
        Start-Sleep -Seconds 2
        Get-B20 (Save-Snapshot 'host-retaining-suite-running') | Out-Null
    }
} catch { $errors.Add($_.Exception.Message) }
finally {
    # Restart vendor support while the lease still holds its objects, then release our handles.
    try {
        if ($serviceStopAttempted -and $serviceWasRunning -and (Get-Service EPOSGamingSuiteService).Status -ne 'Running') {
            Start-Service EPOSGamingSuiteService -ErrorAction Stop
            (Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        }
    } catch { $errors.Add('Suite restoration: '+$_.Exception.Message) }
    try {
        if ($uiClosed -and $suiteProcesses.Count -gt 0 -and @(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue).Count -eq 0) {
            Start-Process -FilePath $suitePath -WindowStyle Hidden
        }
    } catch { $errors.Add('Suite UI restoration: '+$_.Exception.Message) }
    try {
        if ($hostProcess -and !$hostProcess.HasExited) {
            [IO.File]::WriteAllText($stopFile,'stop')
            if (!$hostProcess.WaitForExit(5000)) { Stop-Process -Id $hostProcess.Id -ErrorAction Stop; $errors.Add('Host needed forced termination.') }
            elseif ($hostProcess.ExitCode) { $errors.Add('Host exited with an error; see host/stopped.json.') }
        } elseif ($hostProcess) { $errors.Add('Host exited before requested shutdown; see host/stopped.json.') }
        if ($baseline) {
            for($attempt=0; $attempt -le 10; $attempt++) {
                if ($attempt) { Start-Sleep -Seconds 2 }
                $restored=Save-Snapshot ('restored-'+$attempt)
                $after=@($restored.Endpoints | Where-Object {$_.Endpoint.ProfileIdentity -eq $before.Endpoint.ProfileIdentity})
                if ($after.Count -eq 1 -and $after[0].Effects) { break }
            }
            foreach($prior in $baseline.Endpoints) {
                $current=@($restored.Endpoints | Where-Object {$_.Endpoint.ProfileIdentity -eq $prior.Endpoint.ProfileIdentity})
                if ($current.Count -ne 1) { $differences.Add('Endpoint availability differs: '+$prior.Endpoint.Name); continue }
                foreach($field in @('Effects','Sidetone')) {
                    if (($prior.$field | ConvertTo-Json -Depth 10 -Compress) -ne ($current[0].$field | ConvertTo-Json -Depth 10 -Compress)) { $differences.Add($prior.Endpoint.Name+': '+$field+' differs') }
                }
                if (($prior.Endpoint.State | ConvertTo-Json -Compress) -ne ($current[0].Endpoint.State | ConvertTo-Json -Compress)) { $differences.Add($prior.Endpoint.Name+': Windows level/mute differs') }
            }
        }
    } catch { $errors.Add('Host shutdown/recovery verification: '+$_.Exception.Message) }
    $finalServiceStatus=(Get-Service EPOSGamingSuiteService).Status.ToString()
    $finalSuiteUiCount=@(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue).Count
    if ($serviceWasRunning -ne ($finalServiceStatus -eq 'Running')) { $errors.Add('Service running state differs from baseline.') }
    if ($uiClosed -and ($suiteProcesses.Count -gt 0) -ne ($finalSuiteUiCount -gt 0)) { $errors.Add('Suite UI running state differs from baseline.') }
    $overallOutcome=if ($errors.Count -or $differences.Count) { 'Failed' } elseif ($audioOutcome -eq 'Inconclusive') { 'Inconclusive' } else { 'Passed' }
    $summary=[pscustomobject]@{GeneratedUtc=[DateTime]::UtcNow.ToString('o');Passed=($overallOutcome -eq 'Passed');OverallOutcome=$overallOutcome;ControlReadbackOutcome=$controlOutcome;AudioOutcome=$audioOutcome;ServiceStopRequested=[bool]$StopSuiteTemporarily;ServiceStopAttempted=$serviceStopAttempted;ServiceStoppedEffectsAvailable=($null -ne $stoppedSnapshot -and $null -ne ($stoppedSnapshot.Endpoints | Where-Object {$_.Endpoint.Usb.ProductId -eq '009F' -and $_.Effects}));EffectsTestRan=$effectsTestRan;AudioCheckRequested=[bool]$HardwareAudio;HostSettingsWrites=[bool]$FreshInitialization;CreatesMissingObjects=[bool]$FreshInitialization;FreshInitializationRequested=[bool]$FreshInitialization;CreatedFresh=$createdFresh;ManagedLifecycleRequested=[bool]$ManagedLifecycle;AutomaticRestore=$savedRestoreVerified;SavedStateRestoreVerified=$savedRestoreVerified;StartupRestorePolicy=$startupRestorePolicy;ColdStartTested=$false;ReconnectTested=$false;ServiceWasRunning=$serviceWasRunning;FinalServiceStatus=$finalServiceStatus;SuiteUiWasRunning=($suiteProcesses.Count -gt 0);FinalSuiteUiCount=$finalSuiteUiCount;SettingsDifferences=$differences.ToArray();Errors=$errors.ToArray();Warnings=$warnings.ToArray();Stages=$events.ToArray()}
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
    Write-Output ('Host experiment reports: '+$reportRoot)
}
if ($errors.Count -or $differences.Count) { throw (($errors.ToArray()+$differences.ToArray()) -join [Environment]::NewLine) }
if ($overallOutcome -eq 'Inconclusive') {
    $mode=if($ManagedLifecycle){'managed lifecycle'}elseif($FreshInitialization){'fresh initialization'}else{'retention'}
    Write-Output ('Host '+$mode+' and restoration checks passed. Audio validation remains inconclusive; see its report.')
    exit 2
}
$mode=if($ManagedLifecycle){'managed lifecycle'}elseif($FreshInitialization){'fresh initialization'}else{'retention'}
Write-Output ('Host '+$mode+' experiment passed; original settings and Suite startup state were preserved.')
