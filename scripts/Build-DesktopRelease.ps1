#Requires -Version 7.2
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$Runtime,
 [Parameter(Mandatory)][string]$FirmwareDirectory,
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '../artifacts/desktop-release'),
 [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($IsWindows){throw 'Build this package on its native Linux/macOS host.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
$firmware=[IO.Path]::GetFullPath($FirmwareDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new build output directory.'}
if(-not$output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)){throw 'Output must be inside this checkout.'}
New-Item -ItemType Directory -Path $output|Out-Null
function Invoke-Checked([string]$Command,[string[]]$Arguments){
 Push-Location $root
 try{& $Command @Arguments|Out-Host;if($LASTEXITCODE-ne0){throw ($Command+' failed with exit '+$LASTEXITCODE)}}finally{Pop-Location}
}
$expectedSdk=(Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw|ConvertFrom-Json).sdk.version
if((& dotnet --version).Trim()-cne$expectedSdk){throw ('Install .NET SDK '+$expectedSdk+' before building.')}
$catalogPath=Join-Path $root 'src/InGlorious.Core/SetupFirmwareCatalog.json'
$catalog=Get-Content -LiteralPath $catalogPath -Raw|ConvertFrom-Json
if($catalog.targets.Count-ne15){throw 'Unexpected reviewed firmware target count.'}
foreach($entry in $catalog.targets){
 $file=Join-Path $firmware $entry.file
 if(-not(Test-Path -LiteralPath $file)-or(Get-Item -LiteralPath $file).Length-ne$entry.bytes-or(Get-FileHash -LiteralPath $file).Hash-cne$entry.sha256){throw ('Firmware differs from the reviewed release: '+$entry.file)}
}
$native=& (Join-Path $PSScriptRoot 'Build-PosixNativeTools.ps1') -Runtime $Runtime -OutputDirectory (Join-Path $output 'native-build') -Offline:$Offline
$adapter=Join-Path $output 'adapter'
Invoke-Checked 'dotnet' @('publish','tools/InGlorious.FirmwareAdapter/InGlorious.FirmwareAdapter.csproj','-c','Release','-r',$Runtime,'--self-contained','true','-p:DebugType=None','-p:ContinuousIntegrationBuild=true',('-p:NativeAssetDirectory='+$native.nativeAssetDirectory),('-p:NativeToolCatalogPath='+$native.nativeToolCatalogPath),'-o',$adapter)
# The original Windows distribution README is not provenance for these native builds.
Remove-Item -LiteralPath (Join-Path $adapter 'Licenses/dfu-util-binary-provenance.txt')
Copy-Item -LiteralPath (Join-Path $native.licenseDirectory 'dfu-util-GPL-2.0.txt') -Destination (Join-Path $adapter 'Licenses/dfu-util-GPL-2.0.txt')
Copy-Item -LiteralPath (Join-Path $native.licenseDirectory 'libusb-LGPL-2.1.txt') -Destination (Join-Path $adapter 'Licenses/libusb-LGPL-2.1.txt')
Invoke-Checked 'chmod' @('755',(Join-Path $adapter 'InGlorious.FirmwareAdapter'),(Join-Path $adapter 'native/dfu-util'))
if($IsMacOS){
 Invoke-Checked 'codesign' @('--force','--sign','-','--timestamp=none','--entitlements',(Join-Path $PSScriptRoot 'macos-entitlements.plist'),(Join-Path $adapter 'InGlorious.FirmwareAdapter'))
 Invoke-Checked 'codesign' @('--verify','--strict',(Join-Path $adapter 'InGlorious.FirmwareAdapter'))
}
Invoke-Checked (Join-Path $adapter 'InGlorious.FirmwareAdapter') @('--self-test','--firmware-directory',$firmware)
$assets=Join-Path $output 'assets';$toolCatalog=Join-Path $output 'SetupToolCatalog.json'
& (Join-Path $PSScriptRoot 'Prepare-SetupAssets.ps1') -AdapterDirectory $adapter -FirmwareDirectory $firmware -Runtime $Runtime -Destination $assets -ToolCatalogPath $toolCatalog -UpdateToolCatalog|Out-Host
$prior=$env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY
try{
 $env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY=$firmware
 Invoke-Checked 'dotnet' @('run','--project','tests/InGlorious.PortableSetupChecks/InGlorious.PortableSetupChecks.csproj','-c','Release',('-p:SetupToolCatalogPath='+$toolCatalog),('-p:SetupFirmwareCatalogPath='+$catalogPath))
}finally{$env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY=$prior}
Invoke-Checked 'dotnet' @('restore','src/InGlorious.Desktop/InGlorious.Desktop.csproj','--locked-mode')
$publish=Join-Path $output 'application'
Invoke-Checked 'dotnet' @('publish','src/InGlorious.Desktop/InGlorious.Desktop.csproj','-c','Release','-r',$Runtime,'--self-contained','true','-p:RestoreLockedMode=true','-p:DebugType=None','-p:ContinuousIntegrationBuild=true',('-p:SetupAssetsDirectory='+$assets),('-p:SetupToolCatalogPath='+$toolCatalog),('-p:SetupFirmwareCatalogPath='+$catalogPath),'-o',$publish)
& (Join-Path $PSScriptRoot 'Copy-RuntimeNotices.ps1') -PublishDirectory $publish -Runtime $Runtime
Invoke-Checked 'chmod' @('755',(Join-Path $publish 'INGLORIOUS Advanced'),(Join-Path $publish 'Tools/FirmwareAdapter/InGlorious.FirmwareAdapter'),(Join-Path $publish 'Tools/FirmwareAdapter/native/dfu-util'))
if($IsMacOS){
 Invoke-Checked 'codesign' @('--force','--sign','-','--timestamp=none','--entitlements',(Join-Path $PSScriptRoot 'macos-entitlements.plist'),(Join-Path $publish 'INGLORIOUS Advanced'))
}
if($IsLinux){Invoke-Checked 'xvfb-run' @('-a',(Join-Path $publish 'INGLORIOUS Advanced'),'--smoke')}else{Invoke-Checked (Join-Path $publish 'INGLORIOUS Advanced') @('--smoke')}
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Desktop/InGlorious.Desktop.csproj')
$version=[string]$project.Project.PropertyGroup.Version
# Capture the app on this native host. Screenshot mode uses only synthetic
# profiles and connection fixtures, never user settings or attached hardware.
$screenshots=Join-Path $output 'screenshots'
New-Item -ItemType Directory -Path $screenshots|Out-Null
$screenshotRecords=@()
foreach($device in @('pro-ansi','numpad')){
 $image=Join-Path $screenshots ($device+'-lighting.png')
 $captureArguments=@('--screenshot',$image,'--language','en','--device',$device,'--connected-devices',$device,'--preset')
 if($IsLinux){Invoke-Checked 'xvfb-run' (@('-a',(Join-Path $publish 'INGLORIOUS Advanced'))+$captureArguments)}
 else{Invoke-Checked (Join-Path $publish 'INGLORIOUS Advanced') $captureArguments}
 if(-not(Test-Path -LiteralPath $image -PathType Leaf)){throw ('Native screenshot was not created: '+$device)}
 $bytes=[IO.File]::ReadAllBytes($image)
 if($bytes.Length-lt24-or[Convert]::ToHexString($bytes[0..7])-cne'89504E470D0A1A0A'){throw ('Native screenshot is not a PNG: '+$device)}
 $screenshotRecords += [ordered]@{file=[IO.Path]::GetFileName($image);deviceId=$device;view='lighting';language='en';bytes=$bytes.Length;sha256=(Get-FileHash -LiteralPath $image).Hash}
}
[ordered]@{
 schema=1;operatingSystem=$(if($IsMacOS){'macOS'}else{'Linux'});runtime=$Runtime;appVersion=$version
 synthetic=$true;hardwareAccess=$false;userSettingsAccess=$false;images=$screenshotRecords
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $screenshots 'provenance.json') -Encoding utf8
$package=Join-Path $output ('INGLORIOUS-Advanced-'+$version+'-'+$Runtime)
& (Join-Path $PSScriptRoot 'New-DesktopPackage.ps1') -PublishDirectory $publish -Destination $package -Runtime $Runtime -ExpectedVersion $version -ToolCatalogPath $toolCatalog
& (Join-Path $PSScriptRoot 'Test-DesktopPackage.ps1') -PublishDirectory $publish -PackageDirectory $package -Runtime $Runtime -ExpectedVersion $version -ToolCatalogPath $toolCatalog|Out-Host
[pscustomobject]@{runtime=$Runtime;version=$version;firmwareRelease=$catalog.releaseId;models=$catalog.targets.Count;hardwareAccess=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'result.json')
