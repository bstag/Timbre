[CmdletBinding()]
param([switch]$SkipBuild, [ValidateSet('009f','0098')][string]$HardwareEffects, [ValidateSet('009f','0098')][string]$HardwareAudio, [ValidateSet('009f')][string]$HardwareSidetone, [switch]$HardwarePlayback, [switch]$HardwareLoopback, [switch]$HardwareGsxSidetone, [ValidateSet('009f','0098')][string]$MonitorPackets)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    if (!$SkipBuild) {
        & (Join-Path $PSScriptRoot 'Build-App.ps1')
        dotnet restore tests/EposControl.Tests --configfile NuGet.Config --nologo -v quiet
        if ($LASTEXITCODE) { throw 'Test restore failed' }
        dotnet build tests/EposControl.Tests --no-restore -c Release --nologo -v quiet
        if ($LASTEXITCODE) { throw 'Test build failed' }
        dotnet build tools/GsxSidetoneProbe -c Release --configfile NuGet.Config --nologo -v quiet
        if ($LASTEXITCODE) { throw 'GSX research probe build failed' }
    }
    $testDll=Join-Path $root 'tests/EposControl.Tests/bin/Release/net9.0/EposControl.Tests.dll'
    dotnet $testDll --report artifacts/tests.json --junit artifacts/tests.xml
    if ($LASTEXITCODE) { throw 'Regression tests failed; see artifacts/tests.json' }
    $gsxProbe=Join-Path $root 'tools/GsxSidetoneProbe/bin/Release/net9.0/GsxSidetoneProbe.dll'
    if (Test-Path -LiteralPath $gsxProbe) {
        # Offline descriptor/command guards only; never queries or changes hardware.
        dotnet $gsxProbe --self-test
        if ($LASTEXITCODE) { throw 'GSX research guard tests failed' }
    } elseif ($SkipBuild) { Write-Warning 'GSX research guard checks skipped: build tools/GsxSidetoneProbe first.' }
    & (Join-Path $root 'dist/EposControl.exe') --render-demo (Join-Path $root 'artifacts/app-preview.png') | Out-Null
    if ($LASTEXITCODE) { throw 'WPF UI tests failed; see dist/startup-error.txt' }
    if ($HardwareEffects) {
        # Explicit opt-in: changes gate/filter, switches and EQ, then restores their starting values.
        dotnet $testDll --hardware-effects $HardwareEffects --report ('artifacts/effects-verification-'+$HardwareEffects+'.json')
        if ($LASTEXITCODE) { throw 'Hardware effects check failed; see its report.' }
    }
    if ($HardwarePlayback) {
        # Opt-in: briefly changes GSX sound mode/EQ, verifies unowned settings, restores the starting curve/mode.
        dotnet $testDll --hardware-playback 0098 --report artifacts/playback-verification-0098.json
        if ($LASTEXITCODE) { throw 'GSX playback validation failed; see its report.' }
    }
    if ($HardwareLoopback) {
        # Explicit opt-in: two seconds of quiet generated 1 kHz audio on GSX output; no settings/default changes.
        dotnet $testDll --hardware-loopback --report artifacts/loopback-verification-0098.json
        if ($LASTEXITCODE) { throw 'GSX loopback check failed; see its report.' }
    }
    if ($HardwareSidetone) {
        dotnet $testDll --hardware-sidetone $HardwareSidetone --report ('artifacts/sidetone-verification-'+$HardwareSidetone+'.json')
        if ($LASTEXITCODE) { throw 'Sidetone validation failed; see its report.' }
    }
    if ($HardwareGsxSidetone) {
        # Explicit opt-in: GSX USB monitoring levels, exact restoration, no audio playback/capture.
        dotnet $testDll --hardware-gsx-sidetone --report artifacts/gsx-sidetone-verification.json
        if ($LASTEXITCODE) { throw 'GSX sidetone validation failed; see its report.' }
    }
    if ($HardwareAudio) {
        # Opt-in: captures metrics only, changes gate briefly, and restores complete processing state.
        dotnet $testDll --hardware-audio $HardwareAudio --report ('artifacts/audio-verification-'+$HardwareAudio+'.json')
        if ($LASTEXITCODE -eq 2) { Write-Warning 'Audio check inconclusive; repeat with steady quiet input. See its report.' }
        elseif ($LASTEXITCODE) { throw 'Audio validation failed; see its report.' }
    }
    if ($MonitorPackets) {
        # Explicit opt-in: three seconds of microphone metrics, no audio saved or played, no settings writes.
        dotnet $testDll --monitor-packets $MonitorPackets --assert-initial-flag-classified --report ('artifacts/monitor-packets-'+$MonitorPackets+'.json')
        if ($LASTEXITCODE) { throw 'Microphone packet diagnostics failed; see its report.' }
    }
    Write-Output 'Regression and WPF UI checks passed. Reports are in artifacts.'
} finally { Pop-Location }
