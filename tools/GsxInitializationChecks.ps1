# Pure option/evidence rules shared by the live runner and fixture-free offline checks.
function Assert-GsxInitializationOptions([bool]$StopSuiteTemporarily, [bool]$HardwareAudio, [bool]$HardwarePlaybackAudio, [bool]$ReconnectGsx, [bool]$RestartAudioEngine) {
    if ($HardwareAudio -and !$StopSuiteTemporarily) { throw '-HardwareAudio requires -StopSuiteTemporarily.' }
    if ($HardwarePlaybackAudio -and !$StopSuiteTemporarily) { throw '-HardwarePlaybackAudio requires -StopSuiteTemporarily.' }
    if ($ReconnectGsx -and !$StopSuiteTemporarily) { throw '-ReconnectGsx requires -StopSuiteTemporarily.' }
    if ($RestartAudioEngine -and (!$StopSuiteTemporarily -or $ReconnectGsx)) { throw '-RestartAudioEngine requires -StopSuiteTemporarily and a separate run from -ReconnectGsx.' }
}

function Get-GsxPlaybackAudioOutcome([int]$ExitCode, $Report, [string]$ExpectedDeviceIdentity) {
    if ($ExitCode -notin @(0,2) -or [string]::IsNullOrWhiteSpace($ExpectedDeviceIdentity) -or $null -eq $Report) { return 'Failed' }
    $expectedOutcome=if ($ExitCode -eq 0) { 'Passed' } else { 'Inconclusive' }
    if ($Report.Outcome -cne $expectedOutcome -or $Report.Report.Outcome -cne $expectedOutcome -or
        $Report.EndpointDeviceIdentity -cne $ExpectedDeviceIdentity -or $Report.ExpectedDeviceIdentity -cne $ExpectedDeviceIdentity -or
        $null -ne $Report.Error -or $null -ne $Report.Report.Error -or @($Report.Report.Phases).Count -ne 3) { return 'Failed' }
    foreach ($flag in @($Report.SuiteStopRequired,$Report.SuiteServiceStoppedValidated,$Report.WindowsControlsPreserved,
        $Report.FullGsxBufferRestoredWhileStreamsOpen,$Report.FullB20BufferPreserved,$Report.Report.SettingsRestored)) {
        if ($flag -isnot [bool] -or !$flag) { return 'Failed' }
    }
    if (($Report.SuiteStoppedChecks -isnot [int] -and $Report.SuiteStoppedChecks -isnot [long]) -or $Report.SuiteStoppedChecks -le 0) { return 'Failed' }
    if ($Report.DspResponseObserved -isnot [bool] -or $Report.DspResponseObserved -ne ($ExitCode -eq 0)) { return 'Failed' }
    return $expectedOutcome
}

function Get-GsxInitializationOutcome([bool]$Prepared, [bool]$StopRequested, [string]$InitializationOutcome,
    [string]$ControlOutcome, [string]$AudioOutcome, [string]$PlaybackAudioOutcome, [bool]$AudioRequested,
    [bool]$PlaybackAudioRequested, [bool]$HasErrors, [bool]$HasDifferences) {
    if ($HasErrors -or $HasDifferences -or $InitializationOutcome -eq 'Failed' -or
        $AudioOutcome -eq 'Failed' -or $PlaybackAudioOutcome -eq 'Failed') { return 'Failed' }
    if ($InitializationOutcome -eq 'BlockedByExistingObjects') { return 'BlockedByExistingObjects' }
    if (!$Prepared -or ($StopRequested -and ($InitializationOutcome -ne 'Passed' -or $ControlOutcome -ne 'Passed'))) { return 'Failed' }
    if ($AudioOutcome -notin @('NotRequested','Passed','Inconclusive') -or $PlaybackAudioOutcome -notin @('NotRequested','Passed','Inconclusive') -or
        ($AudioRequested -and $AudioOutcome -eq 'NotRequested') -or ($PlaybackAudioRequested -and $PlaybackAudioOutcome -eq 'NotRequested')) { return 'Failed' }
    if ($AudioOutcome -eq 'Inconclusive' -or $PlaybackAudioOutcome -eq 'Inconclusive') { return 'Inconclusive' }
    return 'Passed'
}

function Test-GsxVendorRecoveryRequired($Baseline,$Current) {
    $targets=@($Baseline.Controls | Where-Object DeviceIdentity -like 'usb-v1:1395:0098:*')
    if ($targets.Count -ne 2 -or @($targets | Where-Object DeviceIdentity -like '*|Playback').Count -ne 1 -or
        @($targets | Where-Object DeviceIdentity -like '*|Microphone').Count -ne 1 -or
        $targets[0].DeviceIdentity.Split('|')[0] -cne $targets[1].DeviceIdentity.Split('|')[0]) { throw 'Baseline must identify one complete physical GSX pair.' }
    $sound=@($targets | Where-Object DeviceIdentity -like '*|Playback')[0]
    $mic=@($targets | Where-Object DeviceIdentity -like '*|Microphone')[0]
    if ($sound.Playback.Status -ne 'Available' -or $mic.Microphone.Status -ne 'Available') { throw 'The saved GSX starting processing must be readable.' }
    $rows=@($Current.Controls | Where-Object DeviceIdentity -like 'usb-v1:1395:0098:*')
    if ($rows.Count -ne 2 -or @($targets | Where-Object { $_.DeviceIdentity -cnotin $rows.DeviceIdentity }).Count -or
        @($rows | Where-Object { $_.Audio.Status -ne 'Available' }).Count) { throw 'The live GSX pair is missing, ambiguous, disconnected or differs from the baseline.' }
    $sound=@($rows | Where-Object DeviceIdentity -like '*|Playback')[0]
    $mic=@($rows | Where-Object DeviceIdentity -like '*|Microphone')[0]
    if ($sound.Playback.Status -eq 'Available' -and $mic.Microphone.Status -eq 'Available') { return $false }
    if ($sound.Playback.Status -ne 'Unavailable' -or $mic.Microphone.Status -ne 'Unavailable' -or
        $sound.Playback.ErrorType -ne 'WaitHandleCannotBeOpenedException' -or $mic.Microphone.ErrorType -ne 'WaitHandleCannotBeOpenedException') {
        throw 'Recovery only handles the missing GSX object set; other failures require diagnosis.'
    }
    return $true
}

function Test-ApoVendorHandoffReady($Owners,[int]$ServiceProcessId,[string]$ProductId='0098') {
    if ($ProductId -notin @('0098','009f')) { throw 'Only mapped EPOS product namespaces may be inspected.' }
    if ($ServiceProcessId -le 0) { return $false }
    $prefix='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$ProductId
    foreach ($suffix in @('memory','mutex','event')) {
        $rows=@($Owners | Where-Object { $_.ProcessId -eq $ServiceProcessId -and $_.ProcessName -eq 'EPOSGamingSuiteService' -and
            $_.ObjectName -ceq ($prefix+'_'+$suffix) -and $_.HandleCount -gt 0 })
        if ($rows.Count -ne 1) { return $false }
    }
    return $true
}
