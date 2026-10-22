# Pure session rules; production runner and fixture-free tests share these checks.
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
