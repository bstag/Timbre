# Pure option/evidence rules shared by the live runner and fixture-free offline checks.
function Assert-GsxPilotTarget([string]$Expected,[string]$Actual) {
    if (![string]::IsNullOrWhiteSpace($Expected) -and $Expected -ine $Actual) { throw 'The selected physical GSX changed before test preparation. No services were changed.' }
}
function Get-GsxPilotReportDirectory([string]$Root,[string]$Requested) {
    $parent=[IO.Path]::GetFullPath((Join-Path $Root 'artifacts\gsx-initialization'))
    if ([string]::IsNullOrWhiteSpace($Requested)) { return Join-Path $parent ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
    $path=[IO.Path]::GetFullPath($Requested)
    if (!$path.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $path)) {
        throw 'The explicit pilot report directory must be new and inside artifacts\gsx-initialization. No services were changed.'
    }
    return $path
}
function Assert-GsxInitializationOptions([bool]$StopSuiteTemporarily, [bool]$HardwareAudio, [bool]$HardwarePlaybackAudio, [bool]$ReconnectGsx, [bool]$RestartAudioEngine, [bool]$ManagedLifecycle=$false, [bool]$ManagedReconnect=$false) {
    if ($ManagedReconnect -and !$ManagedLifecycle) { throw '-ManagedReconnect requires -ManagedLifecycle. Add -StopSuiteTemporarily only for the physical administrator test.' }
    if ($HardwareAudio -and !$StopSuiteTemporarily) { throw '-HardwareAudio requires -StopSuiteTemporarily.' }
    if ($HardwarePlaybackAudio -and !$StopSuiteTemporarily) { throw '-HardwarePlaybackAudio requires -StopSuiteTemporarily.' }
    if ($ReconnectGsx -and !$StopSuiteTemporarily) { throw '-ReconnectGsx requires -StopSuiteTemporarily.' }
    if ($RestartAudioEngine -and (!$StopSuiteTemporarily -or $ReconnectGsx)) { throw '-RestartAudioEngine requires -StopSuiteTemporarily and a separate run from -ReconnectGsx.' }
    if ($ManagedLifecycle -and ($RestartAudioEngine -or $ReconnectGsx -or $HardwareAudio -or $HardwarePlaybackAudio)) {
        throw 'The managed saved-state pilot is separate from audio-engine restart, physical reconnect and audio measurements.'
    }
}

function Get-GsxProcessingIdentity($Microphone,$Playback) {
    if (!$Microphone.Usb -or !$Playback.Usb -or $Microphone.Direction -ne 1 -or $Playback.Direction -ne 0 -or
        $Microphone.Usb.VendorId -ine '1395' -or $Microphone.Usb.ProductId -ine '0098' -or
        $Playback.Usb.VendorId -ine '1395' -or $Playback.Usb.ProductId -ine '0098' -or
        [string]::IsNullOrWhiteSpace($Microphone.Usb.InstanceId) -or $Microphone.Usb.InstanceId -ine $Playback.Usb.InstanceId -or
        [string]::IsNullOrWhiteSpace($Microphone.Id) -or [string]::IsNullOrWhiteSpace($Playback.Id) -or $Microphone.Id -ceq $Playback.Id) {
        throw 'Managed processing requires one matching physical GSX microphone/playback pair.'
    }
    return '1395|0098|Processing|'+$Microphone.Usb.InstanceId.ToUpperInvariant()
}

function Assert-GsxManagedReportIdentity($Report,[int]$ProcessId,$Microphone,$Playback) {
    $identity=Get-GsxProcessingIdentity $Microphone $Playback
    if ($ProcessId -le 0 -or $Report.ProcessId -ne $ProcessId -or
        $Report.Endpoint.Id -cne $Microphone.Id -or $Report.PlaybackEndpoint.Id -cne $Playback.Id -or
        (Get-GsxProcessingIdentity $Report.Endpoint $Report.PlaybackEndpoint) -cne $identity) {
        throw 'Managed report process or physical endpoint identity differs from the baseline.'
    }
}

function Assert-GsxManagedValues($Effects,$MicrophoneValue,$PlaybackValue) {
    if (!$Effects.Microphone.Processing -or !$Effects.Playback.Equalizer -or !$Effects.Playback.Reverb -or
        ($Effects.Microphone | ConvertTo-Json -Depth 12 -Compress) -cne ($MicrophoneValue | ConvertTo-Json -Depth 12 -Compress) -or
        ($Effects.Playback | ConvertTo-Json -Depth 12 -Compress) -cne ($PlaybackValue | ConvertTo-Json -Depth 12 -Compress)) {
        throw 'Managed paired processing differs from the complete baseline.'
    }
}

function Assert-GsxManagedValidation($Report,[int]$ProcessId,$Microphone,$Playback,$MicrophoneValue,$PlaybackValue) {
    Assert-GsxManagedReportIdentity $Report $ProcessId $Microphone $Playback
    if ($Report.Mode -cne 'ValidateManagedGsxStartup') { throw 'The helper did not validate managed GSX input.' }
    foreach ($flag in @($Report.SettingsWrites,$Report.CreatesMissingObjects,$Report.CreatedFresh,$Report.DiscoveryActivatesAudioClients)) {
        if ($flag -isnot [bool] -or $flag) { throw 'Managed preparation did not prove a read-only validation path.' }
    }
    if ($Report.SavedProcessing.RestoreOnConnect -isnot [bool] -or !$Report.SavedProcessing.RestoreOnConnect -or
        $Report.SavedProcessing.DeviceIdentity -cne (Get-GsxProcessingIdentity $Microphone $Playback)) {
        throw 'The isolated saved record does not have the expected physical identity and opt-in policy.'
    }
    Assert-GsxManagedValues $Report.SavedProcessing.Effects $MicrophoneValue $PlaybackValue
    Assert-GsxManagedValues ([pscustomobject]@{Microphone=$Report.Effects;Playback=$Report.Playback}) $MicrophoneValue $PlaybackValue
}

function Assert-GsxManagedReadiness($Report,[int]$ProcessId,$Microphone,$Playback,$MicrophoneValue,$PlaybackValue,[int]$Generation=1) {
    Assert-GsxManagedReportIdentity $Report $ProcessId $Microphone $Playback
    if ($Generation -lt 1 -or $Report.Mode -cne 'ManageGsxLifecycle' -or $Report.State -cne 'Connected' -or $Report.Generation -ne $Generation -or
        ($Report.Generation -isnot [int] -and $Report.Generation -isnot [long]) -or $null -ne $Report.Error -or
        $Report.StartupRestorePolicy -cne 'LastSavedProcessing' -or $Report.CreatedFresh -isnot [bool]) {
        throw 'The managed helper did not report the expected saved-state connection generation.'
    }
    foreach ($flag in @($Report.SettingsWrites,$Report.AutomaticRestore)) {
        if ($flag -isnot [bool] -or !$flag) { throw 'Managed readiness did not verify the opted-in restoration path.' }
    }
    Assert-GsxManagedValues $Report.Effects $MicrophoneValue $PlaybackValue
}

function Assert-GsxManagedWaiting($Report,[int]$ProcessId,[int]$Generation) {
    if ($ProcessId -le 0 -or $Generation -lt 1 -or $Report.ProcessId -ne $ProcessId -or
        $Report.Mode -cne 'ManageGsxLifecycle' -or $Report.State -cne 'WaitingForDevice' -or
        ($Report.Generation -isnot [int] -and $Report.Generation -isnot [long]) -or $Report.Generation -ne $Generation -or
        $null -ne $Report.Endpoint -or $null -ne $Report.PlaybackEndpoint -or $null -ne $Report.Effects -or $null -ne $Report.Error) {
        throw 'The managed helper did not observe removal and release its endpoint pair.'
    }
    foreach ($flag in @($Report.SettingsWrites,$Report.AutomaticRestore,$Report.CreatedFresh)) {
        if ($flag -isnot [bool] -or $flag) { throw 'Managed waiting report contains missing or writable connection evidence.' }
    }
}

function Get-GsxManagedReconnectOutcome([string]$BaseOutcome,[bool]$Requested,[bool]$StopRequested,
    $RemovalObserved,$WaitingObserved,$ReturnObserved,$RestoreVerified) {
    if ($BaseOutcome -cne 'Passed') { return 'Failed' }
    if (!$Requested) { return $BaseOutcome }
    foreach ($flag in @($RemovalObserved,$WaitingObserved,$ReturnObserved,$RestoreVerified)) {
        if ($flag -isnot [bool] -or $flag -ne $StopRequested) { return 'Failed' }
    }
    return 'Passed'
}

function Get-GsxManagedOutcome([bool]$Prepared,[bool]$StopRequested,[string]$ManagedOutcome,[bool]$RestoreVerified,
    [string]$ControlOutcome,$Handoff,$GsxWhileHeld,$GsxAfterRelease,[bool]$HasB20,$B20WhileHeld,$B20AfterRelease,
    $SavedStateUnchanged,[bool]$HasErrors,[bool]$HasDifferences) {
    if (!$Prepared -or $HasErrors -or $HasDifferences -or $SavedStateUnchanged -isnot [bool] -or !$SavedStateUnchanged) { return 'Failed' }
    if (!$StopRequested) {
        if ($ManagedOutcome -cne 'NotRequested' -or $RestoreVerified -or $ControlOutcome -cne 'NotRun') { return 'Failed' }
        return 'Passed'
    }
    if ($ManagedOutcome -cne 'Passed' -or !$RestoreVerified -or $ControlOutcome -cne 'Passed') { return 'Failed' }
    $flags=@($Handoff,$GsxWhileHeld,$GsxAfterRelease)
    if ($HasB20) { $flags+=@($B20WhileHeld,$B20AfterRelease) }
    foreach ($flag in $flags) { if ($flag -isnot [bool] -or !$flag) { return 'Failed' } }
    return 'Passed'
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
