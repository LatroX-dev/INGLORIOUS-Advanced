#Requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$AdapterDirectory,
 [Parameter(Mandatory)][string]$FirmwareDirectory,
 [string]$Destination=(Join-Path $PSScriptRoot '../artifacts/setup-assets'),
 [string]$ToolCatalogPath=(Join-Path $PSScriptRoot '../src/InGlorious.Core/SetupToolCatalog.json'),
 [switch]$UpdateToolCatalog
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$catalogRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/InGlorious.Core'))
$destinationPath=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $destinationPath){throw 'Use a new destination; existing assets are never overwritten.'}
function Assert-NoLink([string]$path){
 for($item=Get-Item -LiteralPath $path;$null-ne$item;$item=$item.Parent){
  if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked source paths are not accepted.'}
  if($item-is[IO.FileInfo]){$item=$item.Directory;if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked source directory.'}}
 }
}
$firmwareCatalog=Get-Content -LiteralPath (Join-Path $catalogRoot 'SetupFirmwareCatalog.json') -Raw|ConvertFrom-Json
$firmwareRoot=[IO.Path]::GetFullPath($FirmwareDirectory)
$adapterRoot=[IO.Path]::GetFullPath($AdapterDirectory)
Assert-NoLink $firmwareRoot;Assert-NoLink $adapterRoot
$records=@(Get-ChildItem -LiteralPath $adapterRoot -File -Recurse|Where-Object Extension -ne '.pdb'|Sort-Object FullName|ForEach-Object{
 Assert-NoLink $_.FullName
 [ordered]@{path=[IO.Path]::GetRelativePath($adapterRoot,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
foreach($required in @('InGlorious.FirmwareAdapter.exe','InGlorious.FirmwareAdapter.dll','native/libusb-1.0.dll','native/dfu-util-static.exe','coreclr.dll')){
 if($required-notin$records.path){throw ('Self-contained adapter file missing: '+$required)}
}
foreach($item in $firmwareCatalog.targets){
 $path=Join-Path $firmwareRoot $item.file;Assert-NoLink $path
 if((Get-Item -LiteralPath $path).Length-ne$item.bytes-or(Get-FileHash -LiteralPath $path).Hash-cne$item.sha256){throw ('Firmware does not match the reviewed catalog: '+$item.file)}
}
$toolCatalogPath=[IO.Path]::GetFullPath($ToolCatalogPath)
if(-not$UpdateToolCatalog){
 $existing=Get-Content -LiteralPath $toolCatalogPath -Raw|ConvertFrom-Json
 if(($existing.files|ConvertTo-Json -Compress)-cne($records|ConvertTo-Json -Compress)){throw 'Adapter differs from the reviewed tool catalog. Review the build before explicitly updating it.'}
}
New-Item -ItemType Directory -Path $destinationPath|Out-Null
foreach($record in $records){
 $target=Join-Path $destinationPath ('Tools/FirmwareAdapter/'+$record.path)
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
 Copy-Item -LiteralPath (Join-Path $adapterRoot $record.path) -Destination $target
 if((Get-FileHash -LiteralPath $target).Hash-cne$record.sha256){throw 'Adapter copy differs.'}
}
New-Item -ItemType Directory -Path (Join-Path $destinationPath 'Firmware')|Out-Null
foreach($item in $firmwareCatalog.targets){
 $target=Join-Path $destinationPath ('Firmware/'+$item.file)
 Copy-Item -LiteralPath (Join-Path $firmwareRoot $item.file) -Destination $target
 if((Get-FileHash -LiteralPath $target).Hash-cne$item.sha256){throw 'Firmware copy differs.'}
}
if($UpdateToolCatalog){
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($toolCatalogPath)) -Force|Out-Null
 [IO.File]::WriteAllText($toolCatalogPath,([ordered]@{schema=1;files=$records}|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{destination=$destinationPath;firmwareModels=$firmwareCatalog.targets.Count;toolFiles=$records.Count;hardwareAccess=$false}
