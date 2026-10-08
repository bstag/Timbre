[CmdletBinding()]
param([switch]$HardwareSidetone, [switch]$HardwareAudio, [switch]$HardwareGsxAudio, [switch]$HardwareEffects, [switch]$HardwareStatus, [switch]$HardwarePlayback, [switch]$HardwareGsxMicrophone, [switch]$HardwareLoopback, [switch]$HardwareGsxSidetone, [ValidateSet('009f','0098')][string]$MonitorPackets)
$ErrorActionPreference='Stop'
if (![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess) { throw 'Run these checks from 64-bit Windows PowerShell.' }
$packageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$runtimeRoot=[Environment]::GetEnvironmentVariable('ProgramW6432')
if (!$runtimeRoot) { $runtimeRoot=[Environment]::GetFolderPath('ProgramFiles') }
$dotnet=Join-Path $runtimeRoot 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Install the .NET 9 Desktop Runtime for Windows x64. See START-HERE.md.' }
$runtimes=& $dotnet --list-runtimes
if ($LASTEXITCODE -or !($runtimes -match '^Microsoft.WindowsDesktop.App 9\.')) { throw 'Install the .NET 9 Desktop Runtime for Windows x64. See START-HERE.md.' }

# Check exact packaged payload paths before executing either binary.
$manifest=Get-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Raw | ConvertFrom-Json
foreach($entry in $manifest.Files) {
    $path=[IO.Path]::GetFullPath((Join-Path $packageRoot $entry.Path))
    if (!$path.StartsWith($packageRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package manifest path.' }
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256) { throw ('Package file is missing or changed: '+$entry.Path) }
}
$reports=Join-Path $packageRoot ('reports\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$testDll=Join-Path $packageRoot 'checks\Timbre.Tests.dll'
$app=Join-Path $packageRoot 'app\Timbre.exe'
& $dotnet $testDll --report (Join-Path $reports 'tests.json') --junit (Join-Path $reports 'tests.xml')
if ($LASTEXITCODE) { throw ('Regression checks failed. See '+$reports) }
& $app --render-demo (Join-Path $reports 'app-preview.png') | Out-Null
if ($LASTEXITCODE) { throw 'UI verification failed. See app\startup-error.txt.' }
& $app --diagnostics (Join-Path $reports 'devices.json') | Out-Null
if ($LASTEXITCODE) { throw 'Device discovery failed. See app\startup-error.txt.' }

if ($HardwareStatus) {
    # Explicit opt-in: sends the recovered B20 status query, with no settings writes.
    & $dotnet $testDll --dependencies --report (Join-Path $reports 'hardware-status.json')
    if ($LASTEXITCODE) { throw ('Status diagnostic failed. See '+$reports) }
    $status=Get-Content -LiteralPath (Join-Path $reports 'hardware-status.json') -Raw | ConvertFrom-Json
    foreach($endpoint in $status.Endpoints) {
        if ($endpoint.PatternError) { Write-Warning ($endpoint.Endpoint.Name+': '+$endpoint.PatternError+' Pickup-pattern support remains experimental.') }
    }
}

if ($HardwareSidetone) {
    & $dotnet $testDll --hardware-sidetone 009f --report (Join-Path $reports 'sidetone.json')
    if ($LASTEXITCODE) { throw ('Sidetone check failed. See '+$reports) }
}
if ($HardwareGsxSidetone) {
    # Explicit opt-in: recovered GSX USB levels, exact restoration, no audio playback/capture.
    & $dotnet $testDll --hardware-gsx-sidetone --report (Join-Path $reports 'gsx-sidetone.json')
    if ($LASTEXITCODE) { throw ('GSX sidetone check failed. See '+$reports) }
}
if ($HardwareEffects) {
    & $dotnet $testDll --hardware-effects 009f --report (Join-Path $reports 'effects.json')
    if ($LASTEXITCODE) { throw ('Effects check failed. See '+$reports) }
}
if ($HardwarePlayback) {
    & $dotnet $testDll --hardware-playback 0098 --report (Join-Path $reports 'playback.json')
    if ($LASTEXITCODE) { throw ('GSX playback check failed. See '+$reports) }
}
if ($HardwareGsxMicrophone) {
    & $dotnet $testDll --hardware-effects 0098 --report (Join-Path $reports 'gsx-microphone.json')
    if ($LASTEXITCODE) { throw ('GSX microphone check failed. See '+$reports) }
}
if ($HardwareLoopback) {
    # Explicit opt-in: quiet generated 1 kHz tone, two seconds, selected GSX output only.
    & $dotnet $testDll --hardware-loopback --report (Join-Path $reports 'gsx-loopback.json')
    if ($LASTEXITCODE) { throw ('GSX loopback check failed. See '+$reports) }
}
if ($HardwareGsxAudio) {
    # Explicit opt-in: GSX shared-mode gate off/maximum/off metrics; no audio saved, starting processing restored.
    & $dotnet $testDll --hardware-audio 0098 --report (Join-Path $reports 'audio-gsx.json')
    if ($LASTEXITCODE -eq 2) { Write-Warning 'GSX audio check inconclusive; repeat with steady quiet input. See its report.' }
    elseif ($LASTEXITCODE) { throw ('GSX audio attenuation criterion or restoration failed; this does not prove driver causality. See '+$reports) }
}
if ($HardwareAudio) {
    & $dotnet $testDll --hardware-audio 009f --report (Join-Path $reports 'audio.json')
    if ($LASTEXITCODE -eq 2) { Write-Warning 'Audio comparison was inconclusive; see the report and repeat with steady ambient input.' }
    elseif ($LASTEXITCODE) { throw ('Audio check failed. See '+$reports) }
}
$devices=Get-Content -LiteralPath (Join-Path $reports 'devices.json') -Raw | ConvertFrom-Json
if ($MonitorPackets) {
    # Metrics-only microphone capture; does not test audible effects or generate sound.
    & $dotnet $testDll --monitor-packets $MonitorPackets --assert-initial-flag-classified --report (Join-Path $reports ('monitor-packets-'+$MonitorPackets+'.json'))
    if ($LASTEXITCODE) { throw ('Microphone packet diagnostics failed. See '+$reports) }
}
Write-Output ('Package, regression and UI checks passed. Discovered EPOS endpoints: '+@($devices.Endpoints).Count)
Write-Output ('Reports: '+$reports)
foreach($control in @($devices.Controls)) {
    $endpoint=@($devices.Endpoints | Where-Object {$_.Id -eq $control.EndpointId}) | Select-Object -First 1
    $monitorStatus=$control.Sidetone.Status
    if ($monitorStatus -eq 'NotSupported' -and $control.Sidetone.Reason -like '*USB status query*') { $monitorStatus='Not queried (open microphone page)' }
    Write-Output ($endpoint.Name+': Windows audio '+$control.Audio.Status+'; microphone processing '+$control.Microphone.Status+'; playback processing '+$control.Playback.Status+'; sidetone '+$monitorStatus)
    foreach($probe in @($control.Audio,$control.Microphone,$control.Playback,$control.Sidetone)) {
        if($probe.Status -eq 'Unavailable') { Write-Warning ($endpoint.Name+': '+$probe.Reason) }
    }
}
if (@($devices.Endpoints).Count -eq 0) { Write-Warning 'No EPOS device is connected. Hardware/listening validation is still pending.' }
