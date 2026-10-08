[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\preservation'))
$ErrorActionPreference = 'Stop'
$source = 'C:\ProgramData\EPOS\Gaming Suite'
$destination = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))))
$null = New-Item -ItemType Directory -Path $destination -Force
# Preserve the cached installer, its metadata, drivers, and local presets.
# Authentication data, recordings, logs, and firmware payloads are excluded.
$items = @('Update\GamingSuite_1.12.2.1185','Config\GSADriver','Config\FrostDriver','CUSTOMPRESET')
$items += @('DeviceInfoConfig.txt','DeviceFeatures.txt','DeviceSettings.txt','GamingSuiteSettings.xml','FLAT.xml','LASTCREATEDCUSTOMPRESET.xml') | ForEach-Object { 'Config\' + $_ }
foreach ($item in $items) {
    $path = Join-Path $source $item
    if (-not (Test-Path -LiteralPath $path)) { Write-Warning "Missing $path"; continue }
    $target = Join-Path $destination $item
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath $path -Destination $target -Recurse
}
$manifest = @(Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
    [pscustomobject]@{ RelativePath=$_.FullName.Substring($destination.Length + 1); Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output "Preserved $($manifest.Count) files in $destination"
