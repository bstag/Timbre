[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$probeErrors = [Collections.Generic.List[string]]::new()
$devices = @()
$services = @()
$listeners = @()
try {
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        $_.InstanceId -match '^(USB|HID)\\VID_1395&PID_(0098|009F)' -or
        ($_.Class -eq 'AudioEndpoint' -and $_.FriendlyName -match 'EPOS (B20|GSX 300)')
    } | ForEach-Object {
        $dev = $_
        $properties = @(Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_Service','DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue | Select-Object KeyName,Data)
        [pscustomobject]@{ Name=$dev.FriendlyName; Class=$dev.Class; Status=$dev.Status; InstanceId=$dev.InstanceId; Properties=$properties }
    })
} catch { $probeErrors.Add('PnP: ' + $_.Exception.Message) }
try { $services = @(Get-CimInstance Win32_Service -Filter "Name='EPOSGamingSuiteService'" | Select-Object Name,State,PathName,ProcessId) }
catch { $probeErrors.Add('Service: ' + $_.Exception.Message) }
$processes = @(Get-Process -Name EPOSGamingSuite,EPOSGamingSuiteService -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,Path)
try { $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.OwningProcess -in $processes.Id } | Select-Object LocalAddress,LocalPort,OwningProcess) }
catch { $probeErrors.Add('TCP: ' + $_.Exception.Message) }
$pipes = @()
try { $pipes = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Where-Object { $_.Name -in @('GSAgentPipe','GSUIPipe') } | Select-Object -ExpandProperty Name) }
catch { $probeErrors.Add('Pipe inventory: ' + $_.Exception.Message) }
$hid = @()
try {
    if (-not ('EposResearch.HidProbe' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HidProbe.cs') }
    $hid = @([EposResearch.HidProbe]::Enumerate())
} catch { $probeErrors.Add('HID descriptors: ' + $_.Exception.Message) }
$audio = @()
try {
    if (-not ('EposResearch.AudioProbe' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'AudioProbe.cs') }
    $audio = @([EposResearch.AudioProbe]::Enumerate())
} catch { $probeErrors.Add('Audio endpoints: ' + $_.Exception.Message) }
$install = 'C:\Program Files (x86)\EPOS\Gaming Suite'
$files = @(Get-ChildItem -LiteralPath $install -File -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ Name=$_.Name; Bytes=$_.Length; Version=$_.VersionInfo.FileVersion; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$report = [ordered]@{
    CollectedAt=[DateTimeOffset]::Now.ToString('o'); ReadOnly=$true
    Devices=$devices; Services=$services; Processes=$processes; TcpListeners=$listeners
    NamedPipes=$pipes; HidInterfaces=$hid; AudioEndpoints=$audio; InstalledFiles=$files; Errors=@($probeErrors.ToArray())
}
$destination = Join-Path $OutputDirectory 'machine-inventory.json'
$report | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output "Saved $destination"
Write-Output "Devices: $($devices.Count); HID interfaces: $($hid.Count); errors: $($probeErrors.Count)"
if ($probeErrors.Count) { $probeErrors | Write-Warning }
