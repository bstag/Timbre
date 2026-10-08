[CmdletBinding()]
param(
    [string]$Root,
    [switch]$PortablePackage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DocumentationPaths.ps1')
if (!$Root) { $Root = Join-Path $PSScriptRoot '..' }
$Root = [IO.Path]::GetFullPath($Root)
if ($PortablePackage) {
    $documents = @(Get-ChildItem -LiteralPath $Root -File -Filter '*.md') +
        @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs') -File -Filter '*.md' -Recurse) +
        @(Get-Item -LiteralPath (Join-Path $Root 'checks/Fixtures/README.md'))
} else {
    $documents = @(Get-ChildItem -LiteralPath $Root -File -Filter '*.md') +
        @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs') -File -Filter '*.md' -Recurse) +
        @(Get-Item -LiteralPath (Join-Path $Root 'tests/README.md'), (Join-Path $Root 'tests/Timbre.Tests/Fixtures/README.md'),
            (Join-Path $Root 'tools/README.md'), (Join-Path $Root 'tools/portable/START-HERE.md'))
}
$failures = [Collections.Generic.List[string]]::new()
$checked = 0
foreach ($document in $documents) {
    $base = $document.DirectoryName
    # Portable templates are copied to the package root, where their links resolve.
    if (!$PortablePackage -and $document.FullName -eq (Join-Path $Root 'tools/portable/START-HERE.md')) { $base = $Root }
    $content = Get-Content -LiteralPath $document.FullName -Raw
    foreach ($match in [regex]::Matches($content, '\]\(([^)]+)\)')) {
        $link = $match.Groups[1].Value
        if ($link -match '^(https?://|mailto:|#)') { continue }
        $path = ($link -split '#',2)[0]
        $target = [IO.Path]::GetFullPath((Join-Path $base $path))
        # Dated evidence may be available locally but is intentionally excluded from Git/ZIPs.
        if ($target.StartsWith((Join-Path $Root 'artifacts') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith((Join-Path $Root 'preservation') + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $checked++
        if (!(Test-Path -LiteralPath $target)) {
            $failures.Add(((Get-DocumentationRelativePath $Root $document.FullName) + ': ' + $link))
        }
    }
}
if ($failures.Count) { throw ("Broken local documentation links:`n" + ($failures -join "`n")) }
Write-Output ("Documentation verified: $($documents.Count) files, $checked local file links.")
