[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts\apo-initializer-recovery'))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source='C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuiteService'
$expectedHash='350D5EC4DEA1F6440C83DA82A0155AF1D7ACA02250E3F31EB7E3EBF472B8DD44'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The recovered addresses apply only to the inspected service image.' }
# Read static instructions and data; never load or execute the vendor image.
$image=[IO.File]::ReadAllBytes($source)
$pe=[BitConverter]::ToInt32($image,60)
$optional=$pe+24
$imageBase=[BitConverter]::ToUInt32($image,$optional+28)
$sections=$optional+[BitConverter]::ToUInt16($image,$pe+20)
$rva=0x7B9930-$imageBase
$constantOffset=$null
for($i=0;$i -lt [BitConverter]::ToUInt16($image,$pe+6);$i++) {
    $s=$sections+$i*40; $va=[BitConverter]::ToUInt32($image,$s+12); $size=[BitConverter]::ToUInt32($image,$s+16)
    if ($rva -ge $va -and $rva -lt $va+$size) { $constantOffset=[BitConverter]::ToUInt32($image,$s+20)+$rva-$va; break }
}
if ($null -eq $constantOffset) { throw 'Meter constant was not mapped to a PE section.' }
$meter=[BitConverter]::ToSingle($image,$constantOffset)
$lines=Get-Content -LiteralPath (Join-Path $root 'artifacts\service-disassembly.txt')
$start=@(for($i=0;$i -lt $lines.Count;$i++){if($lines[$i] -match '^\s+00553D10:'){ $i; break }})
$end=@(for($i=0;$i -lt $lines.Count;$i++){if($lines[$i] -match '^\s+00554027:'){ $i; break }})
if ($start.Count -ne 1 -or $end.Count -ne 1 -or $end[0] -le $start[0]) { throw 'Initializer instruction range was not found.' }
$memory=[byte[]]::new(4096)
$writes=[Collections.Generic.List[object]]::new()
$floatValue=$null
foreach($line in $lines[$start[0]..$end[0]]) {
    if ($line -match 'xorps\s+xmm0,xmm0') { $floatValue=[single]0 }
    if ($line -match 'movss\s+xmm0,dword ptr ds:\[007B9930h\]') { $floatValue=$meter }
    if ($line -match '^\s+(?<address>[0-9A-F]{8}): (?<op>mov|movss)\s+(?<width>byte|dword) ptr \[(?:eax|ecx|edx)(?:\+(?<offset>[0-9A-F]+)h?)?\],(?<value>0|2|4|xmm0)$') {
        $offset=if($Matches.offset){[Convert]::ToInt32($Matches.offset,16)}else{0}
        $wireType=if($Matches.op -eq 'movss'){'Float32'}elseif($Matches.width -eq 'byte'){'Byte'}else{'Int32'}
        $value=if($Matches.value -eq 'xmm0') { if($null -eq $floatValue){throw 'Unknown float constant'}; $floatValue } else { [int]$Matches.value }
        if ($wireType -eq 'Byte') { $memory[$offset]=[byte]$value }
        else {
            $bytes=if($wireType -eq 'Float32'){[BitConverter]::GetBytes([single]$value)}else{[BitConverter]::GetBytes([int]$value)}
            $bytes.CopyTo($memory,$offset)
        }
        $writes.Add([pscustomobject]@{InstructionAddress=$Matches.address;Offset=$offset;WireType=$wireType;Value=$value})
    }
}
if ($writes.Count -ne 60 -or $memory[0] -ne 2 -or $memory[4] -ne 4 -or $meter -ne -120) { throw ('Recovered write set did not match the reviewed initializer: '+$writes.Count) }
$destination=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$binary=Join-Path $destination 'apo-cold-initializer-009f.bin'
[IO.File]::WriteAllBytes($binary,$memory)
[pscustomobject]@{EvidenceType='Static initializer applied to a fresh zeroed page; not a live hardware capture';SourceSha256=$expectedHash;InitializerStart='00553D10';InitializerEnd='00554027';LogicalSize=2112;MappedViewSize=4096;MeterConstantAddress='007B9930';MeterConstant=$meter;Sha256=(Get-FileHash -LiteralPath $binary).Hash;Writes=$writes.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'apo-cold-initializer-009f.json') -Encoding UTF8
Write-Output ('Recovered initializer evidence: '+$destination)
