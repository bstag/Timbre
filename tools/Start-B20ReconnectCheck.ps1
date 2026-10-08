[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$RunDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pilotBase=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts\apo-host'))
function Get-InputHash([string]$path) {
    $stream=[IO.File]::OpenRead($path)
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
if (!$RunDirectory) {
    $pointer=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\current-b20-reconnect-pilot.json') -Raw | ConvertFrom-Json
    $RunDirectory=$pointer.Root
}
$RunDirectory=[IO.Path]::GetFullPath($RunDirectory)
if (!$RunDirectory.StartsWith($pilotBase+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'The prepared run must be under this workspace artifacts\apo-host directory.'
}
$preparation=Get-Content -LiteralPath (Join-Path $RunDirectory 'preparation.json') -Raw | ConvertFrom-Json
$startup=Join-Path $RunDirectory 'startup-state.json'
$isolatedState=Join-Path $RunDirectory 'isolated-state'
$hostExe=Join-Path $workspace 'dist\host\Timbre.Host.exe'
if (!(Test-Path -LiteralPath $hostExe) -or !(Test-Path -LiteralPath $startup)) { throw 'Prepared helper binaries or startup snapshot are missing.' }
if ((Get-InputHash $preparation.MappedRestoreSource) -ne $preparation.OriginalStateHash) {
    throw 'Your saved B20 settings changed since preparation. Ask for a fresh prepared check; no helper was started.'
}
$savedRaw=Get-Content -LiteralPath $preparation.MappedRestoreSource -Raw
$saved=ConvertFrom-Json -InputObject $savedRaw
if ($saved.DeviceIdentity -ne ('1395|009F|Microphone|'+$preparation.DeviceInstance.ToUpperInvariant())) {
    throw 'The saved settings do not match the prepared physical B20.'
}
$preferencePattern='("RestoreOnConnect"\s*:\s*)(true|false)\b'
if ([regex]::Matches($savedRaw,$preferencePattern).Count -ne 1) { throw 'Unexpected saved restore-preference layout.' }
if ((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped') { throw 'Leave EPOSGamingSuiteService stopped for this check.' }
if (@(Get-Process -Name Timbre.Host,EposControl.Host -ErrorAction SilentlyContinue).Count) { throw 'A helper is already running; do not start a competing check.' }
$runId=[Guid]::NewGuid().ToString('N').Substring(0,8)
$reportDirectory=Join-Path $RunDirectory ('host-admin-'+$runId)
$stopFile=Join-Path $RunDirectory 'stop-host'
if (Test-Path -LiteralPath $stopFile) { throw 'This prepared run was already stopped. Ask for a fresh run directory.' }
if (!$PSCmdlet.ShouldProcess($preparation.DeviceInstance,'Start a ten-minute B20 helper using an isolated copy of its latest saved processing settings')) { return }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this prepared check in administrator PowerShell. Fresh Global processing memory needs a privileged token; your real settings and service state were not changed.'
}
# Preserve the source UTC timestamp verbatim. PowerShell date parsing/serialization
# can otherwise convert it to a local offset rejected by the typed store.
$stateCopy=[regex]::Replace($savedRaw,$preferencePattern,'${1}true')
$statePath=Join-Path $isolatedState (Split-Path -Leaf $preparation.MappedRestoreSource)
[IO.File]::WriteAllText($statePath,$stateCopy,[Text.UTF8Encoding]::new($false))
$arguments=@('--manage-b20','--initial-state',('"'+$startup+'"'),
    '--state-directory',('"'+$isolatedState+'"'),'--device-instance',('"'+$preparation.DeviceInstance+'"'),
    '--report-directory',('"'+$reportDirectory+'"'),'--stop-file',('"'+$stopFile+'"'),'--seconds','600')
$process=$null
try {
    $process=Start-Process -FilePath $hostExe -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $RunDirectory ('stdout-admin-'+$runId+'.txt')) `
        -RedirectStandardError (Join-Path $RunDirectory ('stderr-admin-'+$runId+'.txt'))
    [ordered]@{Root=$RunDirectory;ReportRoot=$reportDirectory;ProcessId=$process.Id} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $workspace 'artifacts\current-b20-reconnect-pilot.json') -Encoding UTF8
    $readyPath=Join-Path $reportDirectory 'ready.json'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $readyPath) -and !$process.HasExited -and $watch.Elapsed.TotalSeconds -lt 12) { Start-Sleep -Milliseconds 200 }
    if (!(Test-Path -LiteralPath $readyPath) -or $process.HasExited) { throw ('Helper did not become ready. Reports: '+$reportDirectory) }
    $ready=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.ProcessId -ne $process.Id -or $ready.State -ne 'Connected' -or $ready.StartupRestorePolicy -ne 'LastSavedProcessing' -or
        $ready.Endpoint.Usb.InstanceId -ne $preparation.DeviceInstance -or
        ($ready.Effects | ConvertTo-Json -Depth 12 -Compress) -ne ($saved.Effects | ConvertTo-Json -Depth 12 -Compress)) {
        throw 'Helper readiness or restored settings do not match the prepared check.'
    }
    if ((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped') { throw 'The EPOS service restarted during helper startup.' }
    if ((Get-InputHash $preparation.MappedRestoreSource) -ne $preparation.OriginalStateHash) {
        throw 'Your real saved state changed during startup; stop and review.'
    }
    Write-Output ('B20 helper ready. Process: '+$process.Id+'. Reports: '+$reportDirectory)
    Write-Output 'Your actual restore preference and service state are unchanged. This helper stops after ten minutes.'
    Write-Output 'Leave the B20 connected and paste this output before the reconnect check.'
} catch {
    if ($process -and !$process.HasExited) {
        [IO.File]::WriteAllText($stopFile,'stop')
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id -ErrorAction Stop }
    }
    throw
}
