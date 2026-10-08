[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ApoRecoveryChecks.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run=Join-Path $root 'artifacts/gsx-initialization/20261008-114800-e88e45ba'
$baseline=Get-Content (Join-Path $run 'baseline.json') -Raw | ConvertFrom-Json
$early=Get-Content (Join-Path $run 'restored-0.json') -Raw | ConvertFrom-Json
$later=Get-Content (Join-Path $run 'recovery-later.json') -Raw | ConvertFrom-Json
if (Test-ApoRecoveryReady $baseline $early) { throw 'Recovery incorrectly completes while B20 processing is still unavailable.' }
if (!(Test-ApoRecoveryReady $baseline $later)) { throw 'Recovery should complete when both devices are readable.' }
$checks=2
foreach ($prior in $baseline.Controls) {
    foreach ($field in @('Audio','Microphone','Playback','Sidetone')) {
        if ($prior.$field.Status -ne 'Available') { continue }
        $copy=$later | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $row=@($copy.Controls | Where-Object DeviceIdentity -eq $prior.DeviceIdentity)[0]
        $row.$field.Status='Unavailable'
        if (Test-ApoRecoveryReady $baseline $copy) { throw ('Recovery completed before '+$prior.DeviceIdentity+' '+$field+' was ready.') }
        $checks++
    }
}
$missing=$later | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$missing.Controls=@($missing.Controls | Select-Object -Skip 1)
if (Test-ApoRecoveryReady $baseline $missing) { throw 'Missing endpoint was accepted.' }; $checks++
$ambiguous=$later | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$ambiguous.Controls=@($ambiguous.Controls)+@($ambiguous.Controls[0])
if (Test-ApoRecoveryReady $baseline $ambiguous) { throw 'Ambiguous endpoint was accepted.' }; $checks++
$changed=$later | ConvertTo-Json -Depth 20 | ConvertFrom-Json
@($changed.Controls | Where-Object {$_.Microphone.Status -eq 'Available'})[0].Microphone.Value.GatePercent=87
if (!(Test-ApoRecoveryReady $baseline $changed)) { throw 'Readable changed settings must remain available for the separate differences check.' }; $checks++
if (Test-ApoRecoveryReady ([pscustomobject]@{Controls=@()}) $later) { throw 'Empty baseline was accepted.' }; $checks++
$instance=@($baseline.Endpoints | Where-Object {$_.Usb.ProductId -eq '0098'})[0].Usb.InstanceId
if (!(Test-GsxConnectionState $baseline $instance $true) -or (Test-GsxConnectionState $baseline $instance $false)) { throw 'Connected GSX pair misclassified.' }; $checks++
$absent=[pscustomobject]@{Endpoints=@($baseline.Endpoints | Where-Object {$_.Usb.ProductId -ne '0098'})}
if (!(Test-GsxConnectionState $absent $instance $false) -or (Test-GsxConnectionState $absent $instance $true)) { throw 'Absent GSX misclassified.' }; $checks++
$partial=[pscustomobject]@{Endpoints=@($baseline.Endpoints | Where-Object {$_.Usb.ProductId -ne '0098' -or $_.Direction -eq 1})}
if ((Test-GsxConnectionState $partial $instance $true) -or (Test-GsxConnectionState $partial $instance $false)) { throw 'Partial GSX pair must keep waiting.' }; $checks++
$different=$baseline | ConvertTo-Json -Depth 20 | ConvertFrom-Json
@($different.Endpoints | Where-Object {$_.Usb.ProductId -eq '0098'})[0].Usb.InstanceId='OTHER'
$rejected=$false
try { Test-GsxConnectionState $different $instance $true | Out-Null } catch { $rejected=$true }
if (!$rejected) { throw 'A different GSX instance was accepted.' }; $checks++
Write-Output ("$checks recovery checks passed, including the captured premature B20 recovery regression.")
if (!(Test-WindowsAudioRecoveryReady $baseline $early)) { throw 'Windows level recovery must not depend on APO readiness.' }
if (Test-WindowsAudioRecoveryReady $baseline $missing) { throw 'Windows audio recovery accepted a missing endpoint.' }
$unreadable=$later | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$unreadable.Controls[0].Audio.Status='Unavailable'
if (Test-WindowsAudioRecoveryReady $baseline $unreadable) { throw 'Windows audio recovery accepted an unreadable level.' }
Write-Output '3 Windows audio recovery readiness checks passed.'
