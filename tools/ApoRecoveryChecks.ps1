function Test-WindowsAudioRecoveryReady($baseline,$current) {
    if (!$baseline -or @($baseline.Controls).Count -eq 0) { return $false }
    foreach ($prior in $baseline.Controls) {
        if ($prior.Audio.Status -ne 'Available') { continue }
        $rows=@($current.Controls | Where-Object DeviceIdentity -eq $prior.DeviceIdentity)
        if ($rows.Count -ne 1 -or $rows[0].Audio.Status -ne 'Available') { return $false }
    }
    return $true
}

function Test-ApoRecoveryReady($baseline,$current) {
    # Recovery readiness used by the hardware runner, separate from value comparison.
    if (!$baseline -or @($baseline.Controls).Count -eq 0) { return $false }
    foreach ($prior in $baseline.Controls) {
        $rows=@($current.Controls | Where-Object DeviceIdentity -eq $prior.DeviceIdentity)
        if ($rows.Count -ne 1) { return $false }
        foreach ($field in @('Audio','Microphone','Playback','Sidetone')) {
            if ($prior.$field.Status -eq 'Available' -and $rows[0].$field.Status -ne 'Available') { return $false }
        }
    }
    return $true
}

function Test-GsxConnectionState($snapshot,[string]$instance,[bool]$connected) {
    $gsx=@($snapshot.Endpoints | Where-Object {$_.Usb.VendorId -eq '1395' -and $_.Usb.ProductId -eq '0098'})
    if (@($gsx | Where-Object {$_.Usb.InstanceId -ne $instance}).Count) { throw 'A different physical GSX appeared during the reconnect test.' }
    if (!$connected) { return ($gsx.Count -eq 0) }
    $microphones=@($gsx | Where-Object Direction -eq 1)
    $outputs=@($gsx | Where-Object Direction -eq 0)
    if ($microphones.Count -gt 1 -or $outputs.Count -gt 1) { throw 'Ambiguous GSX endpoints appeared during reconnect.' }
    return ($microphones.Count -eq 1 -and $outputs.Count -eq 1)
}
