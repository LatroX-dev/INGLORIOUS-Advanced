#Requires -Version 7.0
[CmdletBinding()]
param([Alias('AppHostParameter')][string]$AppHost,[string]$ToolCatalogPath=(Join-Path $PSScriptRoot '../src/InGlorious.Core/SetupToolCatalog.json'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('InGlorious-installer-test-'+[Guid]::NewGuid().ToString('N'))
$checks=0
function Check([bool]$Value,[string]$Message){$script:checks++;if(-not$Value){throw $Message}}
function Reject([scriptblock]$Action,[string]$Message){$failed=$false;try{$null=&$Action}catch{$failed=$true};Check $failed $Message}
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
 # Match Windows runners whose TEMP directory is below hidden AppData. The
 # real package/installer checks below must still inspect every ancestor.
 [IO.File]::SetAttributes($testRoot,([IO.File]::GetAttributes($testRoot)-bor[IO.FileAttributes]::Hidden))
 $guardTarget=Join-Path $testRoot 'guard-target';New-Item -ItemType Directory -Path $guardTarget|Out-Null
 $guardFile=Join-Path $guardTarget 'synthetic.txt';[IO.File]::WriteAllText($guardFile,'Synthetic path-check fixture only.')
 $guardLink=Join-Path $testRoot 'hidden-junction'
 New-Item -ItemType Junction -Path $guardLink -Target $guardTarget|Out-Null
 try {
  [IO.File]::SetAttributes($guardLink,([IO.File]::GetAttributes($guardLink)-bor[IO.FileAttributes]::Hidden))
  $linkAttributes=(Get-Item -LiteralPath $guardLink -Force).Attributes
  Check ([bool]($linkAttributes-band[IO.FileAttributes]::Hidden)-and[bool]($linkAttributes-band[IO.FileAttributes]::ReparsePoint)) 'The regression fixture must be a hidden junction.'
  foreach($guardScript in @('New-WindowsPackage.ps1','Install-InGlorious.ps1','Export-PublicSource.ps1','New-CorrespondingSource.ps1','New-DesktopPackage.ps1','Initialize-BuildDependencies.ps1','Initialize-InstallerCompiler.ps1','Build-PosixNativeTools.ps1','Prepare-SetupAssets.ps1')){
   # Execute the production guard in isolation, without starting the build,
   # download, packaging or installation body of the corresponding script.
   $parseErrors=$null;$tokens=$null
   $tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $guardScript),[ref]$tokens,[ref]$parseErrors)
   Check ($parseErrors.Count-eq0) ('Path-guard script must parse: '+$guardScript)
   $definitions=@($tree.FindAll({param($node)$node-is[Management.Automation.Language.FunctionDefinitionAst]-and$node.Name-ceq'Assert-NoLink'},$false))
   Check ($definitions.Count-eq1) ('One production path guard is required: '+$guardScript)
   $guard=[scriptblock]::Create($definitions[0].Extent.Text+"`nAssert-NoLink `$args[0]")
   &$guard $testRoot;Check $true ('Hidden directory accepted: '+$guardScript)
   &$guard $guardFile;Check $true ('Hidden ancestor accepted: '+$guardScript)
   Reject {&$guard $guardLink} ('Hidden junction accepted: '+$guardScript)
   Reject {&$guard (Join-Path $guardLink 'synthetic.txt')} ('Hidden junction ancestor accepted: '+$guardScript)
  }
 } finally {
  # Remove only the verified junction itself; never recurse through its target.
  if(Test-Path -LiteralPath $guardLink){
   if(-not((Get-Item -LiteralPath $guardLink -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Path-check junction changed before cleanup.'}
   Remove-Item -LiteralPath $guardLink -Force
  }
 }
 $fixture=Join-Path $testRoot 'publish';New-Item -ItemType Directory -Path $fixture | Out-Null
 # A real existing apphost supplies Windows version metadata. It is never started.
 if([string]::IsNullOrWhiteSpace($AppHost)){$AppHost=Join-Path $workspace 'artifacts/app-standalone/INGLORIOUS Advanced.exe'}
 $apphost=[IO.Path]::GetFullPath($AppHost)
 if(-not(Test-Path -LiteralPath $apphost)){throw 'An existing standalone Windows publish is required for the isolated packaging fixture.'}
 $version=(Get-Item -LiteralPath $apphost).VersionInfo.ProductVersion.Split('+')[0]
 Copy-Item -LiteralPath $apphost -Destination (Join-Path $fixture 'INGLORIOUS Advanced.exe')
 foreach($name in @('INGLORIOUS Advanced.dll','INGLORIOUS Advanced.deps.json','INGLORIOUS Advanced.runtimeconfig.json','coreclr.dll','System.Private.CoreLib.dll')){[IO.File]::WriteAllText((Join-Path $fixture $name),'SYNTHETIC installer fixture; never execute')}
 Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($apphost)) 'INGLORIOUS Advanced.deps.json') -Destination (Join-Path $fixture 'INGLORIOUS Advanced.deps.json') -Force
 [IO.File]::WriteAllText((Join-Path $fixture 'INGLORIOUS Advanced.pdb'),'private debug paths must not ship')
 New-Item -ItemType Directory -Path (Join-Path $fixture 'Licenses') | Out-Null
 $fontLicense=Join-Path $workspace 'src/InGlorious.Core/Assets/Fonts/Oxanium-OFL.txt'
 Copy-Item -LiteralPath $fontLicense -Destination (Join-Path $fixture 'Licenses/Oxanium-OFL.txt')
 foreach($folder in @('Firmware','Tools','Licenses')){
  if($folder-eq'Licenses'){Copy-Item -Path (Join-Path ([IO.Path]::GetDirectoryName($apphost)) 'Licenses/*') -Destination (Join-Path $fixture 'Licenses') -Force}else{Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($apphost)) $folder) -Destination (Join-Path $fixture $folder) -Recurse}
 }
 # The package builder checks these against the pinned official runtime archive.
 $publishedRoot=[IO.Path]::GetDirectoryName($apphost)
 foreach($resource in Get-ChildItem -LiteralPath $publishedRoot -Recurse -File -Filter '*.resources.dll'){
  $relative=[IO.Path]::GetRelativePath($publishedRoot,$resource.FullName).Replace('\','/')
  if($relative-cnotmatch'^[a-z]{2}(?:-[A-Za-z0-9]{2,8})?/[A-Za-z0-9_.-]+\.resources\.dll$'){continue}
  $copy=Join-Path $fixture $relative
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force|Out-Null
  Copy-Item -LiteralPath $resource.FullName -Destination $copy
 }
 $package=Join-Path $testRoot 'package'
 $builder=Join-Path $PSScriptRoot 'New-WindowsPackage.ps1'
 $plan=&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination $package -ExpectedVersion $version
 Check ($plan.mode-eq'plan' -and -not(Test-Path -LiteralPath $package)) 'Package default must be a side-effect-free plan.'
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination $package -ExpectedVersion '99.0.0'} 'Old publish must not be packaged as a new version.'
 $mixedPublishFile=Join-Path $fixture 'INGLORIOUS Core.dll'
 Copy-Item -LiteralPath (Join-Path $fixture 'INGLORIOUS Advanced.dll') -Destination $mixedPublishFile
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination $package -ExpectedVersion $version} 'Packaging must reject mixed Core/Advanced entry files.'
 Remove-Item -LiteralPath $mixedPublishFile
 $created=&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination $package -ExpectedVersion $version -Create
 Check ($created.mode-eq'created'-and-not$created.digitallySigned) 'Package creation is explicit and reports unsigned status.'
 Check (-not(Test-Path -LiteralPath (Join-Path $package 'application/INGLORIOUS Advanced.pdb'))) 'PDBs must not enter distribution.'
 Check ((Get-FileHash -LiteralPath (Join-Path $package 'application/Licenses/Oxanium-OFL.txt')).Hash -eq (Get-FileHash -LiteralPath $fontLicense).Hash) 'The bundled font license must ship unchanged.'
 $unapprovedText=Join-Path $fixture 'Licenses/unreviewed.txt'
 [IO.File]::WriteAllText($unapprovedText,'Unreviewed text must not be allowed by the font-license exception.')
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination (Join-Path $testRoot 'unexpected-license') -ExpectedVersion $version} 'Only the exact reviewed font-license path is allowed.'
 Remove-Item -LiteralPath $unapprovedText
 $unapprovedTool=Join-Path $fixture 'Tools/unreviewed.exe'
 [IO.File]::WriteAllText($unapprovedTool,'Not a reviewed firmware tool.')
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination (Join-Path $testRoot 'unexpected-tool') -ExpectedVersion $version} 'Unreviewed executable must not enter the tools bundle.'
 Remove-Item -LiteralPath $unapprovedTool
 foreach($extraName in @('my-profile.json','original-backup.bin','unreviewed.dll')){
  $extraFile=Join-Path $fixture $extraName
  [IO.File]::WriteAllText($extraFile,'SYNTHETIC private artifact must never be packaged.')
  Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination (Join-Path $testRoot ('unexpected-'+$extraName)) -ExpectedVersion $version} ('Unlisted publish file must be rejected: '+$extraName)
  Remove-Item -LiteralPath $extraFile
 }
 $resourceFile=Join-Path $fixture 'cs/PresentationCore.resources.dll';$resourceBytes=[IO.File]::ReadAllBytes($resourceFile)
 [IO.File]::WriteAllBytes($resourceFile,[byte[]]@(0,1,2,3))
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination (Join-Path $testRoot 'changed-runtime-resource') -ExpectedVersion $version} 'Changed runtime resources must not be packaged.'
 [IO.File]::WriteAllBytes($resourceFile,$resourceBytes)
 $firmwareFile=Join-Path $fixture 'Firmware/pro.bin';$firmwareBytes=[IO.File]::ReadAllBytes($firmwareFile)
 [IO.File]::WriteAllBytes($firmwareFile,[byte[]]@(0,1,2,3))
 Reject {&$builder -ToolCatalogPath $ToolCatalogPath -PublishDirectory $fixture -Destination (Join-Path $testRoot 'changed-firmware') -ExpectedVersion $version} 'Changed firmware must not be packaged.'
 [IO.File]::WriteAllBytes($firmwareFile,$firmwareBytes)
 $installer=Join-Path $package 'Install-InGlorious.ps1';$manifestPath=Join-Path $package 'package-manifest.json'
 $originalManifest=[IO.File]::ReadAllText($manifestPath)
 $brandingManifest=$originalManifest|ConvertFrom-Json
 Check ($brandingManifest.product-ceq'INGLORIOUS Advanced'-and$brandingManifest.entryPoint-ceq'INGLORIOUS Advanced.exe') 'New manifests must use the current visible product and executable names.'
 Check ((Get-Content -LiteralPath (Join-Path $package 'README.txt') -Raw).StartsWith('INGLORIOUS Advanced ')) 'Package README must display the current brand.'
 $default=&$installer -PackageRoot $package -TestRoot $testRoot
 Check ($default.mode-eq'plan'-and-not(Test-Path -LiteralPath $default.destination)-and-not(Test-Path -LiteralPath $default.shortcut)) 'Installer default must not create target or shortcut.'
 Check (-not$default.autostart-and-not$default.hardwareAccess-and-not$default.appStarted) 'Plan explicitly excludes autostart, hardware and launching.'
 Check ($default.backgroundNotice.Contains('im Hintergrund')-and$default.backgroundNotice.Contains('ohne laufende App')) 'The scope of background dimming must be disclosed.'
 Check ($default.product-ceq'INGLORIOUS Advanced'-and$default.backgroundNotice.Contains('INGLORIOUS im Hintergrund')-and([IO.Path]::GetFileName($default.shortcut))-ceq('INGLORIOUS Advanced '+$version+'.lnk')) 'Plans, notice and shortcut must display uppercase branding.'
 foreach($suffix in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
  $requiredName='INGLORIOUS Advanced'+$suffix
  $missing=$originalManifest|ConvertFrom-Json
  $missing.files=@($missing.files|Where-Object path -CNE $requiredName)
  [IO.File]::WriteAllText($manifestPath,($missing|ConvertTo-Json -Depth 6))
  Reject {&$installer -PackageRoot $package -TestRoot $testRoot} ('Missing required entry file accepted: '+$requiredName)
  $mixed=$originalManifest|ConvertFrom-Json
  $mixedName='INGLORIOUS Core'+$suffix
  $mixedPath=Join-Path $package ('application/'+$mixedName)
  Copy-Item -LiteralPath (Join-Path $package ('application/'+$requiredName)) -Destination $mixedPath
  $mixedRecord=@($mixed.files|Where-Object path -CEQ $requiredName)[0]
  $mixedRecord.path=$mixedName
  [IO.File]::WriteAllText($manifestPath,($mixed|ConvertTo-Json -Depth 6))
  Reject {&$installer -PackageRoot $package -TestRoot $testRoot} ('Mismatched Core entry file accepted: '+$mixedName)
  $mixed=$originalManifest|ConvertFrom-Json
  $mixed.files=@($mixed.files)+@($mixedRecord)
  [IO.File]::WriteAllText($manifestPath,($mixed|ConvertTo-Json -Depth 6))
  Reject {&$installer -PackageRoot $package -TestRoot $testRoot} ('Additional Core entry file accepted beside complete Advanced files: '+$mixedName)
  Remove-Item -LiteralPath $mixedPath
 }
 [IO.File]::WriteAllText($manifestPath,$originalManifest)
 Reject {&$installer -PackageRoot $package -Install -TestRoot $testRoot} 'Test install requires a separate acknowledgement.'
 Reject {&$installer -PackageRoot $package -Install -TestAcknowledgeBackgroundNotice} 'Test acknowledgement cannot bypass the real-user confirmation.'
 Reject {&$installer -PackageRoot $package -TestRoot $workspace} 'Test installation cannot target the workspace or user program folders.'
 foreach($kind in @('traversal','absolute','backslash','duplicate','receipt','size','hash','entry','historical-entry','product','version','installer')) {
  $manifest=$originalManifest|ConvertFrom-Json
  switch($kind){
   'traversal'{$manifest.files[0].path='../escape.exe'}
   'absolute'{$manifest.files[0].path='C:/escape.exe'}
   'backslash'{$manifest.files[0].path='sub\escape.exe'}
   'duplicate'{$manifest.files[1].path=$manifest.files[0].path}
   'receipt'{$manifest.files[0].path='installation.json'}
   'size'{$manifest.files[0].bytes++}
   'hash'{$manifest.files[0].sha256='0'*64}
   'entry'{$manifest.entryPoint='createdump.exe'}
   'historical-entry'{$manifest.entryPoint='INGLORIOUS Core.exe'}
   'product'{$manifest.product='INGLORIOUS Other'}
   'version'{$manifest.version='../bad'}
   'installer'{$manifest.installerSha256='0'*64}
  }
  [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 6))
  Reject {&$installer -PackageRoot $package -TestRoot $testRoot} ('Unsafe manifest accepted: '+$kind)
 }
 [IO.File]::WriteAllText($manifestPath,$originalManifest)
 $installed=&$installer -PackageRoot $package -Install -TestRoot $testRoot -TestAcknowledgeBackgroundNotice
 Check ($installed.mode-eq'installed'-and(Test-Path -LiteralPath $installed.shortcut)) 'Isolated explicit install creates files and a shortcut.'
 $manifest=$originalManifest|ConvertFrom-Json
 foreach($file in $manifest.files){Check ((Get-FileHash -LiteralPath (Join-Path $installed.destination $file.path)).Hash-eq$file.sha256) ('Installed hash differs: '+$file.path)}
 $shell=New-Object -ComObject WScript.Shell
 try{$link=$shell.CreateShortcut($installed.shortcut);Check ($link.TargetPath-eq(Join-Path $installed.destination 'INGLORIOUS Advanced.exe')-and$link.WorkingDirectory-eq$installed.destination-and[string]::IsNullOrEmpty($link.Arguments)) 'Shortcut must launch only the installed app without arguments.'}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
 $receipt=Get-Content -LiteralPath (Join-Path $installed.destination 'installation.json') -Raw|ConvertFrom-Json
 Check ($receipt.product-ceq'INGLORIOUS Advanced') 'New installation receipts must display the current product name.'
 Check ($receipt.backgroundNoticeAccepted-and-not$receipt.autostart-and-not$receipt.hardwareAccess-and-not$receipt.appStarted) 'Receipt preserves explicit notice and scope.'
 $receiptHash=(Get-FileHash -LiteralPath (Join-Path $installed.destination 'installation.json')).Hash
 Reject {&$installer -PackageRoot $package -Install -TestRoot $testRoot -TestAcknowledgeBackgroundNotice} 'Existing installation must not be overwritten.'
 Check ((Get-FileHash -LiteralPath (Join-Path $installed.destination 'installation.json')).Hash-eq$receiptHash) 'Rejected overwrite must preserve receipt.'
 # Parse and run the plan under the actual inbox PowerShell5.1 used by Install.cmd.
 $legacy=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
 $other=Join-Path $testRoot ('InGlorious-installer-test-'+[Guid]::NewGuid().ToString('N'))
 $text=&$legacy -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -PackageRoot $package -TestRoot $other 2>&1
 Check ($LASTEXITCODE-eq0-and-not(Test-Path -LiteralPath $other)) ('PowerShell5.1 default plan failed: '+($text-join' '))
 $text=&$legacy -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -PackageRoot $package -Install -TestRoot $other -TestAcknowledgeBackgroundNotice 2>&1
 Check ($LASTEXITCODE-eq0-and(Test-Path -LiteralPath (Join-Path $other ('shortcuts/INGLORIOUS Advanced '+$version+'.lnk')))) ('PowerShell5.1 isolated installation failed: '+($text-join' '))
 foreach($file in $manifest.files){Check ((Get-FileHash -LiteralPath (Join-Path $other ('installed/'+$version+'/'+$file.path))).Hash-eq$file.sha256) ('PowerShell5.1 installed hash differs: '+$file.path)}
 Write-Output ('PASS '+$checks+' Windows package/installer checks; isolated temporary install and shortcut only. No user-profile installation, hardware or app launch.')
} finally {
 $resolved=[IO.Path]::GetFullPath($testRoot);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($resolved)-notlike'InGlorious-installer-test-*'){throw 'Unsafe test cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
