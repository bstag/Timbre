[CmdletBinding()]
param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $root
try {
    dotnet restore src/Timbre.App --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App restore failed' }
    dotnet publish src/Timbre.App --no-restore -c Release -o dist --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App build failed' }
    dotnet restore src/Timbre.Host --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host restore failed' }
    dotnet publish src/Timbre.Host --no-restore -c Release -o dist/host --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host build failed' }
    if ($RunTests) {
        dotnet restore tests/Timbre.Tests --configfile NuGet.Config --nologo -v quiet
        if ($LASTEXITCODE) { throw 'Test restore failed' }
        dotnet run --project tests/Timbre.Tests --no-restore -c Release -- --report artifacts/tests.json --junit artifacts/tests.xml
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
    Write-Output "Built $root\dist\Timbre.exe"
} finally { Pop-Location }
