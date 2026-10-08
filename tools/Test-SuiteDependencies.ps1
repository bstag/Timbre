[CmdletBinding()]
param([switch]$StopSuiteTemporarily)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testDll=Join-Path $root 'tests\Timbre.Tests\bin\Release\net9.0\Timbre.Tests.dll'
$reportRoot=Join-Path $root ('artifacts\dependencies\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$service=Get-Service -Name EPOSGamingSuiteService
$serviceWasRunning=$service.Status -eq 'Running'
$suiteProcesses=@(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue)
$suitePath='C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuite.exe'
$events=[Collections.Generic.List[object]]::new()
function Save-DependencySnapshot([string]$name) {
    $snapshotPath=Join-Path $reportRoot ($name+'.json')
    & dotnet $testDll --dependencies --report $snapshotPath | Out-Null
    if ($LASTEXITCODE) { throw 'Dependency snapshot failed: '+$name }
    $snapshot=Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
    $events.Add([pscustomobject]@{Stage=$name;ServiceStatus=(Get-Service EPOSGamingSuiteService).Status.ToString();SuiteUiCount=@(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue).Count;Snapshot=$snapshotPath})
    return $snapshot
}
$baseline=Save-DependencySnapshot 'baseline'
try {
    if ($StopSuiteTemporarily) {
        # Explicit opt-in only. The exact Suite UI/service are restored in finally.
        foreach($process in $suiteProcesses) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $uiClosed=Save-DependencySnapshot 'ui-closed'
        if ($serviceWasRunning) { Stop-Service -Name EPOSGamingSuiteService -ErrorAction Stop; (Get-Service EPOSGamingSuiteService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15)) }
        $serviceStopped=Save-DependencySnapshot 'service-stopped'
        $b20=$serviceStopped.Endpoints | Where-Object {$_.Endpoint.Direction -eq 'Microphone' -and $_.Endpoint.Usb.ProductId -eq '009F'}
        if ($b20.Effects) {
            & dotnet $testDll --hardware-effects 009f --report (Join-Path $reportRoot 'effects-service-stopped.json')
            $events.Add([pscustomobject]@{Stage='effects-service-stopped';ExitCode=$LASTEXITCODE})
            & dotnet $testDll --hardware-audio 009f --report (Join-Path $reportRoot 'audio-service-stopped.json')
            $events.Add([pscustomobject]@{Stage='audio-service-stopped';ExitCode=$LASTEXITCODE})
        } else {
            $events.Add([pscustomobject]@{Stage='effects-service-stopped';Skipped=$true;Reason=$b20.EffectsError})
            $events.Add([pscustomobject]@{Stage='audio-service-stopped';Skipped=$true;Reason='Processing interface unavailable; no effect writes or capture attempted.'})
        }
        & dotnet $testDll --hardware-sidetone 009f --report (Join-Path $reportRoot 'sidetone-service-stopped.json')
        $events.Add([pscustomobject]@{Stage='sidetone-service-stopped';ExitCode=$LASTEXITCODE})
    }
} catch {
    [pscustomobject]@{Error=$_.Exception.Message;FailedAt=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'failure.json') -Encoding UTF8
    throw
} finally {
    try {
        if ($serviceWasRunning -and (Get-Service EPOSGamingSuiteService).Status -ne 'Running') {
            Start-Service -Name EPOSGamingSuiteService -ErrorAction Stop
            (Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        }
    } finally {
        if ($suiteProcesses.Count -gt 0 -and @(Get-Process -Name EPOSGamingSuite -ErrorAction SilentlyContinue).Count -eq 0) {
            Start-Process -FilePath $suitePath -WindowStyle Hidden
        }
        $events | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'stages.json') -Encoding UTF8
        Write-Output ('Dependency reports: '+$reportRoot)
    }
}
$restored=Save-DependencySnapshot 'suite-restored'
# A running service does not mean its device objects have finished initialization.
# Wait only for processing interfaces that existed at baseline, preserving every snapshot.
for($attempt=1; $attempt -le 10; $attempt++) {
    # Match identities explicitly rather than using nested pipeline $_ values.
    $missing=@(foreach($before in $baseline.Endpoints) {
        if ($before.Effects) {
            $after=$restored.Endpoints | Where-Object {$_.Endpoint.ProfileIdentity -eq $before.Endpoint.ProfileIdentity}
            if (!$after.Effects) { $before.Endpoint.ProfileIdentity }
        }
    })
    if (!$missing.Count) { break }
    Start-Sleep -Seconds 2
    $restored=Save-DependencySnapshot ('suite-recovery-'+$attempt)
}
$differences=[Collections.Generic.List[string]]::new()
$availability=[Collections.Generic.List[string]]::new()
foreach($before in $baseline.Endpoints) {
    $after=$restored.Endpoints | Where-Object {$_.Endpoint.ProfileIdentity -eq $before.Endpoint.ProfileIdentity}
    if (!$after) { $availability.Add('Endpoint unavailable: '+$before.Endpoint.Name); continue }
    foreach($field in @('Effects','Sidetone')) {
        if (($null -eq $before.$field) -ne ($null -eq $after.$field)) { $availability.Add($before.Endpoint.Name+': '+$field+' availability differs after Suite restart'); continue }
        if (($before.$field | ConvertTo-Json -Depth 10 -Compress) -ne ($after.$field | ConvertTo-Json -Depth 10 -Compress)) { $differences.Add($before.Endpoint.Name+': '+$field+' differs after Suite restart') }
    }
    if (($before.Endpoint.State | ConvertTo-Json -Compress) -ne ($after.Endpoint.State | ConvertTo-Json -Compress)) { $differences.Add($before.Endpoint.Name+': Windows level/mute differs') }
}
[pscustomobject]@{GeneratedUtc=[DateTime]::UtcNow.ToString('o');Stages=$events;SettingsDifferences=$differences.ToArray();AvailabilityDifferences=$availability.ToArray();RebootTested=$false;PhysicalReconnectTested=$false;Scope='Warm-session dependency test; existing audio clients may keep APO/shared objects alive. Does not establish cold-start independence.'} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
if ($differences.Count) { $differences | Write-Warning }
if ($availability.Count) { $availability | Write-Warning }
$events | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'stages.json') -Encoding UTF8
Write-Output ('Dependency summary: '+(Join-Path $reportRoot 'summary.json'))
