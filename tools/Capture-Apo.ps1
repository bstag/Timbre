[CmdletBinding()]
param([ValidateSet('009f','0098')][string]$ProductId='009f', [Parameter(Mandatory)][string]$Name, [string]$Compare)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Capture name must be a simple file name.' }
$base='Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_'+$ProductId
$mutex=[Threading.Mutex]::OpenExisting($base+'_mutex')
$owned=$false; $map=$null; $view=$null
try {
    try { $owned=$mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $owned=$true; throw 'EPOS mutex was abandoned; capture cancelled.' }
    if (!$owned) { throw 'EPOS mutex timeout.' }
    $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($base+'_memory',[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
    $view=$map.CreateViewAccessor(0,0,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
    if ($view.Capacity -ne 4096) { throw 'Unexpected shared memory size.' }
    $bytes=New-Object byte[] 4096
    $null=$view.ReadArray(0,$bytes,0,$bytes.Length)
} finally { if ($view) {$view.Dispose()}; if ($map) {$map.Dispose()}; if ($owned) {$mutex.ReleaseMutex()}; $mutex.Dispose() }
$path=Join-Path $root ('artifacts/'+$Name+'.bin')
[IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null
[IO.File]::WriteAllBytes($path,$bytes)
$changes=@()
if ($Compare) {
    $before=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Compare))
    if ($before.Length -ne $bytes.Length) { throw 'Capture lengths differ.' }
    $changes=@(for ($i=0;$i -lt $bytes.Length;$i++) { if ($before[$i] -ne $bytes[$i]) { [pscustomobject]@{Offset=$i;Before=$before[$i];After=$bytes[$i];FloatAtAlignedOffset=[BitConverter]::ToSingle($bytes,($i -band -4))} } })
}
[pscustomobject]@{Path=$path;Version=@([BitConverter]::ToInt32($bytes,0),[BitConverter]::ToInt32($bytes,4));Changes=$changes} | ConvertTo-Json -Depth 5
