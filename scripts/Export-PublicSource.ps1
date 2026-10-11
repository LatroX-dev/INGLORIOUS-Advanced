#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$Export,
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-NoLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Export refuses reparse points in source or destination paths.' }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}
function Get-Hash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)) }
function Write-New([string]$Path,[byte[]]$Bytes) {
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}
Assert-NoLink $root
if (-not (Get-Item -LiteralPath $root).PSIsContainer) { throw 'SourceRoot must be a directory.' }

# Exact relative paths only. New files never enter this list through recursion or a glob.
$app = @(
    'FirmwareAdapterProcess.cs','PosixDeviceSetupEnvironment.cs','PlatformUsbIdentity.cs','AppAutostart.cs','AppAutostartEditor.cs','WindowsInstallationGuard.cs','BackgroundApplicationSession.cs','WindowsTrayIcon.cs','MainWindow.Background.cs','ApplicationInstance.cs','DeviceSetupFirmwareSequence.cs',
    'App.xaml','App.xaml.cs','AppPreferences.cs','AppSettingsWindow.xaml','AppSettingsWindow.xaml.cs','BackupService.cs','ColorPaletteStore.cs','ColorPickerWindow.cs',
    'DeviceScanner.cs','StockDeviceCatalog.cs','StockFirmwareInspector.cs','InstallationIdentity.cs','KnownFirmwareImages.cs','DeviceSetupService.cs','DeviceSetupService.Storage.cs','WindowsDeviceSetupEnvironment.cs','DeviceSetupWindow.cs','DeviceSetupWindow.Checks.cs','DeviceSetupWindow.Completion.cs','MainWindow.Setup.cs','InGlorious.Core.csproj','MainWindow.xaml','MainWindow.xaml.cs','Models.cs','UserGuide.cs','HelpWindow.cs','MainWindow.Help.cs',
    'MainWindow.Startup.cs','MainWindow.StartupChecks.cs','MainWindow.Connections.cs','MainWindow.ConnectionChecks.cs','StartupDeviceLoader.cs','MainWindow.WorkflowChecks.cs','MainWindow.PendingEdits.cs','MainWindow.PendingEditsChecks.cs','PendingEditDecisionPolicy.cs',
    'MainWindow.NavigationChecks.cs','MainWindow.Animation.cs','MainWindow.AnimationChecks.cs','LightingPreviewEngine.cs',
    'Localization.cs','LocalizationUi.cs','MainWindow.Localization.cs','MainWindow.EffectColors.cs','MainWindow.Favorites.cs',
    'MainWindow.MacroLibrary.cs','MainWindow.OsActions.cs','MainWindow.Profiles.cs','MainWindow.ProfileChecks.cs','MainWindow.SharedBrightness.cs',
    'MacroLibraryWindow.cs','OsActionCatalog.cs','OsActionsWindow.cs','ProfileLibrary.cs','SharedBrightnessService.cs','SharedMacroLibrary.cs',
    'LightingPresetStore.cs','LightPresetsWindow.cs','MainWindow.LightingPresets.cs','MainWindow.NewFeaturesChecks.cs',
    'BoardDescriptorRegistry.cs','OnboardClient.cs','OnboardHidTransport.cs','OnboardProtocol.cs','StockHidContract.cs','StockProtocol.cs','TransferWindow.cs','TransferWindow.StatusChecks.cs',
    'MacroRecorderWindow.xaml','MacroRecorderWindow.xaml.cs','MacroRecording.cs',
    'FirmwareWindow.xaml','FirmwareWindow.xaml.cs','FirmwareWindow.Log.cs','FirmwareWindow.Version.cs','FirmwareWindow.Reconnect.cs','FirmwareService.cs','FirmwareRelease.cs','FirmwareChangelog.cs','FirmwareChangelogPanel.cs','FirmwareMessages.cs','FirmwareEvidenceStore.cs','FirmwareLightingTestService.cs','FirmwareGuideWindow.cs','MainWindow.Onboarding.cs','MainWindow.Standby.cs','UnsavedChangesWindow.cs','UiIcons.cs'
)
$allow = [Collections.Generic.List[string]]::new()
foreach ($name in $app) { $allow.Add('src/InGlorious.Core/'+$name) }
$allow.Add('src/InGlorious.Core/SetupFirmwareCatalog.json')
$allow.Add('src/InGlorious.Core/KnownFirmwareCatalog.json')
$allow.Add('src/InGlorious.Core/SetupToolCatalog.json')
$allow.Add('src/InGlorious.Core/Locales/device-setup-ui.json')
$allow.Add('src/InGlorious.Core/Locales/device-setup-service.json')
$allow.Add('src/InGlorious.Core/Locales/device-update.json')
$allow.Add('src/InGlorious.Core/Locales/startup-settings.json')
$allow.Add('reference/glorious-stock-devices.json')
$allow.Add('docs/setup-boot-gestures.md')
$allow.Add('docs/device-setup.md')
$allow.Add('scripts/Prepare-SetupAssets.ps1')
foreach ($name in @('Build-PosixNativeTools.ps1','build-posix-native.sh','posix-native-dependencies.json','Build-DesktopRelease.ps1','New-DesktopPackage.ps1','Test-DesktopPackage.ps1','macos-entitlements.plist','70-inglorious.rules','Install-UsbPermissions.sh')) { $allow.Add('scripts/'+$name) }
foreach ($name in @('Build-WindowsInstaller.ps1','Initialize-InstallerCompiler.ps1','Test-NativeWindowsInstaller.ps1','installer-dependencies.json')) { $allow.Add('scripts/'+$name) }
$allow.Add('packaging/windows/INGLORIOUS-Advanced.iss')
foreach ($suite in @('InGlorious.AutostartChecks','InGlorious.StartupGuardChecks','InGlorious.BackgroundChecks')) {
    $allow.Add('tests/'+$suite+'/'+$suite+'.csproj'); $allow.Add('tests/'+$suite+'/Program.cs')
}
$allow.Add('tests/InGlorious.BackgroundChecks/Paths.cs')
$adapterFiles=@('AdapterEngine.cs','Contracts.cs','Files.cs','InGlorious.FirmwareAdapter.csproj','NativeUsb.cs','NativeToolCatalog.cs','Program.cs','README.md','SelfTests.cs','Stm32Device.cs','WbProtocol.cs',
 'Licenses/dfu-util-binary-provenance.txt','Licenses/dfu-util-GPL-2.0.txt','Licenses/dotnet-MIT.txt','Licenses/dotnet-THIRD-PARTY-NOTICES.txt','Licenses/libusb-LGPL-2.1.txt','Licenses/README.txt','Licenses/Westberry-Apache-2.0.txt')
foreach($name in $adapterFiles){$allow.Add('tools/InGlorious.FirmwareAdapter/'+$name)}
foreach($suite in @('InGlorious.DiscoveryChecks','InGlorious.SetupChecks')){
 $allow.Add('tests/'+$suite+'/'+$suite+'.csproj');$allow.Add('tests/'+$suite+'/Program.cs')
}
foreach($name in @('CoordinatorChecks.cs','IdentityChecks.cs','StockFirmwareChecks.cs','PortableUsbChecks.cs','FirmwareSequenceChecks.cs')){$allow.Add('tests/InGlorious.SetupChecks/'+$name)}
$allow.Add('tests/InGlorious.PortableSetupChecks/InGlorious.PortableSetupChecks.csproj')
foreach ($name in @('desktop.json','dialogs.json','favorites.json','firmware.json','firmware-service.json','firmware-guide-simple.json','firmware-wizard-simple.json','firmware-routing.json','firmware-details.json','firmware-log.json','firmware-source.json','lighting-presets.json','macros.json','main-status.json','main.json','models.json','operations.json','preferences-profiles.json','sequences.json','standby.json','transfer-simple.json','startup.json','guide.json','slider-editor.json','model-support.json','connections.json','firmware-models.json')) { $allow.Add('src/InGlorious.Core/Locales/'+$name) }
$desktop = @('App.axaml','App.axaml.cs','EditorState.cs','InGlorious.Desktop.csproj','MainWindow.Actions.cs','MainWindow.Checks.cs','MainWindow.cs','MainWindow.Editors.cs','MainWindow.Icons.cs','MainWindow.Macros.cs','MainWindow.Presets.cs','MainWindow.Settings.cs','MainWindow.Startup.cs','MainWindow.StartupChecks.cs','MainWindow.Connections.cs','MainWindow.ConnectionsChecks.cs','MainWindow.Help.cs','MainWindow.HelpChecks.cs','MainWindow.Setup.cs','DeviceSetupWindow.cs','DeviceSetupWindow.Checks.cs','DeviceSetupWindow.Completion.cs','Program.cs','README.md','DesktopTrayIcon.cs','MainWindow.Background.cs','Shared/AssemblyInfo.cs','Shared/InGlorious.Shared.csproj','Shared/OnboardHidTransport.cs','Shared/PlatformDeviceService.cs')
foreach ($name in $desktop) { $allow.Add('src/InGlorious.Desktop/'+$name) }
foreach ($name in @('inglorious-mark.png','inglorious.ico')) { $allow.Add('src/InGlorious.Core/Assets/'+$name) }
foreach ($name in @('INGLORIOUS-Oxanium-SemiBold.ttf','Oxanium-OFL.txt','Oxanium-source.json')) { $allow.Add('src/InGlorious.Core/Assets/Fonts/'+$name) }
foreach ($name in @('LICENSE','sources.json','pro-iso.json','numpad.json','pro-iso-default.c','numpad-default.c','pro-ansi.json','pro-ansi-default.c','pro-rev2-ansi.json','pro-rev2-ansi-default.c','pro-rev2-iso.json','pro-rev2-iso-default.c','gmmk2-65-ansi.json','gmmk2-65-ansi-default.c','gmmk2-65-iso.json','gmmk2-65-iso-default.c','gmmk2-96-ansi.json','gmmk2-96-ansi-default.c','gmmk2-96-iso.json','gmmk2-96-iso-default.c')) { $allow.Add('reference/qmk/'+$name) }
foreach ($name in @('Build-Firmware.ps1','Prepare-Overlay.ps1','Patch-QmkTiming.ps1','Patch-QmkStorage.ps1','Verify-CandidateBuild.py','targets.json','protocol.md','README.md')) { $allow.Add('firmware/'+$name) }
foreach ($name in @('inglorious.c','ig_version.h','ig_effects.h','ig_color.h','ig_timing.c','ig_timing.h','ig_storage.h','ig_storage.inc','wb32_bulk.c')) { $allow.Add('firmware/inglorious/'+$name) }
foreach ($name in @('ig_board.h','cache_staging_checks.h','Run-PortChecks.ps1','storage_tests.c','Run-StorageChecks.ps1')) { $allow.Add('firmware/tests/'+$name) }
foreach ($name in @('port_checks.c','port_mock.h','raw_hid.h','eeprom.h','wear_leveling.h','keymap_introspection.h','bootloader.h','wait.h')) { $allow.Add('firmware/tests/ports/'+$name) }
foreach ($name in @('runtime_tests.c','version_reporting_checks.h','color_sequences_checks.h','color_output_checks.h','standby_checks.h','lighting_test_checks.h','effects_tests.c','bulk_tests.c','qmk_mock.h','analog.h','bootloader.h','eeprom.h','ig_physical.h','keymap_introspection.h','qmk_midi.h','raw_hid.h','wait.h','wear_leveling.h','Run-RuntimeChecks.ps1','Run-EffectsChecks.ps1','Run-BulkChecks.ps1','README.md')) { $allow.Add('firmware/tests/'+$name) }
foreach ($name in @('hal.h','wear_leveling_efl_config.h','wear_leveling_internal.h','wear_leveling.h')) { $allow.Add('firmware/tests/bulk_mocks/'+$name) }
foreach ($name in @('debounce.h','action.h','qmk_mock.h','usb_main.h','timer.h','timing_tests.c','Run-TimingChecks.ps1','Test-DescriptorPatch.ps1')) { $allow.Add('tests/TimingChecks/'+$name) }
foreach ($suite in @('InGlorious.Checks','InGlorious.OnboardChecks','InGlorious.StockChecks','InGlorious.RecordingChecks','InGlorious.FirmwareChecks','InGlorious.EvidenceChecks','InGlorious.LightingTestChecks','InGlorious.PendingChecks','InGlorious.PreferencesChecks','InGlorious.PreviewChecks','InGlorious.ColorSequenceChecks','InGlorious.MacroLibraryChecks','InGlorious.IntegrationChecks','InGlorious.LightingPresetChecks','InGlorious.BoardRegistryChecks','InGlorious.ModelChecks')) {
    $allow.Add('tests/'+$suite+'/'+$suite+'.csproj'); $allow.Add('tests/'+$suite+'/Program.cs')
}
$allow.Add('tests/InGlorious.Checks/ColorPaletteChecks.cs')
$allow.Add('tests/InGlorious.OnboardChecks/FirmwareVersionChecks.cs')
$allow.Add('tests/InGlorious.OnboardChecks/ExperimentalBoardChecks.cs')
$allow.Add('src/InGlorious.Core/Locales/lighting-zones.json')
$allow.Add('reference/discovery/gmbk-eternal-20261011.json')
$allow.Add('docs/discovery-gmbk-eternal.md')
$allow.Add('docs/gmmk3-port.md')
$allow.Add('scripts/Test-Gmmk3References.py')
$allow.Add('scripts/Test-ModelReferenceRejections.py')
$allow.Add('firmware/Prepare-Gmmk3References.py')
$allow.Add('firmware/ports/gmmk3/provenance.json')
$allow.Add('reference/glorious-gmmk3/sources.json')
# Explicitly reviewed manufacturer originals and normalized electrical ports.
foreach ($size in @('65','75','100')) {
    foreach ($name in @('info.json','config.h','halconf.h','mcuconf.h')) {
        $allow.Add('reference/glorious-gmmk3/p'+$size+'/'+$name)
    }
    foreach ($name in @('config.h','halconf.h','mcuconf.h')) {
        $allow.Add('firmware/ports/gmmk3/p'+$size+'/'+$name)
    }
    foreach ($layout in @('ansi','iso')) {
        foreach ($name in @('info.json','readme.md','rules.mk','keymaps/default/keymap.c',($layout+'.c'))) {
            $allow.Add('reference/glorious-gmmk3/p'+$size+'/'+$layout+'/'+$name)
        }
        foreach ($name in @('keyboard.json','keymaps/default/keymap.c',($layout+'.c'))) {
            $allow.Add('firmware/ports/gmmk3/p'+$size+'/'+$layout+'/'+$name)
        }
        $allow.Add('reference/qmk/gmmk3-'+$size+'-'+$layout+'.json')
        $allow.Add('reference/qmk/gmmk3-'+$size+'-'+$layout+'-default.c')
    }
}
$allow.Add('tests/InGlorious.OnboardChecks/StartupDeviceChecks.cs')
foreach ($name in @('effect_oracle.c','Run-PreviewChecks.ps1')) { $allow.Add('tests/InGlorious.PreviewChecks/'+$name) }
foreach ($name in @('sequence_oracle.c','Run-ColorSequenceChecks.ps1')) { $allow.Add('tests/InGlorious.ColorSequenceChecks/'+$name) }
foreach ($name in @('.gitignore','.gitattributes','docs/public-release.md','docs/model-support.md','scripts/Test-ModelReferences.ps1','scripts/Export-PublicSource.ps1','scripts/Build-BrandIcon.ps1','scripts/Install-InGlorious.ps1','scripts/New-WindowsPackage.ps1','scripts/Test-WindowsInstaller.ps1')) { $allow.Add($name) }
foreach ($name in @('README.md','LICENSE','COPYRIGHT.md','THIRD-PARTY-NOTICES.md','CONTRIBUTING.md','SECURITY.md','CHANGELOG.md','global.json','NuGet.Config',
    'docs/getting-started.md','docs/hardware-acceptance.md','docs/license-audit.md','docs/images/windows-lighting.png','docs/images/experimental-layout.png','docs/images/pro-ansi.png','docs/images/gmmk3-100-ansi.png',
    'scripts/Initialize-BuildDependencies.ps1','scripts/build-dependencies.json','scripts/Build-PublicRelease.ps1','scripts/Test-PublicSourceExport.ps1','scripts/Copy-RuntimeNotices.ps1',
    '.github/workflows/build.yml','.github/ISSUE_TEMPLATE/bug_report.yml','.github/ISSUE_TEMPLATE/config.yml','.github/ISSUE_TEMPLATE/device_support.yml','.github/ISSUE_TEMPLATE/feature_request.yml',
    'src/InGlorious.Desktop/packages.lock.json','src/InGlorious.Desktop/Shared/packages.lock.json',
    'Licenses/Apache-2.0.txt','Licenses/Avalonia-MIT.txt','Licenses/GPL-2.0.txt','Licenses/HidSharp-Apache-2.0.txt','Licenses/LGPL-2.1.txt','Licenses/dotnet-WindowsDesktop-MIT.txt',
    'Licenses/Avalonia-BuildServices-MIT.txt','Licenses/MicroCom-MIT.txt','Licenses/Tmds-DBus-MIT.txt','Licenses/Desktop-ThirdParty.txt','Licenses/dotnet-Runtime-MIT.txt','Licenses/dotnet-Runtime-ThirdParty.txt',
    'scripts/New-CorrespondingSource.ps1','scripts/Collect-DesktopLicenses.ps1','reference/source-distribution.json','reference/desktop-license-inventory.json')) { $allow.Add($name) }
if (($allow | Select-Object -Unique).Count -ne $allow.Count) { throw 'Duplicate source allowlist entry.' }

# A UI addition must be explicitly reviewed, not accidentally omitted from a compilable app.
$appRoot = Join-Path $root 'src/InGlorious.Core'
if (Test-Path -LiteralPath $appRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $appRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($appRoot,$file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        if ($file.Extension -in '.cs','.xaml','.csproj','.resx' -and $relative -notin $app) { throw ('Unreviewed app source is not allowlisted: '+$relative) }
        if ($relative.StartsWith('Locales/') -and $file.Extension-eq'.json' -and ('src/InGlorious.Core/'+$relative)-notin$allow) { throw ('Unreviewed language catalog is not allowlisted: '+$relative) }
    }
}
$desktopRoot = Join-Path $root 'src/InGlorious.Desktop'
if (Test-Path -LiteralPath $desktopRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $desktopRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($desktopRoot,$file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        if ($file.Extension -in '.cs','.axaml','.csproj','.resx' -and $relative -notin $desktop) { throw ('Unreviewed desktop source is not allowlisted: '+$relative) }
    }
}

# Learn local identifiers only for leak rejection; none is printed or exported.
$private = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($value in @($env:USERNAME,$env:USERPROFILE,$root)) { if ($value -and $value.Length -ge 4) { [void]$private.Add($value); [void]$private.Add($value.Replace('\','\\')) } }
foreach ($name in @('scripts/Invoke-ProFirmware.ps1','scripts/Invoke-NumpadFirmware.ps1','scripts/Backup-PRO-ReadOnly.ps1','scripts/Install-STM32-Driver.ps1','scripts/Install-WB32-Driver.ps1')) {
    $path = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Assert-NoLink $path
    $text = [IO.File]::ReadAllText($path)
    foreach ($pattern in @("'(?<v>USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^']+)'", "'-[Sp]'\s*,\s*'(?<v>[^']+)'")) {
        foreach ($match in [regex]::Matches($text,$pattern)) {
            $value = $match.Groups['v'].Value
            if ($value -notmatch '[*?]' -and $value.Length -ge 4) { [void]$private.Add($value); [void]$private.Add($value.Replace('\','\\')) }
            if ($value.StartsWith('USB\') -and $value.Split('\')[-1].Length -ge 8) { [void]$private.Add($value.Split('\')[-1]) }
        }
    }
}
function Assert-NoPrivateText([string]$Relative,[byte[]]$Bytes) {
    foreach ($decoder in @($utf8,[Text.Encoding]::Unicode,[Text.Encoding]::BigEndianUnicode)) {
        $text = $decoder.GetString($Bytes)
        # The user explicitly chose this public GitHub account. Only the exact URL
        # owner segment is exempt; usernames elsewhere remain private.
        $publicOwnerUrl = 'https://github.com/LatroX-dev'
        $scan = $text -replace ([regex]::Escape($publicOwnerUrl)+'(?=[/\s)"''#?]|$)'), 'https://github.com/REPOSITORY_OWNER'
        foreach ($value in $private) {
            if ($scan.Contains($value,[StringComparison]::OrdinalIgnoreCase)) { throw ('Private local identifier detected in allowlisted file: '+$Relative) }
        }
        if ($text -match '(?i)[A-Z]:[\\/]+Users[\\/]+[^\s"''<>]+' -or $text -match '(?i)/(?:home|Users)/[^\s/"''<>]+/') { throw ('Local user path detected in allowlisted file: '+$Relative) }
        if ($text -match '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)') { throw ('Credential material detected in allowlisted file: '+$Relative) }
    }
}

$target = $null
if ($Destination) {
    $target = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($target -notmatch '^[A-Za-z]:\\' -or ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($target))).DriveType -eq [IO.DriveType]::Network) { throw 'Destination must be a local drive path, not a network path.' }
    if ($target.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside SourceRoot.' }
    if (Test-Path -LiteralPath $target) { throw 'Destination already exists. Nothing will be overwritten or cleaned.' }
    Assert-NoLink ([IO.Path]::GetDirectoryName($target))
} elseif ($Export) { throw '-Export requires an explicit new local -Destination.' }

$files = [Collections.Generic.List[object]]::new()
$locks = [Collections.Generic.List[IO.FileStream]]::new()
try {
    foreach ($relative in $allow) {
        $reviewedAdapter=$relative.StartsWith('tools/InGlorious.FirmwareAdapter/') -and $relative.Substring('tools/InGlorious.FirmwareAdapter/'.Length) -in $adapterFiles
        if ((-not$reviewedAdapter-and$relative -match '(^|/)(backups|data|artifacts|bin|obj|tools|releases|toolchain)(/|$)') -or $relative.Contains('..')) { throw 'An unsafe path entered the source allowlist.' }
        $path = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $path)) { throw ('Reviewed source file is missing: '+$relative) }
        Assert-NoLink $path
        $stream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $locks.Add($stream)
        if ($stream.Length -gt 4MB) { throw ('Unexpected oversized source file: '+$relative) }
        $bytes = [byte[]]::new($stream.Length); $stream.ReadExactly($bytes)
        Assert-NoPrivateText $relative $bytes
        $files.Add([pscustomobject]@{path=$relative;bytes=$bytes.Length;sha256=(Get-Hash $bytes);content=$bytes})
    }
    $markerBytes=$utf8.GetBytes('{"name":"INGLORIOUS Advanced","publicSource":true}')
    $files.Add([pscustomobject]@{path='inglorious.workspace.json';bytes=$markerBytes.Length;sha256=(Get-Hash $markerBytes);content=$markerBytes})
    $records=@($files | ForEach-Object { [ordered]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256} })
    $result=[ordered]@{mode=if($Export){'local-export'}else{'plan-only'};fileCount=$files.Count;publicReleaseReady=$false;hardwareAccess=$false;networkAccess=$false;files=$records}
    if (-not $Export) { $result; return }
    New-Item -ItemType Directory -Path $target | Out-Null
    Assert-NoLink $target
    foreach ($file in $files) {
        $path=Join-Path $target $file.path
        $parent=[IO.Path]::GetDirectoryName($path)
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Assert-NoLink $parent
        Write-New $path $file.content
        if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256 -or (Get-Item -LiteralPath $path).Length -ne $file.bytes) { throw ('Persisted export verification failed: '+$file.path) }
    }
    Write-New (Join-Path $target 'source-export-manifest.json') ($utf8.GetBytes(($result|ConvertTo-Json -Depth 6)))
    $result
} finally { foreach ($stream in $locks) { $stream.Dispose() } }
