#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ToolchainDirectory,
    [Parameter(Mandatory)][string]$LibusbSourceArchive,
    [Parameter(Mandatory)][string]$DfuSourceArchive,
    [Parameter(Mandatory)][string]$Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'This native builder requires Windows x64.'}
function Assert-NoLink([string]$Path){
    $value=[IO.Path]::GetFullPath($Path)
    while($value){
        if((Test-Path -LiteralPath $value)-and((Get-Item -LiteralPath $value -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw ('Linked native build path: '+$value)}
        $value=[IO.Path]::GetDirectoryName($value)
    }
}
$output=[IO.Path]::GetFullPath($Destination)
Assert-NoLink $output
if(Test-Path -LiteralPath $output){throw 'Native build output must be a new directory.'}
if($output-match'\s'){throw 'The upstream configure/make build requires an output path without spaces.'}
if($output.IndexOfAny([char[]]"`r`n`"'``$;&|<>()")-ge0){throw 'Native build paths must not contain shell metacharacters.'}
$toolchain=[IO.Path]::GetFullPath($ToolchainDirectory);Assert-NoLink $toolchain
$bash=Join-Path $toolchain 'usr/bin/bash.exe'
if(-not(Test-Path -LiteralPath $bash)){throw 'The pinned QMK-MSYS toolchain is missing.'}
$pins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-source-dependencies.json') -Raw|ConvertFrom-Json
$archives=@(@{id='libusb';path=$LibusbSourceArchive;prefix='libusb-1.0.30/'},@{id='dfu-util';path=$DfuSourceArchive;prefix='dfu-util-0.11/'})
foreach($archive in $archives){
    $archive.path=[IO.Path]::GetFullPath($archive.path);Assert-NoLink $archive.path
    $pin=@($pins.sources|Where-Object id -CEQ $archive.id)
    if($pin.Count-ne1-or(Get-Item -LiteralPath $archive.path).Length-ne$pin[0].bytes-or(Get-FileHash -LiteralPath $archive.path).Hash-cne$pin[0].sha256){throw 'Native source archive checksum differs.'}
    $names=@(& tar -tf $archive.path)
    if($LASTEXITCODE-ne0-or$names.Count-eq0-or@($names|Where-Object {$_-match'(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)'-or-not$_.StartsWith($archive.prefix,[StringComparison]::Ordinal)}).Count){throw 'Unexpected native source archive path.'}
    $entries=@(& tar -tvf $archive.path)
    if($LASTEXITCODE-ne0-or@($entries|Where-Object {$_-notmatch'^[-d]'}).Count){throw 'Linked native source entries are not accepted.'}
}
New-Item -ItemType Directory -Path $output|Out-Null
foreach($archive in $archives){
    & tar -xf $archive.path -C $output
    if($LASTEXITCODE-ne0){throw 'Native source extraction failed.'}
}
$script=Join-Path $PSScriptRoot 'build-windows-native.sh';Assert-NoLink $script
& $bash --noprofile --norc ($script.Replace('\','/')) ($output.Replace('\','/')) *> (Join-Path $output 'build.log')
if($LASTEXITCODE-ne0){throw ('Windows native build failed; see '+(Join-Path $output 'build.log'))}
$native=Join-Path $output 'native';Assert-NoLink $native
$dll=Join-Path $native 'libusb-1.0.dll'
if((Get-Item -LiteralPath $dll).VersionInfo.FileVersion-notmatch'^1\.0\.30\.'){throw 'Built USB library version differs.'}
$files=@(Get-ChildItem -LiteralPath $native -File|ForEach-Object{Assert-NoLink $_.FullName;[ordered]@{path=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
if($files.Count-ne2){throw 'Unexpected native output file set.'}
$manifest=[ordered]@{schema=1;runtimeIdentifier='win-x64';libusbVersion='1.0.30';dfuUtilVersion='0.11';compiler='QMK-MSYS 1.12.0 / GCC 15.2.0';files=$files;hardwareAccess=$false}
$manifest|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding utf8
[pscustomobject]@{nativeAssetDirectory=$native;manifest=(Join-Path $output 'build-manifest.json');hardwareAccess=$false}
