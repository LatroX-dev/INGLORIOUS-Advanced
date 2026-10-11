#Requires -Version 7.2
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$PublishDirectory,
 [Parameter(Mandatory)][string]$Destination,
 [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$Runtime,
 [Parameter(Mandatory)][string]$ExpectedVersion,
 [Parameter(Mandatory)][string]$ToolCatalogPath,
 [switch]$ValidateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Assert-NoLink([string]$Path){
 for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
  if((Test-Path -LiteralPath $part)-and((Get-Item -LiteralPath $part).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked package paths are not allowed.'}
 }
}
function Assert-Relative([string]$Path){
 if([string]::IsNullOrWhiteSpace($Path)-or$Path-match'(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)'){throw 'Unexpected package asset path.'}
}
$source=[IO.Path]::GetFullPath($PublishDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target=[IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
Assert-NoLink $source;Assert-NoLink $target
if($ExpectedVersion-cnotmatch'^\d+\.\d+\.\d+$'){throw 'A release version is required.'}
if((Test-Path -LiteralPath $target)-or(Test-Path -LiteralPath ($target+'.tar.gz'))-or$source-eq$target-or$target.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)-or$source.StartsWith($target+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)){throw 'Package output must be new and separate.'}
$reviewed=@{}
$firmware=Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Core/SetupFirmwareCatalog.json') -Raw|ConvertFrom-Json
foreach($asset in $firmware.targets){Assert-Relative $asset.file;$reviewed['Firmware/'+$asset.file]=$asset}
$tools=Get-Content -LiteralPath $ToolCatalogPath -Raw|ConvertFrom-Json
foreach($asset in $tools.files){Assert-Relative $asset.path;$reviewed['Tools/FirmwareAdapter/'+$asset.path]=$asset}
$nativeLibrary=if($Runtime-eq'linux-x64'){'libusb-1.0.so.0'}else{'libusb-1.0.dylib'}
foreach($required in @('InGlorious.FirmwareAdapter','InGlorious.FirmwareAdapter.dll','native/dfu-util',('native/'+$nativeLibrary))){
 if(-not$reviewed.ContainsKey('Tools/FirmwareAdapter/'+$required)){throw ('Native adapter is incomplete: '+$required)}
}
$licenses=@{'Licenses/INGLORIOUS-GPL-3.0.txt'='LICENSE';'Licenses/COPYRIGHT.md'='COPYRIGHT.md';'Licenses/THIRD-PARTY-NOTICES.md'='THIRD-PARTY-NOTICES.md';'Licenses/Oxanium-OFL.txt'='src/InGlorious.Core/Assets/Fonts/Oxanium-OFL.txt'}
foreach($name in @('Apache-2.0.txt','Avalonia-BuildServices-MIT.txt','Avalonia-MIT.txt','Desktop-ThirdParty.txt','dotnet-WindowsDesktop-MIT.txt','dotnet-Runtime-MIT.txt','dotnet-Runtime-ThirdParty.txt','GPL-2.0.txt','HidSharp-Apache-2.0.txt','LGPL-2.1.txt','MicroCom-MIT.txt','Tmds-DBus-MIT.txt')){$licenses['Licenses/'+$name]='Licenses/'+$name}
foreach($entry in $licenses.GetEnumerator()){
 $file=Join-Path $root $entry.Value
 $reviewed[$entry.Key]=@{bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash}
}
$deps=Get-Content -LiteralPath (Join-Path $source 'INGLORIOUS Advanced.deps.json') -Raw|ConvertFrom-Json -AsHashtable
$runtimeTarget='.NETCoreApp,Version=v9.0/'+$Runtime
if($deps.runtimeTarget.name-cne$runtimeTarget-or-not$deps.targets.ContainsKey($runtimeTarget)){throw 'Published runtime target differs.'}
$lock=(Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Desktop/packages.lock.json') -Raw|ConvertFrom-Json -AsHashtable).dependencies
if(-not$lock.ContainsKey('net9.0/'+$Runtime)){throw 'A reviewed runtime-specific dependency lock is required.'}
$locked=@{}
foreach($targetLock in @($lock['net9.0'],$lock['net9.0/'+$Runtime])){foreach($entry in $targetLock.GetEnumerator()){$locked[$entry.Key]=$entry.Value}}
$runtimeFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($name in @('INGLORIOUS Advanced','INGLORIOUS Advanced.dll','INGLORIOUS Advanced.deps.json','INGLORIOUS Advanced.runtimeconfig.json')){[void]$runtimeFiles.Add($name)}
$runtimePack='runtimepack.Microsoft.NETCore.App.Runtime.'+$Runtime+'/9.0.15'
foreach($library in $deps.targets[$runtimeTarget].GetEnumerator()){
 $parts=$library.Key.Split('/');$name=$parts[0];$version=$parts[1]
 if($library.Key-cne('INGLORIOUS Advanced/'+$ExpectedVersion)-and$library.Key-cne'InGlorious.Shared/1.0.0'-and$library.Key-cne$runtimePack){
  if(-not$locked.ContainsKey($name)-or$locked[$name].resolved-cne$version-or$deps.libraries[$library.Key].sha512-cne('sha512-'+$locked[$name].contentHash)){throw ('Unreviewed dependency: '+$library.Key)}
 }
 foreach($kind in @('runtime','native','resources')){
  if(-not$library.Value.ContainsKey($kind)){continue}
  foreach($asset in $library.Value[$kind].GetEnumerator()){
   Assert-Relative $asset.Key
   if([IO.Path]::GetFileName($asset.Key)-eq'_._'){continue}
   $relative=[IO.Path]::GetFileName($asset.Key)
   if($kind-eq'resources'){$relative=$asset.Value.locale+'/'+$relative;Assert-Relative $relative}
   [void]$runtimeFiles.Add($relative)
  }
 }
}
if(-not$deps.targets[$runtimeTarget].ContainsKey($runtimePack)){throw 'Reviewed self-contained runtime is missing.'}
$packages=(& dotnet nuget locals global-packages --list)
if($LASTEXITCODE-ne0-or$packages-notmatch'^global-packages:\s+(.+)$'){throw 'Could not locate the actual runtime licenses.'}
foreach($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')){
 $file=Join-Path $Matches[1] ('microsoft.netcore.app.runtime.'+$Runtime+'/9.0.15/'+$name)
 $reviewed['Licenses/dotnet-'+$Runtime+'-'+$name]=@{bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash}
}
foreach($relative in $reviewed.Keys){
 $file=Join-Path $source $relative;Assert-NoLink $file;$expected=$reviewed[$relative]
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)-or(Get-Item -LiteralPath $file).Length-ne$expected.bytes-or(Get-FileHash -LiteralPath $file).Hash-cne$expected.sha256){throw ('Reviewed asset missing or changed: '+$relative)}
}
foreach($name in @('INGLORIOUS Advanced','INGLORIOUS Advanced.dll','INGLORIOUS Advanced.deps.json','INGLORIOUS Advanced.runtimeconfig.json','System.Private.CoreLib.dll')){if(-not(Test-Path -LiteralPath (Join-Path $source $name))){throw ('Self-contained application is missing: '+$name)}}
$records=@(Get-ChildItem -LiteralPath $source -File -Recurse -Force|Where-Object Extension -ne '.pdb'|Sort-Object FullName|ForEach-Object{
 Assert-NoLink $_.FullName
 $relative=[IO.Path]::GetRelativePath($source,$_.FullName).Replace('\','/')
 if(-not$reviewed.ContainsKey($relative)-and-not$runtimeFiles.Contains($relative)){throw ('Unexpected file in publish directory: '+$relative)}
 [ordered]@{path=$relative;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if($ValidateOnly){[pscustomobject]@{files=$records.Count;runtime=$Runtime;hardwareAccess=$false};return}
if($IsWindows-or($Runtime.StartsWith('osx-')-and-not$IsMacOS)-or($Runtime-eq'linux-x64'-and-not$IsLinux)){throw 'Create this package on its matching native host.'}
New-Item -ItemType Directory -Path $target|Out-Null
$appRoot=if($IsMacOS){Join-Path $target 'INGLORIOUS Advanced.app/Contents/MacOS'}else{Join-Path $target 'application'}
New-Item -ItemType Directory -Path $appRoot -Force|Out-Null
foreach($record in $records){
 $from=Join-Path $source $record.path;$to=Join-Path $appRoot $record.path
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($to)) -Force|Out-Null
 Copy-Item -LiteralPath $from -Destination $to
 [IO.File]::SetUnixFileMode($to,[IO.File]::GetUnixFileMode($from))
 if((Get-FileHash -LiteralPath $to).Hash-cne$record.sha256){throw 'Package copy verification failed.'}
}
if($IsMacOS){
 $contents=Join-Path $target 'INGLORIOUS Advanced.app/Contents'
 $plist=@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>INGLORIOUS Advanced</string><key>CFBundleIdentifier</key><string>org.inglorious.advanced</string><key>CFBundleName</key><string>INGLORIOUS Advanced</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>$ExpectedVersion</string><key>CFBundleVersion</key><string>$ExpectedVersion</string><key>LSMinimumSystemVersion</key><string>12.0</string><key>NSHighResolutionCapable</key><true/></dict></plist>
"@
 [IO.File]::WriteAllText((Join-Path $contents 'Info.plist'),$plist,[Text.UTF8Encoding]::new($false))
 # Only the main app is signed here; never recursively re-sign the pinned adapter/native tools.
 & codesign --force --sign - --timestamp=none --entitlements (Join-Path $PSScriptRoot 'macos-entitlements.plist') (Join-Path $target 'INGLORIOUS Advanced.app')
 if($LASTEXITCODE-ne0){throw 'Application bundle signing failed.'}
 & codesign --verify --strict (Join-Path $target 'INGLORIOUS Advanced.app')
 if($LASTEXITCODE-ne0){throw 'Application bundle signature verification failed.'}
}
# Recheck every firmware/tool after packaging and signing.
foreach($relative in $reviewed.Keys){if((Get-FileHash -LiteralPath (Join-Path $appRoot $relative)).Hash-cne$reviewed[$relative].sha256){throw 'Packaging changed a reviewed asset.'}}
$instructions=if($IsMacOS){'Drag INGLORIOUS Advanced.app to Applications. This build is ad-hoc signed, not Apple notarized. macOS may require approval in Privacy & Security.'}else{'Run application/INGLORIOUS Advanced. See the included setup guide for USB permissions. Do not run the whole app as root.'}
[IO.File]::WriteAllText((Join-Path $target 'README.txt'),("INGLORIOUS Advanced $ExpectedVersion`n$instructions`nFirmware setup, backup and restore are guided inside the app.`nKeep all application files together. Numpad Bluetooth is not supported by INGLORIOUS firmware.`nShared Numpad/PRO dimming requires the app in the background; stored settings run independently.`n"),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $root 'docs/device-setup.md') -Destination (Join-Path $target 'device-setup.md')
if($IsLinux){
 foreach($name in @('70-inglorious.rules','Install-UsbPermissions.sh')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $target $name)}
 & chmod 755 (Join-Path $target 'Install-UsbPermissions.sh')
 if($LASTEXITCODE-ne0){throw 'USB permission helper mode could not be set.'}
}
$manifest=@(Get-ChildItem -LiteralPath $target -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($target,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[IO.File]::WriteAllText((Join-Path $target 'package-manifest.json'),([ordered]@{schema=1;product='INGLORIOUS Advanced';version=$ExpectedVersion;runtime=$Runtime;files=$manifest}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$archive=$target+'.tar.gz'
& tar -czf $archive -C ([IO.Path]::GetDirectoryName($target)) ([IO.Path]::GetFileName($target))
if($LASTEXITCODE-ne0){throw 'Native package archive failed.'}
[IO.File]::WriteAllText(($archive+'.sha256'),((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($archive)+"`n"),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{archive=$archive;files=$manifest.Count;firmwareModels=$firmware.targets.Count;runtime=$Runtime;hardwareAccess=$false}
