[CmdletBinding()]
param([switch]$RunTests, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'OutputDirectory must not be empty.' }
$buildDirectory = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $root
try {
    dotnet restore src/Timbre.App --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App restore failed' }
    dotnet publish src/Timbre.App --no-restore -c Release -o $buildDirectory --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App build failed' }
    dotnet restore src/Timbre.Host --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host restore failed' }
    dotnet publish src/Timbre.Host --no-restore -c Release -o (Join-Path $buildDirectory 'host') --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host build failed' }
    if ($RunTests) {
        dotnet restore tests/Timbre.Tests --configfile NuGet.Config --nologo -v quiet
        if ($LASTEXITCODE) { throw 'Test restore failed' }
        dotnet run --project tests/Timbre.Tests --no-restore -c Release -- --report artifacts/tests.json --junit artifacts/tests.xml
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
    Write-Output ('Built '+(Join-Path $buildDirectory 'Timbre.exe'))
} finally { Pop-Location }
