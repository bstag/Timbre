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
