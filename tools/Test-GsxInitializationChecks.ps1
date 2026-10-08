[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath=Join-Path $PSScriptRoot '../artifacts/gsx-initialization-checks.json' }
. (Join-Path $PSScriptRoot 'GsxInitializationChecks.ps1')
# Synthetic reports only: never queries devices, opens audio streams or changes services.
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Body) {
    try { & $Body; $checks.Add([pscustomobject]@{Name=$Name;Passed=$true;Error=$null}) }
    catch { $checks.Add([pscustomobject]@{Name=$Name;Passed=$false;Error=$_.Exception.Message}) }
}
function Equal($Actual,$Expected) { if ($Actual -cne $Expected) { throw "Expected $Expected; received $Actual" } }
function Refuses([scriptblock]$Body) {
    $refused=$false
    try { & $Body } catch { $refused=$true }
    if (!$refused) { throw 'Expected refusal.' }
}
$identity='usb-v1:1395:0098:USB\VID_1395&PID_0098\TEST|Playback'
function Evidence([string]$Outcome='Passed') {
    [pscustomobject]@{
        Outcome=$Outcome;EndpointDeviceIdentity=$identity;ExpectedDeviceIdentity=$identity
        Error=$null;SuiteStopRequired=$true;SuiteServiceStoppedValidated=$true;SuiteStoppedChecks=75
        WindowsControlsPreserved=$true;FullGsxBufferRestoredWhileStreamsOpen=$true;FullB20BufferPreserved=$true
        DspResponseObserved=($Outcome -eq 'Passed')
        Report=[pscustomobject]@{Outcome=$Outcome;Error=$null;SettingsRestored=$true;Phases=@('flat','boost','flat')}
    }
}
Check 'Read-only preparation needs no stop flag' { Assert-GsxInitializationOptions $false $false $false $false $false }
Check 'Audio measurements require stopped vendor support' {
    Refuses { Assert-GsxInitializationOptions $false $true $false $false $false }
    Refuses { Assert-GsxInitializationOptions $false $false $true $false $false }
}
Check 'Reconnect and audio restart require explicit stop' {
    Refuses { Assert-GsxInitializationOptions $false $false $false $true $false }
    Refuses { Assert-GsxInitializationOptions $false $false $false $false $true }
}
Check 'Reconnect cannot be combined with audio restart' { Refuses { Assert-GsxInitializationOptions $true $false $false $true $true } }
Check 'Combined microphone/playback measurement accepts a controlled restart' { Assert-GsxInitializationOptions $true $true $true $false $true }
Check 'Complete playback evidence passes' { Equal (Get-GsxPlaybackAudioOutcome 0 (Evidence) $identity) 'Passed' }
Check 'Quality-limited playback remains inconclusive' { Equal (Get-GsxPlaybackAudioOutcome 2 (Evidence 'Inconclusive') $identity) 'Inconclusive' }
Check 'Exit code cannot relabel inconclusive evidence' { Equal (Get-GsxPlaybackAudioOutcome 0 (Evidence 'Inconclusive') $identity) 'Failed' }
Check 'Failure exit overrides a passing report' { Equal (Get-GsxPlaybackAudioOutcome 1 (Evidence) $identity) 'Failed' }
Check 'Missing report or expected identity refuses evidence' {
    Equal (Get-GsxPlaybackAudioOutcome 0 $null $identity) 'Failed'
    Equal (Get-GsxPlaybackAudioOutcome 0 (Evidence) '') 'Failed'
}
foreach ($field in @('EndpointDeviceIdentity','ExpectedDeviceIdentity')) {
    Check ("Wrong physical identity: "+$field) { $e=Evidence; $e.$field='replacement'; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
}
foreach ($field in @('SuiteStopRequired','SuiteServiceStoppedValidated','WindowsControlsPreserved','FullGsxBufferRestoredWhileStreamsOpen','FullB20BufferPreserved')) {
    Check ("Required context/restoration flag: "+$field) {
        foreach ($bad in @($false,'true',$null)) { $e=Evidence; $e.$field=$bad; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
    }
}
Check 'Nested processing restoration is mandatory' { $e=Evidence; $e.Report.SettingsRestored=$false; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
Check 'Root and measurement errors override success' {
    $e=Evidence; $e.Error='service restarted'; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed'
    $e=Evidence; $e.Report.Error='capture failed'; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed'
}
Check 'All three measurement windows are required' { $e=Evidence; $e.Report.Phases=@('flat','boost'); Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
Check 'Nested outcome must match the exit and root outcome' { $e=Evidence; $e.Report.Outcome='Inconclusive'; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
Check 'Stopped service must actually have been checked' {
    foreach ($bad in @(0,-1,'75',$null)) { $e=Evidence; $e.SuiteStoppedChecks=$bad; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed' }
}
Check 'DSP response must match the measurement outcome' {
    $e=Evidence; $e.DspResponseObserved=$false; Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed'
    $e=Evidence 'Inconclusive'; $e.DspResponseObserved=$true; Equal (Get-GsxPlaybackAudioOutcome 2 $e $identity) 'Failed'
}
Check 'Normal service-running playback report cannot prove fresh-session audio' {
    $e=Evidence; $e.PSObject.Properties.Remove('SuiteServiceStoppedValidated'); Equal (Get-GsxPlaybackAudioOutcome 0 $e $identity) 'Failed'
}
Check 'Read-only preparation can pass without initialization/audio' { Equal (Get-GsxInitializationOutcome $true $false 'NotRequested' 'NotRun' 'NotRequested' 'NotRequested' $false $false $false $false) 'Passed' }
Check 'Fresh control-only run can pass without audio' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'NotRequested' 'NotRequested' $false $false $false $false) 'Passed' }
Check 'Playback audio pass does not require microphone audio' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'NotRequested' 'Passed' $false $true $false $false) 'Passed' }
Check 'Either audio measurement can make the overall result inconclusive' {
    Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'NotRequested' 'Inconclusive' $false $true $false $false) 'Inconclusive'
    Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Inconclusive' 'Passed' $true $true $false $false) 'Inconclusive'
}
Check 'Playback failure overrides microphone audio success' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Passed' 'Failed' $true $true $false $false) 'Failed' }
Check 'Unperformed requested playback is not a pass' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Passed' 'NotRequested' $true $true $false $false) 'Failed' }
Check 'Unperformed requested microphone audio is not a pass' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'NotRequested' 'Passed' $true $true $false $false) 'Failed' }
Check 'Recovery errors override inconclusive audio' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Passed' 'Inconclusive' $true $true $true $false) 'Failed' }
Check 'Setting differences override passing audio' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Passed' 'Passed' $true $true $false $true) 'Failed' }
Check 'Retained objects remain blocked and are never audio success' { Equal (Get-GsxInitializationOutcome $true $true 'BlockedByExistingObjects' 'NotRun' 'NotRequested' 'NotRequested' $true $true $false $false) 'BlockedByExistingObjects' }
Check 'Blocked initialization with recovery failure is a failure' { Equal (Get-GsxInitializationOutcome $true $true 'BlockedByExistingObjects' 'NotRun' 'NotRequested' 'NotRequested' $true $true $false $true) 'Failed' }
Check 'Failed initialization and control checks cannot be masked by audio' {
    Equal (Get-GsxInitializationOutcome $true $true 'Failed' 'Passed' 'Passed' 'Passed' $true $true $false $false) 'Failed'
    Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Failed' 'Passed' 'Passed' $true $true $false $false) 'Failed'
}
Check 'Unknown audio outcome cannot become success' { Equal (Get-GsxInitializationOutcome $true $true 'Passed' 'Passed' 'Passed' 'Unknown' $true $true $false $false) 'Failed' }
Check 'Missing preparation cannot become success' { Equal (Get-GsxInitializationOutcome $false $true 'Passed' 'Passed' 'Passed' 'Passed' $true $true $false $false) 'Failed' }
function RecoverySnapshot([bool]$Missing=$false) {
    $status=if($Missing){'Unavailable'}else{'Available'}
    $errorType=if($Missing){'WaitHandleCannotBeOpenedException'}else{$null}
    [pscustomobject]@{Controls=@(
        [pscustomobject]@{DeviceIdentity=$identity;Audio=[pscustomobject]@{Status='Available'};Playback=[pscustomobject]@{Status=$status;ErrorType=$errorType}},
        [pscustomobject]@{DeviceIdentity=$identity.Replace('|Playback','|Microphone');Audio=[pscustomobject]@{Status='Available'};Microphone=[pscustomobject]@{Status=$status;ErrorType=$errorType}}
    )}
}
Check 'Available processing while held needs no recovery' { Equal (Test-GsxVendorRecoveryRequired (RecoverySnapshot) (RecoverySnapshot)) $false }
Check 'Minimized post-release trace requires vendor recovery despite live audio endpoints' { Equal (Test-GsxVendorRecoveryRequired (RecoverySnapshot) (RecoverySnapshot $true)) $true }
Check 'Recovery refuses replacement physical devices' {
    $now=RecoverySnapshot $true; foreach ($row in $now.Controls) { $row.DeviceIdentity=$row.DeviceIdentity.Replace('TEST','OTHER') }
    Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
}
Check 'Recovery refuses missing and duplicate current endpoints' {
    $now=RecoverySnapshot $true; $now.Controls=@($now.Controls[0]); Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
    $now=RecoverySnapshot $true; $now.Controls+=@($now.Controls[0]); Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
}
Check 'Recovery refuses ambiguous baseline or mixed physical pair' {
    $prior=RecoverySnapshot; $prior.Controls+=@($prior.Controls[0]); Refuses { Test-GsxVendorRecoveryRequired $prior (RecoverySnapshot $true) }
    $prior=RecoverySnapshot; $prior.Controls[1].DeviceIdentity=$prior.Controls[1].DeviceIdentity.Replace('TEST','OTHER'); Refuses { Test-GsxVendorRecoveryRequired $prior (RecoverySnapshot $true) }
}
Check 'Recovery refuses disconnected audio endpoints before service restart' {
    $now=RecoverySnapshot $true; $now.Controls[0].Audio.Status='Unavailable'; Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
}
Check 'Recovery requires a readable saved starting state' { Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot $true) (RecoverySnapshot $true) } }
Check 'Recovery refuses partial or unknown processing failures' {
    $now=RecoverySnapshot $true; $now.Controls[0].Playback.Status='Available'; Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
    $now=RecoverySnapshot $true; $now.Controls[0].Playback.ErrorType='UnauthorizedAccessException'; Refuses { Test-GsxVendorRecoveryRequired (RecoverySnapshot) $now }
}
function VendorOwners([int]$ProcessId=123,[string]$ProcessName='EPOSGamingSuiteService',[string]$ProductId='0098') {
    foreach ($suffix in @('memory','mutex','event')) { [pscustomobject]@{ProcessId=$ProcessId;ProcessName=$ProcessName;HandleCount=1;ObjectName=('Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$ProductId+'_'+$suffix)} }
}
Check 'Handoff requires the current vendor service to hold all three objects' { Equal (Test-ApoVendorHandoffReady @(VendorOwners) 123) $true }
Check 'Service Running and helper/audio-engine ownership alone cannot prove handoff' {
    Equal (Test-ApoVendorHandoffReady @() 123) $false
    Equal (Test-ApoVendorHandoffReady @(VendorOwners 123 'Timbre.Host') 123) $false
    Equal (Test-ApoVendorHandoffReady @(VendorOwners 124 'audiodg') 123) $false
}
Check 'Handoff refuses stale service PID, partial handles and split process ownership' {
    Equal (Test-ApoVendorHandoffReady @(VendorOwners) 124) $false
    $owners=@(VendorOwners); Equal (Test-ApoVendorHandoffReady @($owners[0],$owners[1]) 123) $false
    $owners[2].ProcessId=124; Equal (Test-ApoVendorHandoffReady $owners 123) $false
    Equal (Test-ApoVendorHandoffReady @(VendorOwners) 0) $false
}
Check 'Handoff rejects empty handles and another model namespace' {
    $owners=@(VendorOwners); $owners[2].HandleCount=0; Equal (Test-ApoVendorHandoffReady $owners 123) $false
    Equal (Test-ApoVendorHandoffReady @(VendorOwners 123 'EPOSGamingSuiteService' '009f') 123) $false
}
Check 'B20 handoff can be inspected independently with its exact namespace' { Equal (Test-ApoVendorHandoffReady @(VendorOwners 123 'EPOSGamingSuiteService' '009f') 123 '009f') $true }
$failures=@($checks | Where-Object { !$_.Passed })
$path=[IO.Path]::GetFullPath($ReportPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
[pscustomobject]@{Total=$checks.Count;Failed=$failures.Count;HardwareAccess=$false;ServiceChanges=$false;Checks=$checks.ToArray()} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
if ($failures.Count) { throw (($failures | ForEach-Object { $_.Name+': '+$_.Error }) -join [Environment]::NewLine) }
Write-Output ("GSX initialization offline checks PASS: "+$checks.Count+". Report: "+$path)
