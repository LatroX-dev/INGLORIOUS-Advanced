#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceCache=(Join-Path $PSScriptRoot '../artifacts/source-cache'),
    [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($IsWindows){throw 'Native Linux/macOS tools must be built on the matching operating system.'}
$platform=if($IsMacOS){'osx'}elseif($IsLinux){'linux'}else{throw 'Unsupported build host.'}
$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if($Runtime-cne($platform+'-'+$architecture)){throw 'The requested runtime must match this native build process.'}
function Assert-NoLink([string]$Path){
    for($value=[IO.Path]::GetFullPath($Path);$value;$value=[IO.Path]::GetDirectoryName($value)){
        if((Test-Path -LiteralPath $value)-and((Get-Item -LiteralPath $value -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked native-build paths are not allowed.'}
    }
}
$output=[IO.Path]::GetFullPath($OutputDirectory);$cache=[IO.Path]::GetFullPath($SourceCache)
Assert-NoLink $output;Assert-NoLink $cache
if(Test-Path -LiteralPath $output){throw 'Native build output must be a new directory.'}
$pins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-source-dependencies.json') -Raw|ConvertFrom-Json
if($pins.schema-ne1-or$pins.sources.Count-ne2){throw 'Unexpected native source catalog.'}
New-Item -ItemType Directory -Path $output|Out-Null
New-Item -ItemType Directory -Path $cache -Force|Out-Null
foreach($source in $pins.sources){
    if($source.file-cnotmatch'^[a-z0-9.-]+$'-or$source.directory-cnotmatch'^[a-z0-9.-]+$'){throw 'Invalid source archive name.'}
    $archive=Join-Path $cache $source.file;Assert-NoLink $archive
    if(-not(Test-Path -LiteralPath $archive)){
        if($Offline){throw ('Required source archive is missing: '+$source.file)}
        $partial=$archive+'.'+[Guid]::NewGuid().ToString('N')+'.partial'
        Invoke-WebRequest -Uri $source.url -OutFile $partial -MaximumRedirection 5
        if((Get-Item -LiteralPath $partial).Length-ne$source.bytes-or(Get-FileHash -LiteralPath $partial).Hash-cne$source.sha256){throw 'Downloaded native source differs from its pinned checksum.'}
        Move-Item -LiteralPath $partial -Destination $archive
    }
    if((Get-Item -LiteralPath $archive).Length-ne$source.bytes-or(Get-FileHash -LiteralPath $archive).Hash-cne$source.sha256){throw ('Cached source archive differs: '+$source.file)}
    $members=@(& tar -tf $archive)
    if($LASTEXITCODE-ne0-or@($members|Where-Object {$_-match'(^/|^[A-Za-z]:|(^|/)\.\.(/|$))'-or-not$_.StartsWith($source.directory+'/',[StringComparison]::Ordinal)}).Count){throw 'Unexpected source archive member.'}
    & tar -xf $archive -C $output
    if($LASTEXITCODE-ne0){throw 'Native source extraction failed.'}
}
& bash (Join-Path $PSScriptRoot 'build-posix-native.sh') $output $Runtime | Out-Host
if($LASTEXITCODE-ne0){throw 'Native tool compilation or relocation checks failed.'}
$native=Join-Path $output 'native'
$library=if($IsLinux){'libusb-1.0.so.0'}else{'libusb-1.0.dylib'}
$files=@(foreach($name in @($library,'dfu-util')){
    $path=Join-Path $native $name;Assert-NoLink $path
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Native build output missing: '+$name)}
    [ordered]@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
})
$catalog=Join-Path $output 'NativeToolCatalog.json'
[IO.File]::WriteAllText($catalog,([ordered]@{schema=1;runtimeIdentifier=$Runtime;files=$files}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$licenses=Join-Path $output 'Licenses';New-Item -ItemType Directory -Path $licenses|Out-Null
Copy-Item -LiteralPath (Join-Path $output 'libusb-1.0.30/COPYING') -Destination (Join-Path $licenses 'libusb-LGPL-2.1.txt')
Copy-Item -LiteralPath (Join-Path $output 'dfu-util-0.11/COPYING') -Destination (Join-Path $licenses 'dfu-util-GPL-2.0.txt')
[pscustomobject]@{runtimeIdentifier=$Runtime;nativeAssetDirectory=$native;nativeToolCatalogPath=$catalog;licenseDirectory=$licenses;hardwareAccess=$false}
