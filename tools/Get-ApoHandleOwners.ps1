[CmdletBinding()]
param([ValidateSet('0098','009f')][string]$ProductId='0098',[string]$ReportPath)
$ErrorActionPreference='Stop'
if (!('EposResearch.ApoHandleOwners' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ApoHandleOwners.cs') }
$owners=[EposResearch.ApoHandleOwners]::Read($ProductId)
$report=[ordered]@{CollectedAtUtc=[DateTime]::UtcNow.ToString('o');ProductId=$ProductId;SettingsWrites=$false;CreatesObjects=$false;Scope='Existing EPOS object handle holders only; mapped views may also retain objects without an open section handle';Owners=@($owners)}
if ($ReportPath) { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
$report | ConvertTo-Json -Depth 8
