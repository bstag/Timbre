[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Baseline)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GsxInitializationChecks.ps1')
. (Join-Path $PSScriptRoot 'ApoRecoveryChecks.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Baseline=[IO.Path]::GetFullPath($Baseline)
if (!(Test-Path -LiteralPath $Baseline)) { throw 'Provide the saved baseline.json from the failed GSX run.' }
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run vendor-service recovery in administrator PowerShell. No changes made.' }
if (@(Get-Process Timbre.Host,EposControl.Host -ErrorAction SilentlyContinue).Count) { throw 'Close experimental helpers before vendor recovery.' }
if ((Get-Service EPOSGamingSuiteService).Status -ne 'Running' -or (Get-Service Audiosrv).Status -ne 'Running') { throw 'Both services must already be Running for this bounded recovery.' }
$appExe=Join-Path $root 'dist/Timbre.exe'
if (!(Test-Path -LiteralPath $appExe)) { throw 'Build the app first.' }
$reportRoot=Join-Path $root ('artifacts/gsx-vendor-recovery/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$saved=Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
$targets=@($saved.Controls | Where-Object DeviceIdentity -like 'usb-v1:1395:0098:*')
function Diagnostic([string]$name) {
    $path=Join-Path $reportRoot ($name+'.json')
    $p=Start-Process -FilePath $appExe -ArgumentList @('--diagnostics',('"'+$path+'"')) -WindowStyle Hidden -PassThru -Wait
    if ($p.ExitCode) { throw ('Read-only diagnostic failed: '+$name) }
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}
function Compare-Controls($before,$after) {
    foreach ($prior in $before.Controls) {
        $rows=@($after.Controls | Where-Object DeviceIdentity -ceq $prior.DeviceIdentity)
        if ($rows.Count -ne 1) { $prior.DeviceIdentity+': missing/ambiguous'; continue }
        foreach ($field in @('Audio','Microphone','Playback','Sidetone')) {
            if (($prior.$field | ConvertTo-Json -Depth 12 -Compress) -cne ($rows[0].$field | ConvertTo-Json -Depth 12 -Compress)) { $prior.DeviceIdentity+': '+$field+' differs' }
        }
    }
}
function Read-HeldB20 {
    $owned=$false
    try {
        try { $owned=$mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $owned=$true; throw 'B20 mutex abandoned; recovery refused.' }
        if (!$owned -or $view.Capacity -ne 4096) { throw 'Cannot safely capture B20 state.' }
        $bytes=New-Object byte[] 4096; $null=$view.ReadArray(0,$bytes,0,$bytes.Length); return ,$bytes
    } finally { if ($owned) { $mutex.ReleaseMutex() } }
}
$mutex=$null; $map=$null; $event=$null; $view=$null; $b20Before=$null
$attempted=$false; $readable=$false; $preserved=$null; $errors=[Collections.Generic.List[string]]::new(); $differences=@()
try {
    $before=Diagnostic 'before'
    if (!(Test-GsxVendorRecoveryRequired $saved $before)) {
        $readable=$true; Write-Output 'GSX processing is already readable; vendor service will not be restarted.'
    } else {
        if (@($before.Controls | Where-Object DeviceIdentity -like 'usb-v1:1395:009F:*').Count) {
            $name='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f'
            $mutex=[Threading.Mutex]::OpenExisting($name+'_mutex')
            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'_memory',[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
            $event=[Threading.EventWaitHandle]::OpenExisting($name+'_event')
            $view=$map.CreateViewAccessor(0,0,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
            $b20Before=Read-HeldB20
            [IO.File]::WriteAllBytes((Join-Path $reportRoot 'b20-before.bin'),$b20Before)
        }
        Write-Output 'Restarting only EPOS support to recover the absent GSX interface. Windows Audio stays running.'
        $attempted=$true
        Restart-Service EPOSGamingSuiteService -ErrorAction Stop
        (Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    }
} catch { $errors.Add($_.Exception.ToString()) }
finally {
    try {
        if ($attempted -and (Get-Service EPOSGamingSuiteService).Status -ne 'Running') { Start-Service EPOSGamingSuiteService; (Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15)) }
        # A timed-out control request can still finish asynchronously. Retain B20
        # during that transition and observe the actual current vendor owner.
        if ($attempted) {
            if (!('EposResearch.ApoHandleOwners' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ApoHandleOwners.cs') }
            $watch=[Diagnostics.Stopwatch]::StartNew(); $attempt=0; $ownerObserved=$false
            do {
                $after=Diagnostic ('service-restarted-'+$attempt++)
                $rows=@($after.Controls | Where-Object DeviceIdentity -cin $targets.DeviceIdentity)
                $readable=$rows.Count -eq 2 -and @($rows | Where-Object { $_.Playback.Status -eq 'Available' -or $_.Microphone.Status -eq 'Available' }).Count -eq 2
                if ($readable -and (Get-Service EPOSGamingSuiteService).Status -eq 'Running') {
                    $serviceProcessId=[int](Get-CimInstance Win32_Service -Filter "Name='EPOSGamingSuiteService'").ProcessId
                    $owners=@([EposResearch.ApoHandleOwners]::Read('0098'))
                    $ownerObserved=Test-ApoVendorHandoffReady $owners $serviceProcessId
                    if ($b20Before -and $ownerObserved) {
                        $b20Owners=@([EposResearch.ApoHandleOwners]::Read('009f'))
                        $ownerObserved=Test-ApoVendorHandoffReady $b20Owners $serviceProcessId '009f'
                    }
                    if ($ownerObserved) { break }
                }
                Start-Sleep -Milliseconds 500
            } while ($watch.Elapsed.TotalSeconds -lt 60)
            [pscustomobject]@{VendorOwnershipObserved=$ownerObserved;WaitSeconds=$watch.Elapsed.TotalSeconds;VendorProcessId=$serviceProcessId;GsxOwners=$owners;B20Owners=$b20Owners} |
                ConvertTo-Json -Depth 8 | Set-Content (Join-Path $reportRoot 'vendor-ownership.json') -Encoding UTF8
            if (!$ownerObserved) { $errors.Add('Vendor ownership was not observed before bounded B20 lease cleanup.') }
        }
        if ($b20Before) {
            $b20After=Read-HeldB20; [IO.File]::WriteAllBytes((Join-Path $reportRoot 'b20-after.bin'),$b20After)
            $preserved=[Convert]::ToBase64String($b20Before) -ceq [Convert]::ToBase64String($b20After)
        }
    } catch { $errors.Add('Service transition observation: '+$_.Exception.ToString()) }
    if ($view) { $view.Dispose() }; if ($map) { $map.Dispose() }; if ($event) { $event.Dispose() }; if ($mutex) { $mutex.Dispose() }
    try {
        $final=Diagnostic 'final'
        $readable=Test-ApoRecoveryReady $saved $final
        $differences=@(Compare-Controls $saved $final)
        $services=@(Get-Service EPOSGamingSuiteService,Audiosrv | Select-Object Name,@{Name='State';Expression={$_.Status.ToString()}})
        if (@($services | Where-Object State -ne 'Running').Count) { $errors.Add('Both services must finish Running.') }
        if ($b20Before -and $preserved -ne $true) { $errors.Add('B20 complete buffer preservation was not verified after vendor restart.') }
        if (!$readable) { $errors.Add('GSX processing was not recovered by the vendor-service restart.') }
    } catch { $errors.Add('Final verification: '+$_.Exception.Message) }
    $passed=$errors.Count -eq 0 -and $differences.Count -eq 0
    [pscustomobject]@{Outcome=if($passed){'Passed'}else{'Failed'};Baseline=$Baseline;ServiceRestartAttempted=$attempted;InterfaceReadable=$readable;B20CaptureMade=($null -ne $b20Before);B20BufferPreserved=$preserved;SettingsDifferences=$differences;Errors=$errors.ToArray();Services=$services;WindowsAudioRestarted=$false;DirectSettingsWrites=$false;Scope='Vendor-service recovery of absent GSX objects, no direct processing writes'} |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $reportRoot 'summary.json') -Encoding UTF8
    Write-Output ('GSX vendor recovery reports: '+$reportRoot)
}
if (!$passed) { throw (($errors.ToArray()+$differences) -join [Environment]::NewLine) }
Write-Output 'GSX vendor interface recovered; captured controls match the original baseline.'
