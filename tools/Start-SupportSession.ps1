[CmdletBinding()]
param([string]$BuildDirectory='dist', [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$ReportDirectory, [Parameter(Mandatory)][string]$ExpectedDeviceInstance,
    [string]$ExpectedB20Instance, [int]$ControllerProcessId, [DateTimeOffset]$ControllerStartedUtc,
    [ValidateRange(30,86400)][int]$SessionSeconds=86400, [switch]$PrepareOnly)
$ErrorActionPreference='Stop'
trap {
    $launchFailure=$_.Exception.Message
    try {
        if($reportRoot -and !(Test-Path -LiteralPath (Join-Path $reportRoot 'summary.json'))){
            New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
            [ordered]@{Outcome='Failed';DeviceInstance=$ExpectedDeviceInstance;PrepareOnly=[bool]$PrepareOnly;ServiceStopAttempted=[bool]$changed;Errors=@($launchFailure);SettingsDifferences=@()} |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
        }
    }catch{}
    [Console]::Error.WriteLine($launchFailure)
    exit 1
}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'SupportSessionChecks.ps1')
$reportRoot=Get-SupportReportDirectory $root $ReportDirectory
$build=if([IO.Path]::IsPathRooted($BuildDirectory)){[IO.Path]::GetFullPath($BuildDirectory)}else{[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))}
$data=[IO.Path]::GetFullPath($StateDirectory)
$app=Join-Path $build 'Timbre.exe'; $hostExe=Join-Path $build 'host\Timbre.Host.exe'
$copies=@((Join-Path $build 'Timbre.Core.dll'),(Join-Path $build 'host\Timbre.Core.dll'),(Join-Path $root 'tests\Timbre.Tests\bin\Release\net9.0\Timbre.Core.dll'))
if (@($copies | ForEach-Object {(Get-FileHash -LiteralPath $_).Hash} | Select-Object -Unique).Count -ne 1) { throw 'Verify matching app/helper/test binaries first.' }
foreach($path in @($app,$hostExe)) { if(!(Test-Path -LiteralPath $path)) { throw 'Build app and helper first.' } }
if (!$PrepareOnly) {
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required. No services changed.' }
}
function Controller-Alive {
    try { $controller=Get-Process -Id $ControllerProcessId -ErrorAction Stop; return (Test-SupportController $ControllerProcessId $ControllerStartedUtc $controller) }
    catch { return $false }
}
if (!$PrepareOnly -and !(Controller-Alive)) { throw 'The controller process is absent or changed. No services changed.' }
if (@(Get-Process Timbre.Host,EposControl.Host -ErrorAction SilentlyContinue).Count) { throw 'An experimental helper is already running.' }
if ((Get-Service EPOSGamingSuiteService).Status -ne 'Running' -or (Get-Service Audiosrv).Status -ne 'Running') { throw 'EPOS support and Windows Audio must be running for preparation.' }
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$errors=[Collections.Generic.List[string]]::new(); $differences=[Collections.Generic.List[string]]::new()
$helpers=[Collections.Generic.List[object]]::new()
$stop=Join-Path $reportRoot 'stop-hosts'; $request=Join-Path $reportRoot 'stop-session'
$changed=$false; $suiteClosed=$false; $latest=$null; $buffers=@{}; $handoff=$false; $helpersStopped=$true; $controlsPreserved=$false; $buffersPreserved=$false
$suite=@(Get-Process EPOSGamingSuite -ErrorAction SilentlyContinue)
$started=[DateTimeOffset]::UtcNow; $deadline=$started.AddSeconds($SessionSeconds)
$supervisorStarted=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')
function Save-Report([string]$name,$value) {
    $path=Join-Path $reportRoot ($name+'.json'); $temporary=$path+'.tmp'
    [IO.File]::WriteAllText($temporary,($value | ConvertTo-Json -Depth 25),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
function Session-State([string]$state) {
    Save-Report 'session' ([ordered]@{State=$state;DeviceInstance=$ExpectedDeviceInstance;SupervisorProcessId=$PID;CollectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Deadline=$deadline.ToString('o');ServiceState=(Get-Service EPOSGamingSuiteService).Status.ToString()})
}
function Diagnostic([string]$name) {
    $path=Join-Path $reportRoot ($name+'.json')
    $process=Start-Process -FilePath $app -ArgumentList @('--diagnostics',('"'+$path+'"')) -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $path)) { throw ('Read-only diagnostic failed: '+$name) }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Read-Buffer([string]$product) {
    $name='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$product
    $mutex=[Threading.Mutex]::OpenExisting($name+'_mutex'); $held=$false; $map=$null; $view=$null
    try {
        try {$held=$mutex.WaitOne(1000)} catch [Threading.AbandonedMutexException] {$held=$true;throw 'Abandoned effects mutex.'}
        if(!$held){throw 'Effects mutex timeout.'}
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'_memory',[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $view=$map.CreateViewAccessor(0,0,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        if($view.Capacity -ne 4096){throw 'Unknown effects memory size.'}
        $bytes=[byte[]]::new(4096); if($view.ReadArray(0,$bytes,0,4096) -ne 4096){throw 'Incomplete effects memory.'}; return ,$bytes
    }finally{if($view){$view.Dispose()};if($map){$map.Dispose()};if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
function Launch-Host([string]$product,[string]$instance) {
    $name=if($product -eq '0098'){'gsx'}else{'b20'}
    $state=Join-Path $data $(if($product -eq '0098'){'gsx-processing-state'}else{'processing-state'})
    $mode=if($product -eq '0098'){'--manage-gsx'}else{'--manage-b20'}
    $directory=Join-Path $reportRoot $name
    $process=[Diagnostics.Process]::new(); $process.StartInfo.FileName=$hostExe; $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $arguments=@($mode,'--initial-state',('"'+(Join-Path $reportRoot ($name+'-seed.json'))+'"'),'--state-directory',('"'+$state+'"'),'--device-instance',('"'+$instance+'"'),
        '--report-directory',('"'+$directory+'"'),'--stop-file',('"'+$stop+'"'),'--seconds',([Math]::Min(86400,$SessionSeconds+60).ToString()),'--supervisor-id',$PID.ToString(),'--supervisor-started-utc',$supervisorStarted)
    $process.StartInfo.Arguments=$arguments -join ' '
    if(!$process.Start()){throw 'Managed host did not start.'}
    $entry=[pscustomobject]@{Name=$name;Product=$product;Instance=$instance;Process=$process;Directory=$directory};$helpers.Add($entry)
}
function Heartbeat($entry) {
    if($entry.Process.HasExited){throw ('Managed host exited: '+$entry.Name)}
    $path=Join-Path $entry.Directory 'heartbeat.json'
    $value=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Assert-SupportHeartbeat $value $entry.Process.Id $entry.Instance
    if($entry.Product -eq '0098' -and $value.State -eq 'Connected' -and $value.PlaybackEndpoint.Usb.InstanceId -ine $entry.Instance){throw 'GSX playback identity differs.'}
    return $value
}
$owner=[Threading.Mutex]::new($false,'Global\Timbre.SupportSession.v1');$owned=$false
try { try {$owned=$owner.WaitOne(0)} catch [Threading.AbandonedMutexException] {$owned=$true}; if(!$owned){throw 'Another support supervisor is already running.'} }
catch {$owner.Dispose();throw}
try {
    Session-State 'Starting'
    $preparedPath=Join-Path $reportRoot 'prepared.json'
    $arguments=@('--prepare-support-session',('"'+$preparedPath+'"'),'--expected-gsx',('"'+$ExpectedDeviceInstance+'"'),'--data-directory',('"'+$data+'"'))
    if($ExpectedB20Instance){$arguments+=@('--expected-b20',('"'+$ExpectedB20Instance+'"'))}
    $process=Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $preparedPath)){throw 'Read-only seed/store preparation failed. No services changed.'}
    $prepared=Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json
    Save-Report 'gsx-seed' $prepared.GsxSeed
    if($prepared.B20Seed){Save-Report 'b20-seed' $prepared.B20Seed}
    $baseline=Diagnostic 'baseline'
    if($PrepareOnly){
        $buffers['0098']=[Convert]::FromBase64String($prepared.GsxSeed.DiagnosticSeed)
        if($prepared.B20Seed){$buffers['009f']=[Convert]::FromBase64String($prepared.B20Seed.DiagnosticSeed)}
    }
    if(!$PrepareOnly){
        if(!(Controller-Alive) -or (Test-Path -LiteralPath $request)){throw 'Start canceled before service changes.'}
        foreach($process in $suite){Stop-Process -Id $process.Id -ErrorAction Stop}; $suiteClosed=$suite.Count -gt 0
        $changed=$true; Stop-Service EPOSGamingSuiteService -ErrorAction Stop
        (Get-Service EPOSGamingSuiteService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
        Launch-Host '0098' $ExpectedDeviceInstance
        if($ExpectedB20Instance){Launch-Host '009f' $ExpectedB20Instance}
        $watch=[Diagnostics.Stopwatch]::StartNew()
        foreach($entry in $helpers){
            while(!(Test-Path -LiteralPath (Join-Path $entry.Directory 'ready.json')) -and !$entry.Process.HasExited -and $watch.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 100}
            $value=Heartbeat $entry
            if($value.State -ne 'Connected'){throw 'Initial managed connection did not become ready.'}
        }
        Session-State 'Running'
        while((Controller-Alive) -and !(Test-Path -LiteralPath $request) -and [DateTimeOffset]::UtcNow -lt $deadline){
            if((Get-Service EPOSGamingSuiteService).Status -ne 'Stopped'){throw 'EPOS support restarted during the session; returning control to it.'}
            foreach($entry in $helpers){Heartbeat $entry | Out-Null}
            Start-Sleep -Milliseconds 500
        }
    }
}catch{$errors.Add($_.Exception.Message)}
finally{
    if(!$PrepareOnly){Session-State 'Recovering'}
    # Snapshot the latest applied controls, not the startup baseline. Named saves
    # remain governed by explicit UI actions and each store's restore preference.
    try{
        $latest=Diagnostic 'latest-before-handoff'
        foreach($entry in $helpers){
            $present=@($latest.Endpoints | Where-Object {$_.Usb.InstanceId -ieq $entry.Instance -and $_.Direction -eq 1}).Count -eq 1
            if($present){$buffers[$entry.Product]=Read-Buffer $entry.Product;[IO.File]::WriteAllBytes((Join-Path $reportRoot ($entry.Name+'-before-handoff.bin')),$buffers[$entry.Product])}
        }
    }catch{$errors.Add('Latest settings capture: '+$_.Exception.Message)}
    try{
        if($changed -and (Get-Service EPOSGamingSuiteService).Status -ne 'Running'){Start-Service EPOSGamingSuiteService -ErrorAction Stop;(Get-Service EPOSGamingSuiteService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))}
        if(!$PrepareOnly -and $changed){
            if(!('EposResearch.ApoHandleOwners' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'ApoHandleOwners.cs')}
            $watch=[Diagnostics.Stopwatch]::StartNew()
            do{
                $vendor=Get-CimInstance Win32_Service -Filter "Name='EPOSGamingSuiteService'"; $okay=$vendor.State -eq 'Running'; $rows=@()
                foreach($entry in $helpers){
                    if($buffers.ContainsKey($entry.Product)){
                        $owners=@([EposResearch.ApoHandleOwners]::Read($entry.Product));$prefix='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$entry.Product
                        $names=@($owners | Where-Object ProcessId -eq $vendor.ProcessId | Select-Object -ExpandProperty ObjectName)
                        $owns=@('_memory','_event','_mutex' | Where-Object {($prefix+$_) -notin $names}).Count -eq 0
                        $okay=$okay -and $owns;$rows+=@([pscustomobject]@{Product=$entry.Product;Observed=$owns;Owners=$owners})
                    }
                }
                if($okay){$handoff=$true;break};Start-Sleep -Milliseconds 250
            }while($watch.Elapsed.TotalSeconds -lt 45)
            Save-Report 'vendor-handoff' ([pscustomobject]@{Observed=$handoff;WaitSeconds=$watch.Elapsed.TotalSeconds;VendorProcessId=$vendor.ProcessId;Devices=$rows})
            if(!$handoff){throw 'Vendor ownership was not observed before helper release.'}
        }
    }catch{$errors.Add('Vendor recovery: '+$_.Exception.Message)}
    [IO.File]::WriteAllText($stop,'stop')
    foreach($entry in $helpers){
        try{if(!$entry.Process.WaitForExit(55000)){throw 'Helper is still recovering; it was not killed.'};$stopped=Get-Content -LiteralPath (Join-Path $entry.Directory 'stopped.json') -Raw | ConvertFrom-Json
            if($entry.Process.ExitCode -ne 0 -or $stopped.Success -ne $true){throw 'Helper shutdown/recovery failed.'}
        }catch{$helpersStopped=$false;$errors.Add($entry.Name+': '+$_.Exception.Message)}finally{$entry.Process.Dispose()}
    }
    try{
        if($suiteClosed -and @(Get-Process EPOSGamingSuite -ErrorAction SilentlyContinue).Count -eq 0){Start-Process -FilePath 'C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuite.exe' -WindowStyle Hidden}
        $final=Diagnostic 'final'
        foreach($prior in $latest.Controls){$current=@($final.Controls | Where-Object DeviceIdentity -eq $prior.DeviceIdentity)
            if($current.Count -ne 1){$differences.Add('Endpoint differs: '+$prior.DeviceIdentity);continue}
            foreach($field in @('Audio','Microphone','Playback','Sidetone')){if(($prior.$field | ConvertTo-Json -Depth 12 -Compress) -cne ($current[0].$field | ConvertTo-Json -Depth 12 -Compress)){$differences.Add($prior.DeviceIdentity+': '+$field+' differs')}}
        }
        $controlsPreserved=$latest -and $differences.Count -eq 0
        $buffersPreserved=$true
        foreach($product in $buffers.Keys){$bytes=Read-Buffer $product;[IO.File]::WriteAllBytes((Join-Path $reportRoot ($product+'-after-handoff.bin')),$bytes)
            if([Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($buffers[$product])){$buffersPreserved=$false;$differences.Add('Complete '+$product+' buffer differs after handoff.')}
        }
    }catch{$errors.Add('Final verification: '+$_.Exception.Message)}
    $service=(Get-Service EPOSGamingSuiteService).Status.ToString();$audio=(Get-Service Audiosrv).Status.ToString()
    $passed=$errors.Count -eq 0 -and $differences.Count -eq 0 -and $helpersStopped -and $controlsPreserved -and $buffersPreserved -and $service -eq 'Running' -and $audio -eq 'Running' -and ($PrepareOnly -or $handoff)
    Save-Report 'summary' ([ordered]@{Outcome=$(if($passed){'Passed'}else{'Failed'});DeviceInstance=$ExpectedDeviceInstance;PrepareOnly=[bool]$PrepareOnly;ServiceStopAttempted=$changed;VendorHandoffObserved=$handoff;HelpersStopped=$helpersStopped;LatestControlsPreserved=[bool]$controlsPreserved;FullBuffersPreserved=$buffersPreserved;FinalServiceState=$service;FinalAudioServiceState=$audio;Errors=$errors.ToArray();SettingsDifferences=$differences.ToArray();UserStoresWritten=$false;AudioMeasured=$false;AudioEngineRestarted=$false})
    if($owned){$owner.ReleaseMutex()};$owner.Dispose()
}
Write-Output ('Support session reports: '+$reportRoot)
if(!$passed){throw (($errors.ToArray()+$differences.ToArray()) -join [Environment]::NewLine)}
