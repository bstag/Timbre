# Shared session rules and report I/O; no audio, device or service operations.
function Read-SupportReport([string]$Path) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if($stream.Length -gt 262144){throw 'Report exceeds its size limit.'}
        $bytes=[byte[]]::new([int]$stream.Length);$offset=0
        while($offset -lt $bytes.Length){$count=$stream.Read($bytes,$offset,$bytes.Length-$offset);if($count -eq 0){throw 'Incomplete report.'};$offset+=$count}
        if($stream.ReadByte() -ne -1){throw 'Report size changed during reading.'}
        return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
    }finally{$stream.Dispose()}
}
function Write-SupportReport([string]$Path,$Value,[scriptblock]$OnContention) {
    if([IO.Directory]::Exists($Path)){throw 'Report path is a directory.'}
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 25),[Text.UTF8Encoding]::new($false))
        for($attempt=0;;$attempt++){
            try {
                if([IO.File]::Exists($Path)){[IO.File]::Replace($temporary,$Path,[NullString]::Value)}else{[IO.File]::Move($temporary,$Path)}
                break
            }
            catch {
                $failure=$_.Exception.GetBaseException();$code=$failure.HResult -band 65535
                if($failure -is [ComponentModel.Win32Exception]){$code=$failure.NativeErrorCode}
                if($attempt -ge 20 -or $code -notin @(5,32,33)){throw}
                if($OnContention){& $OnContention ($attempt+1)}
                Start-Sleep -Milliseconds 25
            }
        }
    }finally{if([IO.File]::Exists($temporary)){try{[IO.File]::Delete($temporary)}catch{}}}
}
function Test-SupportController([int]$ExpectedId,[DateTimeOffset]$ExpectedStarted,$Observed) {
    return $ExpectedId -gt 0 -and $Observed -and $Observed.Id -eq $ExpectedId -and
        ([DateTimeOffset]$Observed.StartTime).UtcTicks -eq $ExpectedStarted.UtcTicks -and !$Observed.HasExited
}
function Assert-SupportHeartbeat($Report,[int]$ProcessId,[string]$Instance) {
    if (!$Report -or $Report.ProcessId -ne $ProcessId -or $Report.State -notin @('Connected','WaitingForDevice','Retrying') -or
        ($Report.Generation -isnot [int] -and $Report.Generation -isnot [long]) -or $Report.Generation -lt 0 -or $Report.Generation -gt [int]::MaxValue) { throw 'Invalid managed session heartbeat.' }
    if ($Report.State -eq 'Connected' -and ($Report.Endpoint.Usb.InstanceId -ine $Instance -or $Report.Generation -lt 1 -or !$Report.Effects)) {
        throw 'Managed session reported another device or incomplete processing.'
    }
    if ($Report.State -eq 'WaitingForDevice' -and ($Report.Endpoint -or $Report.Effects -or $Report.SettingsWrites)) { throw 'Absent-device heartbeat retained active processing.' }
}
function Get-SupportReportDirectory([string]$Root,[string]$Requested) {
    $parent=[IO.Path]::GetFullPath((Join-Path $Root 'artifacts\support-session'))
    $path=[IO.Path]::GetFullPath($Requested)
    if (!$path.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Session report must be inside artifacts\support-session.' }
    if (Test-Path -LiteralPath $path) {
        if (!(Test-Path -LiteralPath $path -PathType Container) -or @(Get-ChildItem -LiteralPath $path -Force | Where-Object Name -ne 'stop-session').Count) {
            throw 'A fresh session report directory is required.'
        }
    }
    return $path
}
function Test-SupportRecoveryStable([object[]]$Observations) {
    if ($Observations.Count -lt 3) { return $false }
    $previous=-1.0; $first=$null
    foreach($sample in $Observations) {
        $seconds=$sample.ElapsedSeconds
        if ($seconds -isnot [double] -and $seconds -isnot [single] -and $seconds -isnot [decimal] -and $seconds -isnot [int] -and $seconds -isnot [long]) { return $false }
        if ([double]::IsNaN($seconds) -or [double]::IsInfinity($seconds) -or $seconds -lt 0 -or $seconds -le $previous) { return $false }
        foreach($field in @('ControlsPreserved','FullBuffersPreserved','ServicesRunning','VendorOwnershipObserved')) {
            if ($sample.$field -isnot [bool] -or !$sample.$field) { return $false }
        }
        if ($null -eq $first) { $first=$seconds }; $previous=$seconds
    }
    return ($previous-$first) -ge 60
}
