[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DocumentationPaths.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Test-App.ps1') }
& (Join-Path $PSScriptRoot 'Test-Documentation.ps1') -Root $root
$app=Join-Path $root 'dist'
$checks=Join-Path $root 'tests\EposControl.Tests\bin\Release\net9.0'
if ((Get-FileHash -LiteralPath (Join-Path $app 'EposControl.Core.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $checks 'EposControl.Core.dll')).Hash) { throw 'App/test binaries differ. Run Test-App.ps1 before packaging.' }
$release=Join-Path $root 'artifacts\releases'
$id=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$stage=Join-Path $release ('staging\'+$id)
$package=Join-Path $stage 'EPOS-Control'
New-Item -ItemType Directory -Path (Join-Path $package 'app'),(Join-Path $package 'checks\Fixtures'),(Join-Path $package 'docs') -Force | Out-Null
foreach($name in @('EposControl.exe','EposControl.dll','EposControl.Core.dll','EposControl.deps.json','EposControl.runtimeconfig.json','devices.json')) {
    Copy-Item -LiteralPath (Join-Path $app $name) -Destination (Join-Path $package 'app')
}
foreach($name in @('EposControl.Tests.dll','EposControl.Tests.deps.json','EposControl.Tests.runtimeconfig.json','EposControl.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $checks $name) -Destination (Join-Path $package 'checks')
}
Get-ChildItem -LiteralPath (Join-Path $checks 'Fixtures') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $package 'checks\Fixtures') }
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'portable') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $package }
$docRoot=Join-Path $root 'docs'
Get-ChildItem -LiteralPath $docRoot -File -Recurse | ForEach-Object {
    $target=Join-Path (Join-Path $package 'docs') ($_.FullName.Substring($docRoot.Length+1))
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
foreach($name in @('README.md','CONTRIBUTING.md','LICENSE','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package
}
# Rebase source-checkout links for the portable layout. Source-only files are
# described as checkout references instead of leaving broken offline hyperlinks.
Get-ChildItem -LiteralPath $package -File -Filter '*.md' -Recurse | ForEach-Object {
    $document=$_
    $relative=$document.FullName.Substring($package.Length+1)
    $original=if ($relative -eq 'checks\Fixtures\README.md') { Join-Path $root 'tests/EposControl.Tests/Fixtures/README.md' }
        elseif ($relative -eq 'START-HERE.md') { Join-Path $root 'START-HERE.md' }
        else { Join-Path $root $relative }
    $content=Get-Content -LiteralPath $document.FullName -Raw
    $content=[regex]::Replace($content, '\[([^\]]+)\]\(([^)]+)\)', [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $label=$match.Groups[1].Value
        $link=$match.Groups[2].Value
        if ($link -match '^(https?://|mailto:|#)') { return $match.Value }
        $parts=$link -split '#',2
        $sourceTarget=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($original)) $parts[0]))
        $sourceRelative=Get-DocumentationRelativePath $root $sourceTarget
        $packRelative=if ($sourceRelative.StartsWith('tests/EposControl.Tests/Fixtures/')) {
            'checks/Fixtures/'+$sourceRelative.Substring('tests/EposControl.Tests/Fixtures/'.Length)
        } else { $sourceRelative }
        $packTarget=Join-Path $package $packRelative
        if (!(Test-Path -LiteralPath $packTarget)) {
            $kind=if ($sourceRelative -match '^(artifacts|preservation)/') { 'local evidence' } else { 'source checkout' }
            return $label+' ('+$kind+': `'+$sourceRelative+'`)'
        }
        $rebased=Get-DocumentationRelativePath $document.DirectoryName $packTarget
        if ($parts.Count -eq 2) { $rebased+='#'+$parts[1] }
        return '['+$label+']('+$rebased+')'
    })
    [IO.File]::WriteAllText($document.FullName,$content,[Text.UTF8Encoding]::new($false))
}
$files=@(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
[pscustomobject]@{Package='EPOS Control';Architecture='win-x64';Framework='Microsoft.WindowsDesktop.App 9.x';GeneratedUtc=[DateTime]::UtcNow.ToString('o');Files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding UTF8
$zip=Join-Path $release ('EPOS-Control-win-x64-'+$id+'.zip')
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $package | ForEach-Object FullName) -DestinationPath $zip -CompressionLevel Optimal
$expanded=Join-Path $stage 'verified-extraction'
Expand-Archive -LiteralPath $zip -DestinationPath $expanded
foreach($entry in $files) {
    $path=Join-Path $expanded $entry.Path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256) { throw ('ZIP content does not match manifest: '+$entry.Path) }
}
& (Join-Path $PSScriptRoot 'Test-Documentation.ps1') -Root $expanded -PortablePackage
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
($hash+'  '+[IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ASCII
[pscustomobject]@{Zip=$zip;Sha256=$hash;Bytes=(Get-Item -LiteralPath $zip).Length;Files=$files.Count;ExtractedPath=$expanded} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'latest-package.json') -Encoding UTF8
Write-Output ('Transfer package: '+$zip)
Write-Output ('Verified extraction: '+$expanded)
