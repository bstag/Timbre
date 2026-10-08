[CmdletBinding()]
param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $root
try {
    dotnet restore src/EposControl.App --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App restore failed' }
    dotnet publish src/EposControl.App --no-restore -c Release -o dist --nologo -v quiet
    if ($LASTEXITCODE) { throw 'App build failed' }
    dotnet restore src/EposControl.Host --configfile NuGet.Config --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host restore failed' }
    dotnet publish src/EposControl.Host --no-restore -c Release -o dist/host --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Host build failed' }
    if ($RunTests) {
        dotnet restore tests/EposControl.Tests --configfile NuGet.Config --nologo -v quiet
        if ($LASTEXITCODE) { throw 'Test restore failed' }
        dotnet run --project tests/EposControl.Tests --no-restore -c Release -- --report artifacts/tests.json --junit artifacts/tests.xml
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
    Write-Output "Built $root\dist\EposControl.exe"
} finally { Pop-Location }
