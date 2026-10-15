[CmdletBinding()]
param([string]$Board='all', [string]$OutputDirectory=(Join-Path $PSScriptRoot 'build'), [switch]$Engineering, [switch]$Clean)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qmk=Join-Path $PSScriptRoot 'qmk_firmware'
$versionHeader=Join-Path $PSScriptRoot 'inglorious/ig_version.h'
function Get-TargetIdentity($target){
$identityHeader=Join-Path $PSScriptRoot $target.versionHeaderPath
$versionText=[IO.File]::ReadAllText($identityHeader)
$versionParts=@()
foreach($part in @('MAJOR','MINOR','PATCH')){
 $matches=[regex]::Matches($versionText,('(?m)^#define IG_FW_VERSION_'+$part+' ([0-9]+)\r?$'))
 if($matches.Count-ne1){throw ('Missing or ambiguous firmware version component: '+$part)}
 $versionParts += [uint16]::Parse($matches[0].Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
}
$buildIdMatch=[regex]::Matches($versionText,'(?m)^#define IG_FW_BUILD_ID 0x([0-9A-Fa-f]{8})UL\r?$')
if($buildIdMatch.Count-ne1){throw 'Missing or ambiguous explicit firmware build identifier.'}
$firmwareVersion=$versionParts -join '.'
$firmwareBuildId=$buildIdMatch[0].Groups[1].Value.ToUpperInvariant()
if($firmwareVersion-cne$target.firmwareVersion-or$firmwareBuildId-cne$target.firmwareBuildId){throw ('Version header differs from target: '+$target.deviceId)}
return [ordered]@{deviceId=$target.deviceId;firmwareVersion=$firmwareVersion;firmwareBuildId=$firmwareBuildId;
 identityVersionHeaderPath=$target.versionHeaderPath;identityVersionHeaderSha256=(Get-FileHash -LiteralPath $identityHeader).Hash;
 applicationOffsetBytes=$target.applicationOffsetBytes;protectedPrefixBytes=$target.protectedPrefixBytes;identityBytes=$target.identityBytes;
 cleanTargetBuild=([bool]$Clean-or[bool]$Engineering-or$target.boardId-eq16)}
}
$bash=Join-Path $PSScriptRoot 'toolchain/qmk-msys/{app}/usr/bin/bash.exe'
$selection=& (Join-Path $PSScriptRoot 'Resolve-Targets.ps1') -Board $Board -Engineering:$Engineering
$targetManifest=$selection.Manifest
$targetBuilds=@($selection.Targets|ForEach-Object{Get-TargetIdentity $_})
$single=if($targetBuilds.Count-eq1){$targetBuilds[0]}else{$null}
$versions=@($targetBuilds.firmwareVersion|Sort-Object -Unique)
$buildIds=@($targetBuilds.firmwareBuildId|Sort-Object -Unique)
$firmwareVersion=if($versions.Count-eq1){$versions[0]}else{$null}
$firmwareBuildId=if($buildIds.Count-eq1){$buildIds[0]}else{$null}
if($Engineering-and!$PSBoundParameters.ContainsKey('OutputDirectory')){throw 'Engineering builds require an explicit separate OutputDirectory.'}
if($Engineering){
 $engineeringOutput=[IO.Path]::GetFullPath($OutputDirectory)
 $artifactRoot=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))+[IO.Path]::DirectorySeparatorChar
 if(!$engineeringOutput.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Engineering output must use a separate directory below artifacts.'}
 $previousManifest=Join-Path $engineeringOutput 'manifest.json'
 if(Test-Path -LiteralPath $previousManifest){
  $previous=Get-Content -Raw -LiteralPath $previousManifest|ConvertFrom-Json
  if(!$previous.PSObject.Properties['engineering']-or!$previous.engineering-or@($previous.targets).Count-ne1-or$previous.targets[0]-cne$Board){throw 'Engineering output must not overwrite another build manifest.'}
  if(!$previous.PSObject.Properties['applicationOffsetBytes']-or$previous.applicationOffsetBytes-ne$targetManifest.targets[0].applicationOffsetBytes){throw 'Engineering output must not overwrite a build with a different application origin.'}
 }
}
$existingOutputManifest=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'manifest.json'
if(Test-Path -LiteralPath $existingOutputManifest){
 $previous=Get-Content -LiteralPath $existingOutputManifest -Raw|ConvertFrom-Json
 if(!$previous.PSObject.Properties['targetBuilds']-or($previous.targetBuilds|ConvertTo-Json -Depth 8 -Compress)-cne($targetBuilds|ConvertTo-Json -Depth 8 -Compress)-or[bool]$previous.engineering-ne[bool]$Engineering){throw 'Build output must not overwrite another target selection, version or layout'}
}
$expectedCommit=$targetManifest.qmkCommit
if (!(Test-Path -LiteralPath $bash)) { throw 'Portable QMK-MSYS is missing. This script does not install or download tools.' }
$actualCommit=(& git -C $qmk rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $expectedCommit) { throw 'The QMK checkout does not match the reviewed commit.' }
$submodules=@{}
foreach($relative in @('lib/chibios','lib/chibios-contrib','lib/lufa','lib/printf')){
 $expected=(& git -C $qmk rev-parse ($expectedCommit+':'+$relative)).Trim()
 if($LASTEXITCODE-ne0){throw ('Missing pinned submodule: '+$relative)}
 $actual=(& git -C (Join-Path $qmk $relative) rev-parse HEAD).Trim()
 if($LASTEXITCODE-ne0-or$actual-cne$expected){throw ('Wrong submodule revision: '+$relative)}
 $submodules[$relative]=$actual
}
$compilerOutput=@(& $bash --noprofile --norc -c 'export PATH=/opt/qmk/bin:/mingw64/bin:/usr/bin:/bin; arm-none-eabi-gcc --version')
$compilerVersion=$compilerOutput[0]
if($LASTEXITCODE-ne0-or$compilerVersion-notmatch'15\.2\.0'){throw 'The expected local ARM compiler is not available'}
$targets=$selection.Targets
foreach($target in $targets){if($target.name-cnotmatch'\A[a-z0-9-]+\z'-or$target.qmk-cnotmatch'\Agmmk/[a-z0-9/]+\z'){throw 'Unsafe target name'}}
& (Join-Path $PSScriptRoot 'Prepare-Overlay.ps1') -Board $Board -Engineering:$Engineering
$output=[IO.Path]::GetFullPath($OutputDirectory)
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'releases'))
if(!$output.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or$output.Equals($releaseRoot,[StringComparison]::OrdinalIgnoreCase)-or$output.StartsWith($releaseRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must remain in the workspace outside frozen releases'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$inputFiles=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'inglorious') -File | Where-Object{$_.Extension-in@('.c','.h','.inc')})
foreach($target in $targets){$inputFiles+=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot ('keymaps/'+$target.name)) -File)}
if(@($targets|Where-Object boardId -ge 10).Count){
 $inputFiles+=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'ports/gmmk3') -File -Recurse)
 $inputFiles+=Get-Item -LiteralPath (Join-Path $PSScriptRoot 'Prepare-Gmmk3References.py')
 foreach($prefix in @($targets|Where-Object boardId -ge 10|ForEach-Object{$_.qmk.Split('/')[1]}|Sort-Object -Unique)){
  $inputFiles+=@(Get-ChildItem -LiteralPath (Join-Path $qmk ('keyboards/gmmk/'+$prefix)) -File -Recurse)
 }
}
$inputFiles+=Get-Item -LiteralPath $selection.Path
foreach($identity in $targetBuilds){$inputFiles+=Get-Item -LiteralPath (Join-Path $PSScriptRoot $identity.identityVersionHeaderPath)}
foreach($relative in @('targets.json','Resolve-Targets.ps1','Prepare-Overlay.ps1','Build-Firmware.ps1','Patch-QmkTiming.ps1','Patch-QmkStorage.ps1','qmk_firmware/quantum/wear_leveling/wear_leveling.c','qmk_firmware/tmk_core/protocol/usb_descriptor.c')){$inputFiles+=Get-Item -LiteralPath (Join-Path $PSScriptRoot $relative)}
$inputHashes=@{}
foreach($file in $inputFiles){$inputHashes[$file.FullName]=(Get-FileHash -LiteralPath $file.FullName).Hash}
$records=@()
foreach($target in $targets) {
 $stem=$target.qmk.Replace('/','_')+'_inglorious'
 $applicationOffset=$target.applicationOffsetBytes
 $applicationBase=0x08000000+$applicationOffset
 $identityBytes=$target.identityBytes
 $limit=$target.flashBytes-$targetManifest.storageBytes-$applicationOffset-$identityBytes
 $qmkMsys=((& $bash --noprofile --norc -c ('/usr/bin/cygpath -u '+"'"+$qmk.Replace("'","'\''")+"'")).Trim())
 if ($LASTEXITCODE -ne 0) { throw 'Could not resolve QMK source path.' }
 # MILC invokes a shell from native Windows Python, so resolve the pinned Bash to a Windows path.
 # QMK does not reliably invalidate a cached ELF when only a custom linker file
 # changes. Retained-loader and engineering images always use a clean target build.
 $cleanOption=if($Clean-or$Engineering-or$target.boardId-eq16){' -c'}else{''}
 $command='export PATH=/opt/qmk/bin:/opt/uv/tools/bin:/mingw64/bin:/usr/bin:/bin; export PYTHONUTF8=1 MSYSTEM=MINGW64 QMK_DISTRIB_DIR=/opt/qmk; export SHELL="$(/usr/bin/cygpath -m /usr/bin/bash.exe)"; cd '+"'"+$qmkMsys.Replace("'","'\''")+"'"+'; qmk compile'+$cleanOption+' -j 8 -kb '+$target.qmk+' -km inglorious; rc=$?; arm-none-eabi-size -A .build/'+$stem+'.elf; exit $rc'
 & $bash --noprofile --norc -c $command *> (Join-Path $output ($target.name+'-build.log'))
 if ($LASTEXITCODE -ne 0) {
   Get-Content -LiteralPath (Join-Path $output ($target.name+'-build.log')) -Tail 60 | Write-Host
   throw ('Offline ARM build failed: '+$target.name+'; see build log.')
 }
 foreach($path in $inputHashes.Keys){if((Get-FileHash -LiteralPath $path).Hash-cne$inputHashes[$path]){throw ('Source changed during serial builds: '+$path)}}
 $source=Join-Path $qmk ('.build/'+$stem+'.bin')
 $bytes=[IO.File]::ReadAllBytes($source)
 if ($bytes.Length -lt 1024 -or $bytes.Length -gt $limit) { throw ('Image length exceeds reserved code range: '+$target.name) }
 $stack=[BitConverter]::ToUInt32($bytes,0);$reset=[BitConverter]::ToUInt32($bytes,4)
 if ($stack -lt 0x20000000 -or $stack -gt (0x20000000+$target.ramBytes) -or ($stack%4) -ne 0 -or ($reset-band1)-ne1 -or ($reset-band0xfffffffe)-lt$applicationBase -or ($reset-band0xfffffffe)-ge($applicationBase+$bytes.Length)) { throw ('Invalid ARM vector table: '+$target.name) }
 foreach($extension in @('bin','elf','map','hex')) {
   $from=Join-Path $qmk ('.build/'+$stem+'.'+$extension)
   $destination=Join-Path $output ($target.name+'.'+$extension)
   Copy-Item -LiteralPath $from -Destination $destination -Force
   $file=Get-Item -LiteralPath $destination
   $records += [ordered]@{path=$file.Name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash;kind='offline-custom-firmware-'+$extension;board=$target.qmk;deviceId=$target.deviceId;boardId=$target.boardId}
 }
 $stackFiles=@(Get-ChildItem -LiteralPath (Join-Path $qmk ('.build/obj_'+$stem)) -Filter '*.su' -File -Recurse | Sort-Object FullName)
 if($stackFiles.Count-eq0){throw ('No compiler stack evidence: '+$target.name)}
 $stackReport=Join-Path $output ($target.name+'-stack-usage.txt')
 [IO.File]::WriteAllText($stackReport, (($stackFiles | ForEach-Object { '# '+[IO.Path]::GetRelativePath($qmk,$_.FullName)+[Environment]::NewLine+[IO.File]::ReadAllText($_.FullName) }) -join [Environment]::NewLine))
 foreach($suffix in @('-build.log','-stack-usage.txt')){
  $file=Get-Item -LiteralPath (Join-Path $output ($target.name+$suffix))
  $records += [ordered]@{path=$file.Name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash;kind='offline-build-evidence';board=$target.qmk;deviceId=$target.deviceId;boardId=$target.boardId}
 }
 Write-Host ($target.name+': built '+$bytes.Length+' bytes, reserved code limit '+$limit+' bytes.')
}
$manifest=[ordered]@{
 schema=1;createdUtc=[DateTime]::UtcNow.ToString('o');qmkCommit=$expectedCommit;protocolSchema=1;configSchemas=@(1,2,3,4);firmwareVersion=$firmwareVersion;firmwareBuildId=$firmwareBuildId
 compiler='QMK-MSYS1.12.0, arm-none-eabi-gcc15.2.0';lto=$false
 compilerVersion=$compilerVersion;submodules=$submodules
 hardwareFlashed=$false;hardwareAcceptanceTested=$false;powerLossTested=$false
 engineering=[bool]$Engineering;releaseCandidate=$false
 targetBuilds=$targetBuilds
 applicationOffsetBytes=$(if($single){$single.applicationOffsetBytes}else{$null})
 protectedPrefixBytes=$(if($single){$single.protectedPrefixBytes}else{$null})
 identityBytes=$(if($single){$single.identityBytes}else{$null})
 cleanTargetBuild=(@($targetBuilds|Where-Object{!$_.cleanTargetBuild}).Count-eq0)
 includesResidentLoader=$false
 scope='Offline firmware binaries. Final 32768 internal flash bytes reserved for wear levelling; external SPI EEPROM disabled.'
 targetManifestPath=[IO.Path]::GetRelativePath($PSScriptRoot,$selection.Path).Replace('\','/')
 targetManifestSha256=(Get-FileHash -LiteralPath $selection.Path).Hash
 manufacturerProvenanceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ports/gmmk3/provenance.json')).Hash
 manufacturerSourcesCatalogSha256=(Get-FileHash -LiteralPath (Join-Path $workspace 'reference/glorious-gmmk3/sources.json')).Hash
 sourceSnapshot=@($inputHashes.Keys | Sort-Object | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($PSScriptRoot,$_).Replace('\','/');sha256=$inputHashes[$_]} })
 targets=@($targets | ForEach-Object { $_.deviceId })
 generatedSources=@($targets | ForEach-Object { $target=$_; Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot ('keymaps/'+$target.name)) -File | ForEach-Object { [ordered]@{path='keymaps/'+$target.name+'/'+$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } })
 commonSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/inglorious.c')).Hash
 versionHeaderSha256=(Get-FileHash -LiteralPath $versionHeader).Hash
 identityVersionHeaderPath=$(if($single){$single.identityVersionHeaderPath}else{$null})
 identityVersionHeaderSha256=$(if($single){$single.identityVersionHeaderSha256}else{$null})
 effectsSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_effects.h')).Hash
 colorOutputHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_color.h')).Hash
 timingSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_timing.c')).Hash
 timingHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_timing.h')).Hash
 descriptorPatchScriptSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Patch-QmkTiming.ps1')).Hash
 descriptorSourceSha256=(Get-FileHash -LiteralPath (Join-Path $qmk 'tmk_core/protocol/usb_descriptor.c')).Hash
 wb32BulkSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/wb32_bulk.c')).Hash
 storagePatchScriptSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Patch-QmkStorage.ps1')).Hash
 storageHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_storage.h')).Hash
 storageImplementationSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'inglorious/ig_storage.inc')).Hash
 wearLevelingSourceSha256=(Get-FileHash -LiteralPath (Join-Path $qmk 'quantum/wear_leveling/wear_leveling.c')).Hash
 artifacts=$records
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'),($manifest|ConvertTo-Json -Depth 8))
