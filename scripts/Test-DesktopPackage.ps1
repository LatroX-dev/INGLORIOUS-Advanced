#Requires -Version 7.2
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$PublishDirectory,
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$Runtime,
 [Parameter(Mandatory)][string]$ExpectedVersion,
 [Parameter(Mandatory)][string]$ToolCatalogPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$package=[IO.Path]::GetFullPath($PackageDirectory)
$testRoot=Join-Path ([IO.Path]::GetDirectoryName($package)) ('package-check-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot|Out-Null
$arguments=@{PublishDirectory=$publish;Destination=(Join-Path $testRoot 'unused');Runtime=$Runtime;ExpectedVersion=$ExpectedVersion;ToolCatalogPath=$ToolCatalogPath;ValidateOnly=$true}
& (Join-Path $PSScriptRoot 'New-DesktopPackage.ps1') @arguments|Out-Host
$checked=1
foreach($name in @('personal-profile.json','original-firmware.bin','unexpected.dll','device.log')){
 $file=Join-Path $publish $name
 if(Test-Path -LiteralPath $file){throw 'Negative-test fixture already exists.'}
 try{
  [IO.File]::WriteAllText($file,'synthetic package rejection test')
  $rejected=$false
  try{& (Join-Path $PSScriptRoot 'New-DesktopPackage.ps1') @arguments|Out-Null}catch{if($_.Exception.Message-notlike'Unexpected file in publish directory:*'){throw};$rejected=$true}
  if(-not$rejected){throw ('Unreviewed file was accepted: '+$name)}
  $checked++
 }finally{Remove-Item -LiteralPath $file}
}
$manifest=Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw|ConvertFrom-Json
foreach($entry in $manifest.files){
 if($entry.path-match'(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)'){throw 'Unsafe package manifest member.'}
 $path=Join-Path $package $entry.path
 if((Get-Item -LiteralPath $path).Length-ne$entry.bytes-or(Get-FileHash -LiteralPath $path).Hash-cne$entry.sha256){throw 'Package manifest differs.'}
 $checked++
}
if(@(Get-ChildItem -LiteralPath $package -File -Recurse -Force).Count-ne($manifest.files.Count+1)){throw 'Unlisted package file.'}
$extract=Join-Path $testRoot 'extracted';New-Item -ItemType Directory -Path $extract|Out-Null
& tar -xzf ($package+'.tar.gz') -C $extract
if($LASTEXITCODE-ne0){throw 'Package cannot be extracted.'}
$extractedPackage=Join-Path $extract ([IO.Path]::GetFileName($package))
foreach($entry in $manifest.files){if((Get-FileHash -LiteralPath (Join-Path $extractedPackage $entry.path)).Hash-cne$entry.sha256){throw 'Archive contents differ.'};$checked++}
$appRoot=if($Runtime.StartsWith('osx-')){Join-Path $extractedPackage 'INGLORIOUS Advanced.app/Contents/Resources/application'}else{Join-Path $extractedPackage 'application'}
foreach($entry in @('INGLORIOUS Advanced','Tools/FirmwareAdapter/InGlorious.FirmwareAdapter','Tools/FirmwareAdapter/native/dfu-util')){
 if(([IO.File]::GetUnixFileMode((Join-Path $appRoot $entry))-band[IO.UnixFileMode]::UserExecute)-eq0){throw 'An executable lost its execute permission.'};$checked++
}
if($Runtime.StartsWith('osx-')){
 $bundle=Join-Path $extractedPackage 'INGLORIOUS Advanced.app'
 $launcher=Join-Path $bundle 'Contents/MacOS/INGLORIOUS Advanced'
 if(([IO.File]::GetUnixFileMode($launcher)-band[IO.UnixFileMode]::UserExecute)-eq0){throw 'Application launcher lost its execute permission.'};$checked++
 & codesign --verify --deep --strict $bundle
 if($LASTEXITCODE-ne0){throw 'Extracted application bundle signature verification failed.'};$checked++
 foreach($entry in @('INGLORIOUS Advanced','Tools/FirmwareAdapter/InGlorious.FirmwareAdapter','Tools/FirmwareAdapter/native/dfu-util','Tools/FirmwareAdapter/native/libusb-1.0.dylib')){
  & codesign --verify --strict (Join-Path $appRoot $entry)
  if($LASTEXITCODE-ne0){throw ('Extracted native signature verification failed: '+$entry)};$checked++
 }
 # The relocated, extracted bundle must forward every offline argument. This
 # also exercises payload lookup and resources without opening any HID device.
 $smoke=Join-Path $testRoot 'extracted-bundle-smoke.txt'
 & $launcher --smoke $smoke --language en
 if($LASTEXITCODE-ne0-or-not(Test-Path -LiteralPath $smoke)-or(Get-Content -LiteralPath $smoke -Raw)-notmatch'^PASS \d+ offline desktop checks;'){throw 'Extracted application bundle smoke failed.'};$checked++
}
# Rules must grant access only to the reviewed stock/runtime pairs and two update interfaces.
$rules=Get-Content -LiteralPath (Join-Path $PSScriptRoot '70-inglorious.rules')
$expectedPairs=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$stock=Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Core/StockDeviceCatalog.cs') -Raw
foreach($match in [regex]::Matches($stock,'new StockDeviceMatch\(0x([0-9A-F]{4}), 0x([0-9A-F]{4}),')){[void]$expectedPairs.Add(($match.Groups[1].Value+':'+$match.Groups[2].Value).ToLowerInvariant())}
1..16|ForEach-Object{[void]$expectedPairs.Add(('feed:{0:x4}' -f (0x1c00+$_)))}
$normalCount=$expectedPairs.Count
foreach($pair in @('0483:df11','342d:dfa0')){[void]$expectedPairs.Add($pair)}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($line in $rules){
 if($line.StartsWith('#')-or-not$line){continue}
 if($line-cnotmatch'^SUBSYSTEM=="(usb|hidraw)", ATTRS?\{idVendor\}=="([0-9a-f]{4})", ATTRS?\{idProduct\}=="([0-9a-f]{4})", TAG\+="uaccess"$'){throw 'USB rules must use exact IDs and active-session access.'}
 $pair=$Matches[2]+':'+$Matches[3]
 if(-not$expectedPairs.Contains($pair)-or-not$seen.Add($Matches[1]+':'+$pair)){throw 'Unexpected or duplicate USB permission rule.'};$checked++
}
if($seen.Count-ne(2*$normalCount+2)){throw 'USB permission rules are incomplete.'}
[pscustomobject]@{checks=$checked;runtime=$Runtime;hardwareAccess=$false;systemSettingsChanged=$false}
