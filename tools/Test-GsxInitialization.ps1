[CmdletBinding()]
param([switch]$StopSuiteTemporarily, [switch]$HardwareAudio, [switch]$HardwarePlaybackAudio, [switch]$ReconnectGsx, [switch]$RestartAudioEngine)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GsxInitializationChecks.ps1')
Assert-GsxInitializationOptions ([bool]$StopSuiteTemporarily) ([bool]$HardwareAudio) ([bool]$HardwarePlaybackAudio) ([bool]$ReconnectGsx) ([bool]$RestartAudioEngine)
if ($StopSuiteTemporarily) {
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the temporary service-stop test in administrator PowerShell. No changes were made.'
    }
}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ApoRecoveryChecks.ps1')
$hostExe=Join-Path $root 'dist\host\Timbre.Host.exe'
$appExe=Join-Path $root 'dist\Timbre.exe'
$testDll=Join-Path $root 'tests\Timbre.Tests\bin\Release\net9.0\Timbre.Tests.dll'
foreach ($path in @($hostExe,$appExe,$testDll)) { if (!(Test-Path -LiteralPath $path)) { throw 'Build the app, host and tests first.' } }
if (@(Get-Process Timbre.Host,EposControl.Host -ErrorAction SilentlyContinue).Count) { throw 'Close the existing experimental helper before this test.' }
$service=Get-Service EPOSGamingSuiteService
if ($service.Status -ne 'Running') { throw 'Start the vendor service first to capture the current GSX settings.' }
$audioService=Get-Service Audiosrv
if ($RestartAudioEngine -and ($audioService.Status -ne 'Running' -or @($audioService.DependentServices | Where-Object Status -eq 'Running').Count)) {
    throw 'Windows Audio must be Running with no running dependent services for the bounded restart test. No services changed.'
}
$reportRoot=Join-Path $root ('artifacts\gsx-initialization\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$stopFile=Join-Path $reportRoot 'stop-hosts'
$startupPath=Join-Path $reportRoot 'startup-state.json'
$processes=[Collections.Generic.List[object]]::new()
$errors=[Collections.Generic.List[string]]::new()
$warnings=[Collections.Generic.List[string]]::new()
$differences=[Collections.Generic.List[string]]::new()
$events=[Collections.Generic.List[object]]::new()
$baseline=$null; $ready=$null; $hostProcess=$null; $serviceStopAttempted=$false; $uiClosed=$false
$suiteProcesses=@(Get-Process EPOSGamingSuite -ErrorAction SilentlyContinue)
$suitePath='C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuite.exe'
$initializationOutcome='NotRequested'; $audioOutcome='NotRequested'; $playbackAudioOutcome='NotRequested'; $controlOutcome='NotRun'
$fullGsxRestored=$null; $fullB20Preserved=$null; $prepared=$false
$disconnectObserved=$false; $returnObserved=$false
$audioStopAttempted=$false; $audioRestartVerified=$false
$gsxPreparedEntry=$null
function Save-Diagnostics([string]$name) {
    $path=Join-Path $reportRoot ($name+'.json')
    $process=Start-Process -FilePath $appExe -ArgumentList @('--diagnostics',('"'+$path+'"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $path)) { throw ('Diagnostic failed: '+$name) }
    $events.Add([pscustomobject]@{Stage=$name;ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString();Path=$path})
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}
function Read-Apo([string]$product) {
    $name='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$product
    $mutex=[Threading.Mutex]::OpenExisting($name+'_mutex')
    $owned=$false; $map=$null; $view=$null
    try {
        try { $owned=$mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $owned=$true; throw 'Abandoned effects mutex; capture refused.' }
        if (!$owned) { throw 'Effects mutex timeout.' }
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'_memory',[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $view=$map.CreateViewAccessor(0,0,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        if ($view.Capacity -ne 4096) { throw 'Unknown effects memory capacity.' }
        $bytes=[byte[]]::new(4096)
        if ($view.ReadArray(0,$bytes,0,$bytes.Length) -ne $bytes.Length) { throw 'Incomplete effects capture.' }
        return ,$bytes
    } finally { if($view){$view.Dispose()};if($map){$map.Dispose()};if($owned){$mutex.ReleaseMutex()};$mutex.Dispose() }
}
function Restore-WindowsAudio([string]$name) {
    # Audio-engine restart may reset hardware gain independently of APO memory.
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        $captured=Save-Diagnostics ($name+'-'+$attempt)
        if (Test-WindowsAudioRecoveryReady $baseline $captured) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!(Test-WindowsAudioRecoveryReady $baseline $captured)) { throw 'Audio endpoints did not become readable for level/mute restoration.' }
    & dotnet $testDll --restore-audio-state (Join-Path $reportRoot 'baseline.json') --expected-audio-state (Join-Path $reportRoot ($name+'-'+$attempt+'.json')) --report (Join-Path $reportRoot ($name+'-levels-restored.json'))
    if ($LASTEXITCODE) { throw 'Captured Windows audio level/mute restoration failed.' }
}
function Hash-Bytes([byte[]]$bytes) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') } finally { $algorithm.Dispose() }
}
function Start-Helper([string]$mode,[string]$name) {
    $directory=Join-Path $reportRoot $name
    $duration=if($ReconnectGsx -or $RestartAudioEngine){'600'}else{'300'}
    $arguments=@($mode,'--report-directory',('"'+$directory+'"'),'--stop-file',('"'+$stopFile+'"'),'--seconds',$duration)
    if ($mode -ne '--retain-b20') { $arguments+=@('--initial-state',('"'+$startupPath+'"')) }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo.FileName=$hostExe; $process.StartInfo.Arguments=($arguments -join ' ')
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
    if (!$process.Start()) { throw ('Helper launch failed: '+$name) }
    $entry=[pscustomobject]@{Name=$name;Process=$process;Directory=$directory}
    $processes.Add($entry); return $entry
}
function Wait-Ready($entry,[string]$file='ready.json') {
    $path=Join-Path $entry.Directory $file; $watch=[Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $path) -and !$entry.Process.HasExited -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $path) -or ($file -ne 'validated.json' -and $entry.Process.HasExited)) {
        $stoppedPath=Join-Path $entry.Directory 'stopped.json'
        if (Test-Path -LiteralPath $stoppedPath) {
            $stopped=Get-Content -LiteralPath $stoppedPath -Raw | ConvertFrom-Json
            if ($stopped.Error -match 'Existing effects objects are still present|Partial effects objects|appeared during startup') {
                $script:initializationOutcome='BlockedByExistingObjects'
                throw 'Fresh GSX initialization was refused because native objects are still retained. No existing memory was overwritten.'
            }
            throw ('Helper refused startup: '+$stopped.Error)
        }
        throw ('Helper did not become ready: '+$entry.Name)
    }
    $value=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($value.ProcessId -ne $entry.Process.Id) { throw 'Readiness process identity differs.' }
    return $value
}
function Require-ServiceStopped {
    if ((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped') { throw 'Vendor service restarted; independent initialization was not tested.' }
    if ($hostProcess -and $hostProcess.HasExited) { throw 'GSX helper exited before the control checks finished.' }
}
function Wait-GsxConnection([bool]$connected) {
    $watch=[Diagnostics.Stopwatch]::StartNew(); $attempt=0
    while ($watch.Elapsed.TotalSeconds -lt 60) {
        Require-ServiceStopped
        $snapshot=Save-Diagnostics ('reconnect-poll-'+$connected+'-'+$attempt)
        if (Test-GsxConnectionState $snapshot $mic.Usb.InstanceId $connected) {
            $snapshot | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $reportRoot $(if($connected){'gsx-return-observed.json'}else{'gsx-disconnect-observed.json'})) -Encoding UTF8
            return
        }
        $attempt++; Start-Sleep -Milliseconds 500
    }
    throw ('Timed out waiting for GSX '+$(if($connected){'reconnection'}else{'removal'})+'. Vendor support will be restored.')
}
try {
    $baseline=Save-Diagnostics 'baseline'
    $mics=@($baseline.Endpoints | Where-Object {$_.Direction -eq 1 -and $_.Usb.VendorId -eq '1395' -and $_.Usb.ProductId -eq '0098'})
    $outputs=@($baseline.Endpoints | Where-Object {$_.Direction -eq 0 -and $_.Usb.VendorId -eq '1395' -and $_.Usb.ProductId -eq '0098'})
    if ($mics.Count -ne 1 -or $outputs.Count -ne 1 -or $mics[0].Usb.InstanceId -ne $outputs[0].Usb.InstanceId) { throw 'Exactly one physical GSX microphone/sound pair is required.' }
    $mic=$mics[0]; $output=$outputs[0]
    $micControl=@($baseline.Controls | Where-Object EndpointId -eq $mic.Id)[0]
    $soundControl=@($baseline.Controls | Where-Object EndpointId -eq $output.Id)[0]
    if ($micControl.Microphone.Status -ne 'Available' -or $soundControl.Playback.Status -ne 'Available') { throw 'Current GSX processing must be readable before testing.' }
    $gsxBefore=Read-Apo '0098'; [IO.File]::WriteAllBytes((Join-Path $reportRoot 'gsx-before.bin'),$gsxBefore)
    $b20Before=$null
    if (@($baseline.Endpoints | Where-Object {$_.Direction -eq 1 -and $_.Usb.VendorId -eq '1395' -and $_.Usb.ProductId -eq '009F'}).Count) {
        $b20Before=Read-Apo '009f'; [IO.File]::WriteAllBytes((Join-Path $reportRoot 'b20-before.bin'),$b20Before)
    }
    $startup=[ordered]@{SchemaVersion=1;DeviceInstance=$mic.Usb.InstanceId;MicrophoneEndpointId=$mic.Id;PlaybackEndpointId=$output.Id;Microphone=$micControl.Microphone.Value;Playback=$soundControl.Playback.Value;DiagnosticSeed=[Convert]::ToBase64String($gsxBefore)}
    [IO.File]::WriteAllText($startupPath,($startup | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $validation=Start-Helper '--validate-gsx' 'validate'
    $validated=Wait-Ready $validation 'validated.json'
    if (!$validation.Process.WaitForExit(5000) -or $validation.Process.ExitCode -ne 0 -or $validated.SettingsWrites -or $validated.CreatesMissingObjects) { throw 'Read-only GSX startup validation failed.' }
    $prepared=$true
    if ($StopSuiteTemporarily) {
        if ($b20Before) { $retained=Start-Helper '--retain-b20' 'retain-b20'; Wait-Ready $retained | Out-Null }
        if ($RestartAudioEngine) {
            $gsxPreparedEntry=Start-Helper '--initialize-gsx-paused' 'initialize'
            $preparedPair=Wait-Ready $gsxPreparedEntry 'prepared.json'
            if ($preparedPair.SettingsWrites -or $preparedPair.CreatedFresh -or !$preparedPair.WaitingForExplicitInitializationRequest -or
                $preparedPair.Endpoint.Id -ne $mic.Id -or $preparedPair.PlaybackEndpoint.Id -ne $output.Id -or
                $preparedPair.Endpoint.Usb.InstanceId -ne $mic.Usb.InstanceId) { throw 'GSX live preparation did not match the saved endpoint pair.' }
            $events.Add([pscustomobject]@{Stage='gsx-live-pair-prepared';ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString();AudioServiceState=(Get-Service Audiosrv).Status.ToString();ProcessId=$gsxPreparedEntry.Process.Id})
        }
        $uiClosed=$true
        foreach ($process in $suiteProcesses) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $serviceStopAttempted=$true
        Stop-Service EPOSGamingSuiteService -ErrorAction Stop
        (Get-Service EPOSGamingSuiteService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
        if ($RestartAudioEngine) {
            Write-Host 'Briefly stopping Windows Audio to release retained GSX objects. All PC audio is interrupted during this step.'
            $audioStopAttempted=$true
            Stop-Service Audiosrv -ErrorAction Stop
            (Get-Service Audiosrv).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
            # Do not activate audio clients via normal diagnostics while preparing fresh creation.
            $watch=[Diagnostics.Stopwatch]::StartNew()
            do {
                $remaining=$null
                try { $remaining=[Threading.Mutex]::OpenExisting('Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_0098_mutex') }
                catch [Threading.WaitHandleCannotBeOpenedException] { break }
                finally { if($remaining){$remaining.Dispose()} }
                Start-Sleep -Milliseconds 100
            } while($watch.Elapsed.TotalSeconds -lt 5)
            $events.Add([pscustomobject]@{Stage='audio-engine-stopped';ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString();AudioServiceState=(Get-Service Audiosrv).Status.ToString()})
        } else { Save-Diagnostics 'service-stopped-before-initialization' | Out-Null }
        if ($ReconnectGsx) {
            Write-Host 'Unplug only the GSX 300 USB cable now. Leave B20 connected. You have 60 seconds.'
            Wait-GsxConnection $false; $disconnectObserved=$true
            Write-Host 'GSX removal observed. Reconnect the GSX 300 USB cable now. You have 60 seconds.'
            Wait-GsxConnection $true; $returnObserved=$true
            Write-Host 'Same physical GSX returned. Attempting fresh initialization; avoid changing controls.'
        }
        $initializationOutcome='Failed'
        if ($RestartAudioEngine) {
            $entry=$gsxPreparedEntry
            if ($entry.Process.HasExited -or (Get-Service Audiosrv).Status -ne 'Stopped') { throw 'Prepared GSX helper or paused Windows Audio is unavailable.' }
            $request=[ordered]@{ProcessId=$entry.Process.Id;DeviceInstance=$mic.Usb.InstanceId}
            $requestPath=Join-Path $entry.Directory 'initialize-request.json'
            [IO.File]::WriteAllText(($requestPath+'.tmp'),($request | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath ($requestPath+'.tmp') -Destination $requestPath
        } else { $entry=Start-Helper '--initialize-gsx' 'initialize' }
        $hostProcess=$entry.Process; $ready=Wait-Ready $entry
        Require-ServiceStopped
        if (!$ready.CreatedFresh -or $ready.Endpoint.Usb.InstanceId -ne $mic.Usb.InstanceId -or $ready.PlaybackEndpoint.Usb.InstanceId -ne $mic.Usb.InstanceId) { throw 'Fresh GSX readiness identity is invalid.' }
        $initializationOutcome='Passed'
        if ((Hash-Bytes (Read-Apo '0098')) -ne (Hash-Bytes $gsxBefore)) { throw 'Fresh GSX memory differs from the complete starting snapshot.' }
        if ($RestartAudioEngine) {
            Start-Service Audiosrv -ErrorAction Stop
            (Get-Service Audiosrv).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
            $audioRestartVerified=$true
            Restore-WindowsAudio 'audio-engine-restarted-with-fresh-host'
        }
        & dotnet $testDll --hardware-effects 0098 --report (Join-Path $reportRoot 'microphone-service-stopped.json')
        if ($LASTEXITCODE) { throw 'GSX microphone control/restoration check failed.' }
        Require-ServiceStopped
        & dotnet $testDll --hardware-playback 0098 --report (Join-Path $reportRoot 'playback-service-stopped.json')
        if ($LASTEXITCODE) { throw 'GSX playback control/restoration check failed.' }
        Require-ServiceStopped; $controlOutcome='Passed'
        if ($HardwareAudio) {
            & dotnet $testDll --hardware-audio 0098 --report (Join-Path $reportRoot 'audio-service-stopped.json')
            $audioOutcome=if($LASTEXITCODE -eq 0){'Passed'}elseif($LASTEXITCODE -eq 2){'Inconclusive'}else{'Failed'}
            if ($audioOutcome -eq 'Failed') { throw 'GSX gate audio check failed.' }
            Require-ServiceStopped
        }
        if ($HardwarePlaybackAudio) {
            $playbackAudioOutcome='Failed'
            Require-ServiceStopped
            $events.Add([pscustomobject]@{Stage='playback-audio-start';ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString();ProcessId=$hostProcess.Id;DeviceIdentity=$soundControl.DeviceIdentity})
            $playbackAudioPath=Join-Path $reportRoot 'playback-audio-service-stopped.json'
            & dotnet $testDll --hardware-playback-audio --expected-device-identity $soundControl.DeviceIdentity --require-suite-stopped --report $playbackAudioPath
            $playbackAudioExit=$LASTEXITCODE
            Require-ServiceStopped
            $playbackAudio=Get-Content -LiteralPath $playbackAudioPath -Raw | ConvertFrom-Json
            $playbackAudioOutcome=Get-GsxPlaybackAudioOutcome $playbackAudioExit $playbackAudio $soundControl.DeviceIdentity
            $events.Add([pscustomobject]@{Stage='playback-audio-finished';ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString();ProcessId=$hostProcess.Id;Outcome=$playbackAudioOutcome;Path=$playbackAudioPath})
            if ($playbackAudioOutcome -eq 'Failed') { throw 'GSX playback audio/context/restoration check failed; inspect its report.' }
        }
        $gsxAfter=Read-Apo '0098'; [IO.File]::WriteAllBytes((Join-Path $reportRoot 'gsx-checks-restored.bin'),$gsxAfter)
        $fullGsxRestored=(Hash-Bytes $gsxAfter) -eq (Hash-Bytes $gsxBefore)
        if (!$fullGsxRestored) { throw 'Complete GSX starting memory was not restored after control checks.' }
        if ($b20Before) {
            $b20After=Read-Apo '009f'; [IO.File]::WriteAllBytes((Join-Path $reportRoot 'b20-during-gsx-restored.bin'),$b20After)
            $fullB20Preserved=(Hash-Bytes $b20After) -eq (Hash-Bytes $b20Before)
            if (!$fullB20Preserved) { throw 'B20 memory changed during the GSX test.' }
        }
        Save-Diagnostics 'checks-restored-with-host' | Out-Null
    }
} catch {
    if ($initializationOutcome -eq 'BlockedByExistingObjects') { $warnings.Add($_.Exception.Message) }
    else { $errors.Add($_.Exception.Message) }
}
finally {
    try {
        if ($audioStopAttempted -and (Get-Service Audiosrv).Status -ne 'Running') {
            Start-Service Audiosrv -ErrorAction Stop
            (Get-Service Audiosrv).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        }
        if ($audioStopAttempted -and (Get-Service Audiosrv).Status -eq 'Running') { Restore-WindowsAudio 'audio-engine-recovery' }
    } catch { $errors.Add('Windows Audio restoration: '+$_.Exception.Message) }
    try {
        if ($serviceStopAttempted -and (Get-Service EPOSGamingSuiteService).Status -ne 'Running') {
            Start-Service EPOSGamingSuiteService -ErrorAction Stop
            (Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        }
    } catch { $errors.Add('Service restoration: '+$_.Exception.Message) }
    try {
        if ($uiClosed -and $suiteProcesses.Count -gt 0 -and @(Get-Process EPOSGamingSuite -ErrorAction SilentlyContinue).Count -eq 0) {
            Start-Process -FilePath $suitePath -WindowStyle Hidden
        }
    } catch { $errors.Add('Suite UI restoration: '+$_.Exception.Message) }
    [IO.File]::WriteAllText($stopFile,'stop')
    foreach ($entry in $processes) {
        try {
            if (!$entry.Process.HasExited -and !$entry.Process.WaitForExit(5000)) {
                Stop-Process -Id $entry.Process.Id -ErrorAction Stop; $errors.Add('Helper needed forced termination: '+$entry.Name)
            }
            if ($entry.Process.HasExited) {
                [IO.File]::WriteAllText((Join-Path $reportRoot ($entry.Name+'-stdout.txt')),$entry.Process.StandardOutput.ReadToEnd())
                [IO.File]::WriteAllText((Join-Path $reportRoot ($entry.Name+'-stderr.txt')),$entry.Process.StandardError.ReadToEnd())
                if ($entry.Process.ExitCode -ne 0 -and !($entry.Name -eq 'initialize' -and $initializationOutcome -eq 'BlockedByExistingObjects')) {
                    $errors.Add('Helper reported failure: '+$entry.Name)
                }
            }
        } catch { $errors.Add('Helper shutdown: '+$_.Exception.Message) }
    }
    try {
        if ($baseline) {
            for ($attempt=0; $attempt -lt 40; $attempt++) {
                if ($attempt) { Start-Sleep -Milliseconds 500 }
                $restored=Save-Diagnostics ('restored-'+$attempt)
                if (Test-ApoRecoveryReady $baseline $restored) { break }
            }
            foreach ($prior in $baseline.Controls) {
                $current=@($restored.Controls | Where-Object DeviceIdentity -eq $prior.DeviceIdentity)
                if ($current.Count -ne 1) { $differences.Add('Endpoint identity unavailable: '+$prior.DeviceIdentity); continue }
                foreach ($field in @('Audio','Microphone','Playback','Sidetone')) {
                    if (($prior.$field | ConvertTo-Json -Depth 12 -Compress) -ne ($current[0].$field | ConvertTo-Json -Depth 12 -Compress)) {
                        $differences.Add($prior.DeviceIdentity+': '+$field+' differs')
                    }
                }
            }
        }
    } catch { $errors.Add('Recovery verification: '+$_.Exception.Message) }
    $finalService=(Get-Service EPOSGamingSuiteService).Status.ToString()
    if ($finalService -ne 'Running') { $errors.Add('Vendor service was not restored to Running.') }
    if ($audioStopAttempted -and (Get-Service Audiosrv).Status -ne 'Running') { $errors.Add('Windows Audio was not restored to Running.') }
    if ($uiClosed -and ($suiteProcesses.Count -gt 0) -ne (@(Get-Process EPOSGamingSuite -ErrorAction SilentlyContinue).Count -gt 0)) { $errors.Add('Suite UI startup state differs.') }
    $outcome=Get-GsxInitializationOutcome $prepared ([bool]$StopSuiteTemporarily) $initializationOutcome $controlOutcome $audioOutcome $playbackAudioOutcome ([bool]$HardwareAudio) ([bool]$HardwarePlaybackAudio) ($errors.Count -gt 0) ($differences.Count -gt 0)
    $summary=[ordered]@{CollectedAtUtc=[DateTime]::UtcNow.ToString('o');Outcome=$outcome;Prepared=$prepared;ServiceStopRequested=[bool]$StopSuiteTemporarily;ServiceStopAttempted=$serviceStopAttempted;InitializationOutcome=$initializationOutcome;CreatedFresh=($null -ne $ready -and $ready.CreatedFresh);ControlOutcome=$controlOutcome;AudioOutcome=$audioOutcome;PlaybackAudioRequested=[bool]$HardwarePlaybackAudio;PlaybackAudioOutcome=$playbackAudioOutcome;FullGsxBufferRestored=$fullGsxRestored;FullB20BufferPreserved=$fullB20Preserved;FinalServiceState=$finalService;AudioEngineRestartRequested=[bool]$RestartAudioEngine;AudioEngineStopAttempted=$audioStopAttempted;AudioEngineRestartedWithFreshHost=$audioRestartVerified;FinalAudioServiceState=(Get-Service Audiosrv).Status.ToString();SettingsDifferences=$differences.ToArray();Errors=$errors.ToArray();Warnings=$warnings.ToArray();ColdStartTested=$false;ReconnectRequested=[bool]$ReconnectGsx;DisconnectObserved=$disconnectObserved;ReturnObserved=$returnObserved;PhysicalReconnectObserved=($disconnectObserved -and $returnObserved);ReconnectTested=$false;ManagedReconnectRecoveryTested=$false;AutomaticRestore=$false;StartupPolicy='Explicit current diagnostic snapshot only';Stages=$events.ToArray()}
    $summary | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $reportRoot 'summary.json') -Encoding UTF8
    Write-Output ('GSX initialization reports: '+$reportRoot)
}
if ($outcome -eq 'Failed') { throw (($errors.ToArray()+$differences.ToArray()) -join [Environment]::NewLine) }
if ($outcome -eq 'BlockedByExistingObjects') {
    if ($ReconnectGsx) { Write-Warning 'GSX objects survived the observed reconnect. Fresh initialization was safely refused; service and UI were restored. Inspect retained object owners before another test.' }
    else { Write-Warning 'GSX objects remain retained by a warm audio process. Fresh initialization was safely refused; service and UI were restored. Physical reconnect is a separate next step.' }
    exit 3
}
if ($outcome -eq 'Inconclusive') { Write-Warning ('Initialization/control/restoration passed; microphone audio: '+$audioOutcome+'; playback audio: '+$playbackAudioOutcome+'. See the individual reports.'); exit 2 }
if ($StopSuiteTemporarily) { Write-Output 'GSX fresh initialization, microphone/playback controls and recovery passed.' }
else { Write-Output 'Read-only GSX preparation passed. No service changes, vendor-object creation or settings writes.' }
