# Use URI path handling so documentation tooling also works in Windows PowerShell 5.1.
function Get-DocumentationRelativePath([string]$BaseDirectory, [string]$Target) {
    $basePath=[IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $baseUri=[Uri]::new($basePath)
    $targetUri=[Uri]::new([IO.Path]::GetFullPath($Target))
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString())
}
