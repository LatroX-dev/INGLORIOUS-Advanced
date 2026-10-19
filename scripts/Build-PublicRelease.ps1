#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('SourceChecks','Windows')][string]$Mode='SourceChecks',
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../artifacts/public-build'),
    [string]$FirmwareDirectory,
    [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Build output must be a new directory.'}
if(-not$output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must be inside this checkout.'}
New-Item -ItemType Directory -Path $output|Out-Null
function Invoke-Checked([string]$Command,[string[]]$Arguments){
    Push-Location $root
    try{
        & $Command @Arguments
        if($LASTEXITCODE-ne0){throw ($Command+' failed with exit '+$LASTEXITCODE)}
    }finally{Pop-Location}
}
$sdk=(& dotnet --version).Trim()
$expectedSdk=(Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw|ConvertFrom-Json).sdk.version
if($sdk-cne$expectedSdk){throw ('Install .NET SDK '+$expectedSdk+' from Microsoft before building.')}
$tests=@('InGlorious.Checks','InGlorious.BoardRegistryChecks','InGlorious.ModelChecks','InGlorious.DiscoveryChecks','InGlorious.OnboardChecks','InGlorious.MacroLibraryChecks','InGlorious.KeyBindingChecks','InGlorious.LightingPresetChecks','InGlorious.PreferencesChecks','InGlorious.RecordingChecks','InGlorious.PendingChecks','InGlorious.LightingTestChecks','InGlorious.AutostartChecks','InGlorious.StartupGuardChecks','InGlorious.BackgroundChecks','InGlorious.AppStartupChecks','InGlorious.AdapterFileChecks','InGlorious.SoftwareUpdateChecks')
$executedTests=0
$tests += @('InGlorious.FeedbackChecks','InGlorious.CoreImportChecks')
foreach($suite in $tests){
    [xml]$testProject=Get-Content -LiteralPath (Join-Path $root ('tests/'+$suite+'/'+$suite+'.csproj'))
    if(-not$IsWindows-and$testProject.Project.PropertyGroup.TargetFramework.EndsWith('-windows')){continue}
    Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root ('tests/'+$suite+'/'+$suite+'.csproj')),'-c','Release')
    $executedTests++
}
Invoke-Checked 'dotnet' @('restore',(Join-Path $root 'src/InGlorious.Desktop/InGlorious.Desktop.csproj'),'--locked-mode')
Invoke-Checked 'dotnet' @('build',(Join-Path $root 'src/InGlorious.Desktop/InGlorious.Desktop.csproj'),'-c','Release','--no-restore')
if($IsWindows){
    Invoke-Checked 'dotnet' @('build',(Join-Path $root 'src/InGlorious.Core/InGlorious.Core.csproj'),'-c','Release',('-p:SetupAssetsDirectory='+ (Join-Path $output 'no-firmware-assets')))
    Invoke-Checked 'dotnet' @('build',(Join-Path $root 'tools/InGlorious.FirmwareAdapter/InGlorious.FirmwareAdapter.csproj'),'-c','Release',('-p:NativeAssetDirectory='+ (Join-Path $output 'no-native-assets')))
}
if(-not$IsWindows){Invoke-Checked 'bash' @((Join-Path $root 'tests/PosixNativeChecks/check-dependency.sh'))}
if($Mode-eq'SourceChecks'){
    [pscustomobject]@{mode=$Mode;tests=$executedTests;firmwareBundled=$false;hardwareAccess=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'result.json')
    return
}
if(-not$IsWindows){throw 'The full firmware installer release is currently Windows x64 only.'}
$dependencies=& (Join-Path $PSScriptRoot 'Initialize-BuildDependencies.ps1') -Scope Native -Offline:$Offline
if(-not$FirmwareDirectory){
    & (Join-Path $PSScriptRoot 'Initialize-BuildDependencies.ps1') -Scope Firmware -Offline:$Offline|Out-Host
    $FirmwareDirectory=Join-Path $output 'firmware'
    & (Join-Path $root 'firmware/Build-Firmware.ps1') -OutputDirectory $FirmwareDirectory
    # Generate neutral C# defaults for the independent comparison with linked ARM keymaps.
    foreach($family in @('port','gmmk3')){
        $fixtureDirectory=Join-Path $root ('artifacts/'+$(if($family-eq'port'){'port-fixtures'}else{'gmmk3-port-fixtures'}))
        Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root 'tests/InGlorious.OnboardChecks/InGlorious.OnboardChecks.csproj'),'-c','Release','--',('--'+$family+'-fixtures'),$fixtureDirectory)
    }
    Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root 'tests/InGlorious.OnboardChecks/InGlorious.OnboardChecks.csproj'),'-c','Release','--','--retained-loader-fixtures',(Join-Path $FirmwareDirectory 'fixtures'))
    Invoke-Checked 'python' @((Join-Path $root 'firmware/Verify-CandidateBuild.py'),$FirmwareDirectory)
    foreach($runner in @('tests/InGlorious.ColorSequenceChecks/Run-ColorSequenceChecks.ps1','firmware/tests/Run-RuntimeChecks.ps1','firmware/tests/Run-EffectsChecks.ps1','firmware/tests/Run-BulkChecks.ps1','firmware/tests/Run-StorageChecks.ps1','firmware/tests/Run-EngineeringChecks.ps1','firmware/tests/Run-ProfileSelectorChecks.ps1','firmware/tests/Run-ProfileBankChecks.ps1','firmware/tests/Run-ProfileFlashChecks.ps1','tests/TimingChecks/Run-TimingChecks.ps1','tests/InGlorious.PreviewChecks/Run-PreviewChecks.ps1')){
        Invoke-Checked 'pwsh' @('-NoProfile','-File',(Join-Path $root $runner))
    }
    # The independent port fixtures supply physical geometry and a neutral profile.
    Invoke-Checked 'pwsh' @('-NoProfile','-File',(Join-Path $root 'firmware/tests/Run-PortChecks.ps1'),'-Board','gmmk3-pro-100-iso','-FixtureDirectory',(Join-Path $FirmwareDirectory 'fixtures'))
    Invoke-Checked 'pwsh' @('-NoProfile','-File',(Join-Path $root 'firmware/tests/Run-ProfileRuntimeChecks.ps1'),'-FixtureDirectory',(Join-Path $FirmwareDirectory 'fixtures'))
    # Cross-check the C writer's valid, interrupted and unsafe records in the host reader.
    $bankFixtures=Join-Path $output 'profile-bank-fixtures'
    Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root 'tests/InGlorious.OnboardChecks/InGlorious.OnboardChecks.csproj'),'-c','Release','--','--hardware-profile-fixtures',$bankFixtures)
    $compilerDirectory=Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
    $fixtureExporter=Join-Path $bankFixtures 'export-fixtures.exe'
    $priorPath=$env:PATH
    try{
        $env:PATH=$compilerDirectory+[IO.Path]::PathSeparator+$priorPath
        Invoke-Checked (Join-Path $compilerDirectory 'gcc.exe') @('-std=c11','-O2','-Wall','-Wextra','-Werror','-pedantic',(Join-Path $root 'firmware/inglorious/ig_profile_bank.c'),(Join-Path $root 'firmware/tests/profile_bank_fixtures.c'),'-o',$fixtureExporter)
        Invoke-Checked $fixtureExporter @((Join-Path $bankFixtures 'payload.bin'),$bankFixtures)
    }finally{$env:PATH=$priorPath}
    Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root 'tests/InGlorious.OnboardChecks/InGlorious.OnboardChecks.csproj'),'-c','Release','--','--profile-bank-fixtures',$bankFixtures)
}
$FirmwareDirectory=[IO.Path]::GetFullPath($FirmwareDirectory)
# Existing binary input is accepted only against the checked-in, reviewed release catalog.
$catalog=Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Core/SetupFirmwareCatalog.json') -Raw|ConvertFrom-Json
foreach($target in $catalog.targets){
    $file=Join-Path $FirmwareDirectory $target.file
    if(-not(Test-Path -LiteralPath $file)-or(Get-Item -LiteralPath $file).Length-ne$target.bytes-or(Get-FileHash -LiteralPath $file).Hash-cne$target.sha256){throw ('Firmware build differs from the reviewed release: '+$target.file+'. Review and version firmware separately; this script never changes firmware trust.')}
}
$adapter=Join-Path $output 'adapter'
Invoke-Checked 'dotnet' @('publish',(Join-Path $root 'tools/InGlorious.FirmwareAdapter/InGlorious.FirmwareAdapter.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:ContinuousIntegrationBuild=true','-o',$adapter,('-p:NativeAssetDirectory='+$dependencies.nativeAssetDirectory))
Invoke-Checked (Join-Path $adapter 'InGlorious.FirmwareAdapter.exe') @('--self-test','--firmware-directory',$FirmwareDirectory)
$assets=Join-Path $output 'assets';$toolCatalog=Join-Path $output 'SetupToolCatalog.json'
& (Join-Path $PSScriptRoot 'Prepare-SetupAssets.ps1') -AdapterDirectory $adapter -FirmwareDirectory $FirmwareDirectory -Destination $assets -ToolCatalogPath $toolCatalog -UpdateToolCatalog|Out-Host
$priorFirmware=$env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY
try{
    $env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY=$FirmwareDirectory
    Invoke-Checked 'dotnet' @('run','--project',(Join-Path $root 'tests/InGlorious.SetupChecks/InGlorious.SetupChecks.csproj'),'-c','Release',('-p:SetupAssetsDirectory='+$assets),('-p:SetupToolCatalogPath='+$toolCatalog))
}finally{$env:INGLORIOUS_TEST_FIRMWARE_DIRECTORY=$priorFirmware}
$publish=Join-Path $output 'application'
Invoke-Checked 'dotnet' @('publish',(Join-Path $root 'src/InGlorious.Core/InGlorious.Core.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:ContinuousIntegrationBuild=true','-o',$publish,('-p:SetupAssetsDirectory='+$assets),('-p:SetupToolCatalogPath='+$toolCatalog))
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'src/InGlorious.Core/InGlorious.Core.csproj')
$version=[string]$project.Project.PropertyGroup.Version
$package=Join-Path $output ('INGLORIOUS-Advanced-'+$version+'-win-x64')
& (Join-Path $PSScriptRoot 'New-WindowsPackage.ps1') -PublishDirectory $publish -Destination $package -ExpectedVersion $version -ToolCatalogPath $toolCatalog -Create|Out-Host
$zip=$package+'.zip';Compress-Archive -LiteralPath $package -DestinationPath $zip
((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip))|Set-Content -LiteralPath ($zip+'.sha256')
$installer=& (Join-Path $PSScriptRoot 'Build-WindowsInstaller.ps1') -PackageDirectory $package -ToolCatalogPath $toolCatalog -OutputDirectory (Join-Path $output 'installer') -Offline:$Offline
[pscustomobject]@{mode=$Mode;version=$version;firmwareRelease=$catalog.releaseId;models=$catalog.targets.Count;installer=$installer.installer;internalPackage=$zip;toolCatalog=$toolCatalog;hardwareAccess=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'result.json')
