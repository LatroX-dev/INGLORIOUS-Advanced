#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$Destination,[string]$ExpectedVersion='0.6.55',[string]$ToolCatalogPath=(Join-Path $PSScriptRoot '../src/InGlorious.Core/SetupToolCatalog.json'),[switch]$Create)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Assert-NoLink([string]$Path) {
 $current=[IO.Path]::GetFullPath($Path)
 while($current){if((Test-Path -LiteralPath $current)-and((Get-Item -LiteralPath $current -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked package paths are not allowed.'};$current=[IO.Path]::GetDirectoryName($current)}
}
$source=[IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\');$target=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
Assert-NoLink $source;Assert-NoLink $target
if($ExpectedVersion-notmatch'^\d+\.\d+\.\d+$'-or$target-notmatch'^[A-Za-z]:\\'-or([IO.DriveInfo]::new([IO.Path]::GetPathRoot($target))).DriveType-eq[IO.DriveType]::Network){throw 'ExpectedVersion and a local destination are required.'}
if((Test-Path -LiteralPath $target)-or$target.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)-or$source.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package destination must be new and separate from the publish directory.'}
$entry=Join-Path $source 'INGLORIOUS Advanced.exe'
if(-not(Test-Path -LiteralPath $entry)-or(Get-Item -LiteralPath $entry).VersionInfo.ProductVersion.Split('+')[0]-cne$ExpectedVersion){throw 'Published executable does not match ExpectedVersion.'}
foreach($legacyEntry in @('InGlorious Core.exe','InGlorious Core.dll','InGlorious Core.deps.json','InGlorious Core.runtimeconfig.json')){if(Test-Path -LiteralPath (Join-Path $source $legacyEntry)){throw 'Mixed Core/Advanced application entry files are not allowed.'}}
foreach($required in @('INGLORIOUS Advanced.dll','INGLORIOUS Advanced.deps.json','INGLORIOUS Advanced.runtimeconfig.json','coreclr.dll','System.Private.CoreLib.dll')){if(-not(Test-Path -LiteralPath (Join-Path $source $required))){throw ('Self-contained Windows publish file missing: '+$required)}}
$reviewedAssets=@{}
$catalogRoot=Join-Path $PSScriptRoot '../src/InGlorious.Core'
$firmwareCatalog=Get-Content -LiteralPath (Join-Path $catalogRoot 'SetupFirmwareCatalog.json') -Raw | ConvertFrom-Json
foreach($asset in $firmwareCatalog.targets){$reviewedAssets['Firmware/'+$asset.file]=$asset}
$toolCatalog=Get-Content -LiteralPath $ToolCatalogPath -Raw | ConvertFrom-Json
foreach($asset in $toolCatalog.files){$reviewedAssets['Tools/FirmwareAdapter/'+$asset.path]=$asset}
$licenseSources=@{'Licenses/INGLORIOUS-GPL-3.0.txt'='LICENSE';'Licenses/COPYRIGHT.md'='COPYRIGHT.md';'Licenses/THIRD-PARTY-NOTICES.md'='THIRD-PARTY-NOTICES.md';'Licenses/Oxanium-OFL.txt'='src/InGlorious.Core/Assets/Fonts/Oxanium-OFL.txt'}
foreach($name in @('Apache-2.0.txt','Avalonia-BuildServices-MIT.txt','Avalonia-MIT.txt','Desktop-ThirdParty.txt','dotnet-WindowsDesktop-MIT.txt','dotnet-Runtime-MIT.txt','dotnet-Runtime-ThirdParty.txt','GPL-2.0.txt','HidSharp-Apache-2.0.txt','LGPL-2.1.txt','MicroCom-MIT.txt','Tmds-DBus-MIT.txt')){$licenseSources['Licenses/'+$name]='Licenses/'+$name}
foreach($entry in $licenseSources.GetEnumerator()){
 $file=Join-Path $PSScriptRoot ('../'+$entry.Value)
 $reviewedAssets[$entry.Key]=[pscustomobject]@{bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash}
}
foreach($relative in $reviewedAssets.Keys){
 $path=Join-Path $source $relative;Assert-NoLink $path;$asset=$reviewedAssets[$relative]
 if(-not(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-Item -LiteralPath $path).Length-ne$asset.bytes-or(Get-FileHash -LiteralPath $path).Hash-cne$asset.sha256){throw ('Reviewed firmware asset missing or changed: '+$relative)}
}
$runtimeFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($name in @('INGLORIOUS Advanced.exe','INGLORIOUS Advanced.dll','INGLORIOUS Advanced.deps.json','INGLORIOUS Advanced.runtimeconfig.json')){[void]$runtimeFiles.Add($name)}
$deps=Get-Content -LiteralPath (Join-Path $source 'INGLORIOUS Advanced.deps.json') -Raw|ConvertFrom-Json -AsHashtable
if($deps.runtimeTarget.name-notmatch'^\.NETCoreApp,Version=v9\.0/win-x64$'-or-not$deps.targets.Contains($deps.runtimeTarget.name)){throw 'Unexpected published runtime target.'}
foreach($library in $deps.targets[$deps.runtimeTarget.name].GetEnumerator()){
 if($library.Key-cne('INGLORIOUS Advanced/'+$ExpectedVersion)-and$library.Key-cnotmatch'^runtimepack\.Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64/9\.0\.20$'){throw ('Unreviewed published dependency: '+$library.Key)}
 foreach($kind in @('runtime','native','resources')){
  if(-not$library.Value.Contains($kind)){continue}
  foreach($asset in $library.Value[$kind].Keys){
   if($asset-match'(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)'-or[IO.Path]::GetExtension($asset)-notin@('.dll','.exe','.dat')){throw 'Unexpected runtime asset path.'}
   [void]$runtimeFiles.Add($asset)
  }
 }
}
# .NET omits WindowsDesktop satellite resources from deps.json. Take their exact
# names and bytes from the official runtime package pinned by SDK 9.0.318.
$desktopRuntime='runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/9.0.20'
if(-not$deps.targets[$deps.runtimeTarget.name].Contains($desktopRuntime)){throw 'The reviewed WindowsDesktop runtime is missing.'}
if(-not$deps.targets[$deps.runtimeTarget.name].Contains('runtimepack.Microsoft.NETCore.App.Runtime.win-x64/9.0.20')){throw 'The reviewed .NET runtime is missing.'}
$globalPackages=(& dotnet nuget locals global-packages --list)
if($LASTEXITCODE-ne0-or$globalPackages-notmatch'^global-packages:\s+(.+)$'){throw 'Could not locate the restored WindowsDesktop runtime package.'}
$runtimePackage=Join-Path $Matches[1] 'microsoft.windowsdesktop.app.runtime.win-x64/9.0.20/microsoft.windowsdesktop.app.runtime.win-x64.9.0.20.nupkg'
Assert-NoLink $runtimePackage
if(-not(Test-Path -LiteralPath $runtimePackage)-or(Get-FileHash -LiteralPath $runtimePackage).Hash-cne'A1920B48F7540CA97742893F87DE74FB26EE4AA1B0DC32467C91FA3E3CD41A3C'){throw 'Restored WindowsDesktop runtime package differs from the reviewed official package.'}
$runtimeArchive=[IO.Compression.ZipFile]::OpenRead($runtimePackage)
try {
 $resourceCount=0;$resourcePrefix='runtimes/win-x64/lib/net9.0/'
 foreach($entry in $runtimeArchive.Entries){
  if(-not$entry.FullName.StartsWith($resourcePrefix,[StringComparison]::Ordinal)){continue}
  $relative=$entry.FullName.Substring($resourcePrefix.Length)
  if($relative-cnotmatch'^[a-z]{2}(?:-[A-Za-z0-9]{2,8})?/[A-Za-z0-9_.-]+\.resources\.dll$'){continue}
  $parentAssembly=[IO.Path]::GetFileName($relative).Replace('.resources.dll','.dll')
  if(-not$runtimeFiles.Contains($parentAssembly)){continue}
  $stream=$entry.Open()
  try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
  $published=Join-Path $source $relative;Assert-NoLink $published
  if(-not(Test-Path -LiteralPath $published)-or(Get-Item -LiteralPath $published).Length-ne$entry.Length-or(Get-FileHash -LiteralPath $published).Hash-cne$hash){throw ('WindowsDesktop resource is missing or changed: '+$relative)}
  $reviewedAssets[$relative]=[pscustomobject]@{bytes=$entry.Length;sha256=$hash};$resourceCount++
 }
 if($resourceCount-eq0){throw 'The reviewed runtime package contains no satellite resources.'}
} finally {$runtimeArchive.Dispose()}
$records=@(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object Extension -ne '.pdb' | Sort-Object FullName | ForEach-Object {
 Assert-NoLink $_.FullName
 $relative=[IO.Path]::GetRelativePath($source,$_.FullName).Replace('\','/')
 if(-not$reviewedAssets.ContainsKey($relative)-and-not$runtimeFiles.Contains($relative)){throw ('Unexpected file in publish directory: '+$relative)}
 [ordered]@{path=$relative;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$installer=Join-Path $PSScriptRoot 'Install-InGlorious.ps1'
$manifest=[ordered]@{schema=1;product='INGLORIOUS Advanced';version=$ExpectedVersion;platform='win-x64';entryPoint='INGLORIOUS Advanced.exe';installerSha256=(Get-FileHash -LiteralPath $installer).Hash;files=$records}
if(-not$Create){[pscustomobject]@{mode='plan';destination=$target;version=$ExpectedVersion;files=$records.Count;verifiedFiles=$records;hardwareAccess=$false;networkAccess=$false};return}
# Claim the destination itself before creating descendants; never merge another package tree.
New-Item -ItemType Directory -Path $target -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path (Join-Path $target 'application') | Out-Null
$locks=[Collections.Generic.List[IO.FileStream]]::new()
try {
 foreach($file in $records){$path=Join-Path $source $file.path;$locks.Add([IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read));if((Get-FileHash -LiteralPath $path).Hash-ne$file.sha256){throw 'Published source changed during packaging.'};$copy=Join-Path (Join-Path $target 'application') $file.path;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force|Out-Null;Copy-Item -LiteralPath $path -Destination $copy;if((Get-FileHash -LiteralPath $copy).Hash-ne$file.sha256){throw 'Package copy verification failed.'}}
 Copy-Item -LiteralPath $installer -Destination $target
 if((Get-FileHash -LiteralPath (Join-Path $target 'Install-InGlorious.ps1')).Hash-ne$manifest.installerSha256){throw 'Installer changed during packaging.'}
 [IO.File]::WriteAllText((Join-Path $target 'package-manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $target 'Install.cmd'),"@echo off`r`npowershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-InGlorious.ps1`" -Install`r`nif errorlevel 1 pause`r`n",[Text.ASCIIEncoding]::new())
 [IO.File]::WriteAllText((Join-Path $target 'README.txt'),"INGLORIOUS Advanced $ExpectedVersion`r`nStart Install.cmd for a per-user Windows x64 installation with explicit confirmation.`r`nThe app installer does not access devices or configure autostart. Firmware changes require a separate confirmation inside the app.`r`nShared Numpad/PRO dimming requires the app in the background; stored settings run independently.`r`nThe whole application folder can also run portably. Keep all files together.`r`nHashes detect damaged or changed package files; this package is not digitally signed.`r`n",[Text.UTF8Encoding]::new($false))
 [pscustomobject]@{mode='created';destination=$target;version=$ExpectedVersion;files=$records.Count;hardwareAccess=$false;networkAccess=$false;digitallySigned=$false}
} finally {foreach($handle in $locks){$handle.Dispose()}}
