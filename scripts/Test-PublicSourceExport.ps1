#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exporter=Join-Path $PSScriptRoot 'Export-PublicSource.ps1'
$checks=0
function Check([bool]$Condition,[string]$Message) { $script:checks++; if(-not $Condition){throw $Message} }
function Rejected([scriptblock]$Action,[string]$Message) { $caught=$false; try{$null=&$Action}catch{$caught=$true}; Check $caught $Message }
$tempParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot=Join-Path $tempParent ('InGlorious-public-export-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
 $fixture=Join-Path $testRoot 'source';New-Item -ItemType Directory -Path $fixture | Out-Null
 $plan=&$exporter
 Check ($plan.mode-eq'plan-only' -and $plan.fileCount-gt50) 'Default invocation must be a substantive read-only plan.'
 foreach($file in $plan.files){
  $source=Join-Path $workspace $file.path
  if(-not(Test-Path -LiteralPath $source)){continue}
  $target=Join-Path $fixture $file.path
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
  Copy-Item -LiteralPath $source -Destination $target
 }
 $noise=@('backups/private.bin','data/profile.json','artifacts/hardware.txt','tools/private.exe','src/InGlorious.Core/bin/secret.txt','firmware/local-firmware-bindings.json','firmware/releases/private.bin')
 foreach($name in $noise){$path=Join-Path $fixture $name;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force|Out-Null;[IO.File]::WriteAllText($path,'private-test-noise')}
 $destination=Join-Path $testRoot 'export'
 $fixturePlan=&$exporter -SourceRoot $fixture -Destination $destination
 Check (-not(Test-Path -LiteralPath $destination)) 'Plan with destination must not create it.'
 $result=&$exporter -SourceRoot $fixture -Destination $destination -Export
 Check ($result.mode-eq'local-export') 'Explicit local export completed.'
 Check ((Get-Content -LiteralPath (Join-Path $destination 'README.md') -Raw).StartsWith('# INGLORIOUS Advanced')) 'Generated export README must use the uppercase product brand.'
 $workspaceMarker=Get-Content -LiteralPath (Join-Path $destination 'inglorious.workspace.json') -Raw|ConvertFrom-Json
 Check ($workspaceMarker.name-ceq'INGLORIOUS Advanced') 'Generated workspace display name must use the uppercase product brand without renaming its file.'
 foreach($file in $result.files){$path=Join-Path $destination $file.path;Check ((Get-Item -LiteralPath $path).Length-eq$file.bytes -and (Get-FileHash -LiteralPath $path).Hash-eq$file.sha256) ('Persisted hash mismatch: '+$file.path)}
 foreach($name in $noise){Check (-not(Test-Path -LiteralPath (Join-Path $destination $name))) ('Private path copied: '+$name)}
 Check ((Get-ChildItem -LiteralPath $destination -Recurse -File).Count-eq($result.fileCount+1)) 'Only planned files and manifest may exist.'
 $firstManifestHash=(Get-FileHash -LiteralPath (Join-Path $destination 'source-export-manifest.json')).Hash
 Rejected {&$exporter -SourceRoot $fixture -Destination $destination -Export} 'An existing destination must be rejected.'
 Check ((Get-FileHash -LiteralPath (Join-Path $destination 'source-export-manifest.json')).Hash-eq$firstManifestHash) 'Rejected overwrite must preserve existing export.'
 Rejected {&$exporter -SourceRoot $fixture -Destination (Join-Path $fixture 'nested') -Export} 'Nested source destination must be rejected.'
 Rejected {&$exporter -SourceRoot $fixture -Destination '\\example.invalid\share\export' -Export} 'Network destination must be rejected before access.'
 Rejected {&$exporter -SourceRoot $fixture -Export} 'Explicit export requires explicit destination.'
 $unknown=Join-Path $fixture 'src/InGlorious.Core/Unreviewed.cs';[IO.File]::WriteAllText($unknown,'class Unreviewed {}')
 Rejected {&$exporter -SourceRoot $fixture} 'Unreviewed top-level app code must fail closed.'
 Remove-Item -LiteralPath $unknown
 $model=Join-Path $fixture 'src/InGlorious.Core/Models.cs';$original=[IO.File]::ReadAllText($model)
 [IO.File]::WriteAllText($model,$original+"`n// "+('C:'+'\Users'+'\PrivateFixture\settings'))
 $blocked=Join-Path $testRoot 'blocked-private-path'
 Rejected {&$exporter -SourceRoot $fixture -Destination $blocked -Export} 'Local user path must be rejected.'
 Check (-not(Test-Path -LiteralPath $blocked)) 'All content checks must precede destination creation.'
 [IO.File]::WriteAllText($model,$original)
 foreach($secret in @(('gh'+'p_'+('A'*36)),('github'+'_pat_'+('B'*40)),('-----BEGIN '+'PRIVATE KEY-----'),('/home'+'/privacy-test/settings'),('C:'+'\Users'+'\PrivateFixture\settings'))){
  [IO.File]::WriteAllText($model,$original+"`n// "+$secret)
  Rejected {&$exporter -SourceRoot $fixture} 'Credential and user-path fixtures must fail closed.'
 }
 [IO.File]::WriteAllText($model,$original)
 foreach($encoding in @([Text.Encoding]::Unicode,[Text.Encoding]::BigEndianUnicode)){
  [IO.File]::WriteAllBytes($model,$encoding.GetBytes($original+"`n// "+('C:'+'\Users'+'\PrivateFixture\settings')))
  Rejected {&$exporter -SourceRoot $fixture} 'UTF-16 user-path fixtures must be rejected.'
 }
 [IO.File]::WriteAllText($model,$original)
 $privateWrapper=Join-Path $fixture 'scripts/Invoke-ProFirmware.ps1'
 $instance='USB\VID_1234&PID_5678\'+('PRIVATE-'+'FIXTURE')
 $serial='FIXTURE-'+'SERIAL-ONLY';$port='7-'+'8.9'
 [IO.File]::WriteAllText($privateWrapper,"`$instance='$instance';`$args=@('-S','$serial','-p','$port')")
 foreach($secret in @($instance,$serial,$port)){
  [IO.File]::WriteAllText($model,$original+"`n// "+$secret)
  Rejected {&$exporter -SourceRoot $fixture} 'Known private wrapper identifier must be rejected without disclosure.'
 }
 [IO.File]::WriteAllText($model,$original)
 $last=&$exporter -SourceRoot $fixture
 Check ($last.mode-eq'plan-only') 'Restoring clean fixture yields a valid plan again.'
 Write-Output "PASS: $checks public-source-export checks; plans, exact hashes, exclusions, privacy and destination guards. No network, Git or hardware."
} finally {
 # Delete only the unique directory created above, after checking its resolved parent and name.
 $resolved=[IO.Path]::GetFullPath($testRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
 if([IO.Path]::GetDirectoryName($resolved)-cne$tempParent -or [IO.Path]::GetFileName($resolved)-notmatch'^InGlorious-public-export-test-[a-f0-9]{32}$'){throw 'Unsafe test cleanup target; refusing deletion.'}
 $links=@(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object{($_.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0})
 if($links.Count){throw 'Unexpected reparse point in test tree; refusing recursive cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
