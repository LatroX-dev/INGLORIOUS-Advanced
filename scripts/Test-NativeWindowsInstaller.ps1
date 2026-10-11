#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$ToolCatalogPath,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$Offline)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'Native installer tests require Windows.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Installer test output must be new.'}
New-Item -ItemType Directory -Path $output|Out-Null
$checks=0
$manifestFixture=Join-Path $output 'manifest-fixture'
New-Item -ItemType Directory -Path $manifestFixture|Out-Null
Copy-Item -LiteralPath (Join-Path $PackageDirectory 'application') -Destination $manifestFixture -Recurse
$originalManifest=Get-Content -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Raw
foreach($case in @('empty','missing-firmware','extra','duplicate','wrong-hash','wrong-size')){
 $altered=$originalManifest|ConvertFrom-Json
 switch($case){
  'empty' {$altered.files=@()}
  'missing-firmware' {$missing=@($altered.files|Where-Object path -like 'Firmware/*')[0].path;$altered.files=@($altered.files|Where-Object path -cne $missing)}
  'extra' {$altered.files+=@([pscustomobject]@{path='extra.json';bytes=1;sha256=('0'*64)})}
  'duplicate' {$altered.files[1]=$altered.files[0]}
  'wrong-hash' {$altered.files[0].sha256='0'*64}
  'wrong-size' {$altered.files[0].bytes++}
 }
 [IO.File]::WriteAllText((Join-Path $manifestFixture 'package-manifest.json'),($altered|ConvertTo-Json -Depth 10))
 $negativeOutput=Join-Path $output ('reject-'+$case);$rejected=$false
 try{& (Join-Path $PSScriptRoot 'Build-WindowsInstaller.ps1') -PackageDirectory $manifestFixture -ToolCatalogPath $ToolCatalogPath -OutputDirectory $negativeOutput -TestInstaller -Offline|Out-Null}catch{$rejected=$true}
 if(-not$rejected-or(Test-Path -LiteralPath $negativeOutput)){throw ('Invalid manifest reached the compiler: '+$case)};$checks++
}
$built=& (Join-Path $PSScriptRoot 'Build-WindowsInstaller.ps1') -PackageDirectory $PackageDirectory -ToolCatalogPath $ToolCatalogPath -OutputDirectory (Join-Path $output 'build') -TestInstaller -Offline:$Offline
$install=Join-Path $output 'installed'
$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D5748319-8891-40CE-9DB9-9FA2D1B189B8}_is1'
if(Test-Path -LiteralPath $key){throw 'An earlier isolated installer test is still registered.'}
$runKey='HKCU:\Software\INGLORIOUS-Installer-Checks\Run';$runName='INGLORIOUS Advanced Installer Test'
function Read-StartupValue {
 $registry=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\INGLORIOUS-Installer-Checks\Run')
 try{if($null-eq$registry){return $null};return $registry.GetValue($runName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{if($registry){$registry.Dispose()}}
}
if($null-ne(Read-StartupValue)){throw 'An earlier isolated startup test remains.'}
$approvalKey='HKCU:\Software\INGLORIOUS-Installer-Checks\StartupApproved\Run'
function Read-StartupApproval {
 $registry=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\INGLORIOUS-Installer-Checks\StartupApproved\Run')
 try{if($null-eq$registry){return $null};return $registry.GetValue($runName,$null)}finally{if($registry){$registry.Dispose()}}
}
if($null-ne(Read-StartupApproval)){throw 'An earlier isolated startup approval test remains.'}
New-Item -Path $approvalKey -Force|Out-Null
$approvalBytes=[byte[]]@(3,0,0,0,0,0,0,0,0,0,0,0)
New-ItemProperty -LiteralPath $approvalKey -Name $runName -Value $approvalBytes -PropertyType Binary|Out-Null
$approvalHex=[Convert]::ToHexString($approvalBytes)
function Run-Installer([string]$Executable,[string[]]$Arguments){
 $info=[Diagnostics.ProcessStartInfo]::new($Executable);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($info)
 try{
  if(-not$process.WaitForExit(120000)){throw 'The isolated test installer timed out.'}
  if($process.ExitCode-eq0-and[IO.Path]::GetFileName($Executable)-like'unins*.exe'){
   # Inno's first process may exit while its temporary cleanup process is still
   # finishing. Do not begin a second installation until its lifetime gate closes.
   $timer=[Diagnostics.Stopwatch]::StartNew()
   do{$handle=$null;$present=[Threading.Mutex]::TryOpenExisting('INGLORIOUS.Advanced.InstallerTest.Setup',[ref]$handle);if($handle){$handle.Dispose()};if($present){Start-Sleep -Milliseconds 50}}while($present-and$timer.Elapsed.TotalSeconds-lt20)
   if($present){throw 'The test uninstaller did not release its lifetime gate.'}
  }
  return $process.ExitCode
 }finally{$process.Dispose()}
}
$common=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/SP-',('/DIR='+$install))
$dataRoot=Join-Path $output 'build/fake-user-data'
$backup=Join-Path $dataRoot 'backups/original-firmware.bin'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force|Out-Null
[IO.File]::WriteAllBytes($backup,[byte[]]::new(4096))
$backupHash=(Get-FileHash -LiteralPath $backup).Hash
[IO.File]::WriteAllText((Join-Path $dataRoot 'profile.json'),'synthetic saved user profile')
$externalFile=Join-Path $output 'independent-export.json';[IO.File]::WriteAllText($externalFile,'synthetic external export')
$externalHash=(Get-FileHash -LiteralPath $externalFile).Hash
try{
$mutex=[Threading.Mutex]::new($false,'INGLORIOUS.Advanced.InstallerTest.Running')
try{
 $blocked=Run-Installer $built.installer ($common+@('/LANG=english',('/LOG='+(Join-Path $output 'blocked.log'))))
 if($blocked-eq0-or(Test-Path -LiteralPath $install)-or(Test-Path -LiteralPath $key)){throw 'Running-app guard did not prevent installation.'};$checks++
}finally{$mutex.Dispose()}
$exitCode=Run-Installer $built.installer ($common+@('/LANG=english',('/LOG='+(Join-Path $output 'install.log'))))
if($exitCode-ne0){throw ('Isolated installation failed: '+$exitCode)}
$source=Get-Content -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Raw|ConvertFrom-Json
foreach($file in $source.files){if((Get-FileHash -LiteralPath (Join-Path $install $file.path)).Hash-cne$file.sha256){throw 'Installed file differs.'};$checks++}
$registration=Get-ItemProperty -LiteralPath $key
if($registration.DisplayVersion-cne$source.version-or$registration.DisplayName-cne'INGLORIOUS Advanced'-or-not$registration.UninstallString.Contains($install,[StringComparison]::OrdinalIgnoreCase)){throw 'Apps & Features registration differs.'};$checks++
if((Read-StartupValue)-cne('"'+(Join-Path $install 'INGLORIOUS Advanced.exe')+'" --background')){throw 'Fresh-install startup default does not launch in the notification area.'};$checks++
$sentinel=Join-Path $install 'keep-my-profile.json';[IO.File]::WriteAllText($sentinel,'synthetic user-owned file')
$sentinelHash=(Get-FileHash -LiteralPath $sentinel).Hash
# Simulate changing the setting in the app after installation: the real registry
# state, rather than Inno's remembered task selection, must control the upgrade.
$ownedCommand='"'+(Join-Path $install 'INGLORIOUS Advanced.exe')+'"'
New-Item -Path $runKey -Force|Out-Null
New-ItemProperty -LiteralPath $runKey -Name $runName -Value $ownedCommand -PropertyType String -Force|Out-Null
$ownedCommand+=' --background'
$exitCode=Run-Installer $built.installer ($common+@('/LANG=german',('/LOG='+(Join-Path $output 'update.log'))))
if($exitCode-ne0-or(Get-FileHash -LiteralPath $sentinel).Hash-cne$sentinelHash){throw 'Reinstallation changed a user-owned file.'};$checks++
if((Read-StartupValue)-cne$ownedCommand){throw 'Upgrade lost the current enabled autostart setting.'};$checks++
if([Convert]::ToHexString([byte[]](Read-StartupApproval))-cne$approvalHex){throw 'Upgrade changed Windows startup approval.'};$checks++
foreach($file in $source.files){if((Get-FileHash -LiteralPath (Join-Path $install $file.path)).Hash-cne$file.sha256){throw 'Updated file differs.'};$checks++}
Remove-ItemProperty -LiteralPath $runKey -Name $runName
$exitCode=Run-Installer $built.installer ($common+@('/LANG=english',('/LOG='+(Join-Path $output 'disabled-update.log'))))
if($exitCode-ne0-or$null-ne(Read-StartupValue)){throw 'Upgrade re-enabled autostart after it was disabled in the app.'};$checks++
$foreign='"C:\Windows\notepad.exe"'
New-ItemProperty -LiteralPath $runKey -Name $runName -Value $foreign -PropertyType String -Force|Out-Null
$exitCode=Run-Installer $built.installer ($common+@('/TASKS=startup',('/LOG='+(Join-Path $output 'foreign-conflict.log'))))
if($exitCode-eq0-or(Read-StartupValue)-cne$foreign){throw 'A foreign startup entry was accepted or overwritten.'};$checks++
$exitCode=Run-Installer $built.installer ($common+@('/TASKS=!startup',('/LOG='+(Join-Path $output 'foreign-preserve.log'))))
if($exitCode-ne0-or(Read-StartupValue)-cne$foreign){throw 'Disabling startup removed a foreign value.'};$checks++
Remove-ItemProperty -LiteralPath $runKey -Name $runName
$exitCode=Run-Installer $built.installer ($common+@('/TASKS=startup',('/LOG='+(Join-Path $output 'explicit-startup.log'))))
if($exitCode-ne0-or(Read-StartupValue)-cne$ownedCommand){throw 'Explicit startup opt-in failed.'};$checks++
$uninstallers=@(Get-ChildItem -LiteralPath (Join-Path $install 'Uninstall') -Filter 'unins*.exe' -File)
if($uninstallers.Count-ne1){throw 'Uninstaller is not unique.'}
$setupMutexName='INGLORIOUS.Advanced.InstallerTest.Setup'
foreach($blockedBy in @('INGLORIOUS.Advanced.InstallerTest.Running',$setupMutexName)){
 $blocker=[Threading.Mutex]::new($false,$blockedBy)
 try{
  $exitCode=Run-Installer $uninstallers[0].FullName @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
  if($exitCode-eq0-or-not(Test-Path -LiteralPath $key)-or(Get-FileHash -LiteralPath $backup).Hash-cne$backupHash){throw 'Uninstaller did not preserve files while another app or setup was active.'};$checks++
 }finally{$blocker.Dispose()}
}
# Hold the real uninstaller at its test-only checkpoint. Exercise the actual app
# guard against its live mutex, then cancel removal without touching any app data.
$ready=$dataRoot+'.uninstall-ready';$resume=$dataRoot+'.uninstall-continue'
$info=[Diagnostics.ProcessStartInfo]::new($uninstallers[0].FullName);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
foreach($argument in @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTPAUSEUNINSTALL=1','/TESTCANCELUNINSTALL=1')){$info.ArgumentList.Add($argument)}
$paused=[Diagnostics.Process]::Start($info)
try{
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while(-not(Test-Path -LiteralPath $ready)-and-not$paused.HasExited-and$timer.Elapsed.TotalSeconds-lt20){Start-Sleep -Milliseconds 50}
 if(-not(Test-Path -LiteralPath $ready)){throw 'Uninstaller did not reach its isolated checkpoint.'}
 $guardSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/InGlorious.Core/WindowsInstallationGuard.cs') -Raw
 if(-not('InGlorious.Core.WindowsInstallationGuard' -as [type])){Add-Type -TypeDefinition ("#nullable enable`nusing System;`nusing System.Threading;`n"+$guardSource)}
 $guard=[Activator]::CreateInstance([InGlorious.Core.WindowsInstallationGuard],[Reflection.BindingFlags]'Instance,NonPublic',$null,@('INGLORIOUS.Advanced.InstallerTest.Running',$setupMutexName,$true),$null)
 try{if($guard.CanStart-or$guard.State.ToString()-cne'InstallerRunning'){throw 'App guard admitted startup during removal.'};$checks++}finally{$guard.Dispose()}
}finally{
 [IO.File]::WriteAllText($resume,'continue')
 if(-not$paused.WaitForExit(20000)){throw 'Paused test uninstaller did not terminate.'}
 $cancelExit=$paused.ExitCode;$paused.Dispose()
}
if($cancelExit-eq0-or-not(Test-Path -LiteralPath $key)-or(Get-FileHash -LiteralPath $backup).Hash-cne$backupHash){throw 'Cancelled removal changed the app or data.'};$checks++
$remainingMutex=$null
if([Threading.Mutex]::TryOpenExisting($setupMutexName,[ref]$remainingMutex)){$remainingMutex.Dispose();throw 'Cancelled removal retained the setup gate.'};$checks++
New-ItemProperty -LiteralPath $runKey -Name $runName -Value ('"'+(Join-Path $install 'INGLORIOUS Advanced.exe')+'"') -PropertyType String -Force|Out-Null
$exitCode=Run-Installer $uninstallers[0].FullName @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+(Join-Path $output 'uninstall.log')))
if($exitCode-ne0-or(Test-Path -LiteralPath $key)-or(Get-FileHash -LiteralPath $sentinel).Hash-cne$sentinelHash){throw 'Uninstall did not preserve the user-owned file or registration remains.'};$checks++
if($null-ne(Read-StartupValue)){throw 'Uninstall retained its own startup entry.'};$checks++
if([Convert]::ToHexString([byte[]](Read-StartupApproval))-cne$approvalHex){throw 'Keep-data uninstall changed Windows startup approval.'};$checks++
if((Get-FileHash -LiteralPath $backup).Hash-cne$backupHash-or-not(Test-Path -LiteralPath (Join-Path $dataRoot 'profile.json'))){throw 'Default uninstall removed a profile or original-firmware backup.'};$checks++
foreach($file in $source.files){if(Test-Path -LiteralPath (Join-Path $install $file.path)){throw ('Installer-owned file remains: '+$file.path)};$checks++}
$exitCode=Run-Installer $built.installer ($common+@('/TASKS=!startup',('/LOG='+(Join-Path $output 'reinstall-for-data-check.log'))))
if($exitCode-ne0){throw 'Could not reinstall the isolated data-removal fixture.'}
$junction=Join-Path $dataRoot 'linked-folder'
$externalDirectory=Join-Path $output 'external-folder';New-Item -ItemType Directory -Path $externalDirectory|Out-Null
$externalSentinel=Join-Path $externalDirectory 'keep.txt';[IO.File]::WriteAllText($externalSentinel,'outside the app data folder')
New-Item -ItemType Junction -Path $junction -Target $externalDirectory|Out-Null
$uninstaller=(Get-ChildItem -LiteralPath (Join-Path $install 'Uninstall') -Filter 'unins*.exe' -File)[0].FullName
try{
 $exitCode=Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTREMOVEUSERDATA=1',('/LOG='+(Join-Path $output 'linked-data-rejected.log')))
 if($exitCode-eq0-or(Get-FileHash -LiteralPath $backup).Hash-cne$backupHash-or-not(Test-Path -LiteralPath $externalSentinel)-or-not(Test-Path -LiteralPath $key)){throw 'Linked data path was not safely rejected.'};$checks++
}finally{
 if((Get-Item -LiteralPath $junction).Attributes-band[IO.FileAttributes]::ReparsePoint){Remove-Item -LiteralPath $junction}else{throw 'Expected test junction changed.'}
}
New-ItemProperty -LiteralPath $runKey -Name $runName -Value $foreign -PropertyType String -Force|Out-Null
$exitCode=Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTREMOVEUSERDATA=1',('/LOG='+(Join-Path $output 'remove-all-data.log')))
if($exitCode-ne0-or(Test-Path -LiteralPath $dataRoot)-or(Test-Path -LiteralPath $key)){throw 'Explicit complete removal did not remove its fake data.'};$checks++
if((Get-FileHash -LiteralPath $externalFile).Hash-cne$externalHash-or-not(Test-Path -LiteralPath $externalSentinel)-or(Get-FileHash -LiteralPath $sentinel).Hash-cne$sentinelHash){throw 'Complete removal changed a file outside the fixed app-data root.'};$checks++
if((Read-StartupValue)-cne$foreign-or[Convert]::ToHexString([byte[]](Read-StartupApproval))-cne$approvalHex){throw 'Complete removal changed a foreign startup value or its approval.'};$checks++
Remove-ItemProperty -LiteralPath $runKey -Name $runName
$exitCode=Run-Installer $built.installer ($common+@('/TASKS=!startup',('/LOG='+(Join-Path $output 'reinstall-for-approval-check.log'))))
if($exitCode-ne0){throw 'Could not reinstall the startup approval fixture.'}
$uninstaller=(Get-ChildItem -LiteralPath (Join-Path $install 'Uninstall') -Filter 'unins*.exe' -File)[0].FullName
$exitCode=Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTREMOVEUSERDATA=1',('/LOG='+(Join-Path $output 'remove-startup-approval.log')))
if($exitCode-ne0-or$null-ne(Read-StartupApproval)-or(Test-Path -LiteralPath $key)){throw 'Complete removal retained its own Windows startup approval.'};$checks++
[pscustomobject]@{checks=$checks;isolatedRegistration=$true;hardwareAccess=$false;appLaunched=$false;userFilesPreserved=$true;autostartPreserved=$true;dataRemovalTested=$true;linksRejected=$true;uninstallRaceProtected=$true;languagesTested=@('english','german')}
}finally{
 Remove-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue
 Remove-ItemProperty -LiteralPath $approvalKey -Name $runName -ErrorAction SilentlyContinue
}
