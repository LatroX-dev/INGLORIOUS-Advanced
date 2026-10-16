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
function RejectedWithMessage([scriptblock]$Action,[string]$Expected,[string]$Message) { $caught=$null; try{$null=&$Action}catch{$caught=$_.Exception.Message}; Check ($caught-ceq$Expected) $Message }
$testParent=[IO.Path]::GetFullPath((Join-Path $workspace 'workspace/checks')).TrimEnd([IO.Path]::DirectorySeparatorChar)
for($part=$testParent;$part;$part=[IO.Path]::GetDirectoryName($part)){
 if((Test-Path -LiteralPath $part)-and((Get-Item -LiteralPath $part -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Test output must not use linked directories.'}
}
New-Item -ItemType Directory -Path $testParent -Force | Out-Null
$testRoot=Join-Path $testParent ('InGlorious-public-export-test-'+[Guid]::NewGuid().ToString('N'))
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
 $noise=@('AGENTS.md','docs/AGENTS.md','backups/private.bin','data/profile.json','artifacts/hardware.txt','tools/private.exe','src/InGlorious.Core/bin/secret.txt','firmware/local-firmware-bindings.json','firmware/releases/private.bin','workspace/public/README.md','workspace/public/.git/config','workspace/archive/reviews/private.txt')
 foreach($name in $noise){$path=Join-Path $fixture $name;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force|Out-Null;[IO.File]::WriteAllText($path,'private-test-noise')}
 $exportsRoot=Join-Path $fixture 'workspace/exports'
 New-Item -ItemType Directory -Path $exportsRoot | Out-Null
 $destination=Join-Path $exportsRoot 'reviewed-source'
 $fixturePlan=&$exporter -SourceRoot $fixture -Destination $destination
 Check (-not(Test-Path -LiteralPath $destination)) 'Plan with destination must not create it.'
 $result=&$exporter -SourceRoot $fixture -Destination $destination -Export
 Check ($result.mode-eq'local-export') 'Explicit local export completed.'
 foreach($name in @('.github/FUNDING.yml','.github/pull_request_template.md','SUPPORT.md','CODE_OF_CONDUCT.md')){
  Check (Test-Path -LiteralPath (Join-Path $destination $name)) ('Reviewed community file must be exported: '+$name)
 }
 Check ((Get-Content -LiteralPath (Join-Path $destination 'README.md') -Raw).StartsWith('# INGLORIOUS Advanced')) 'Generated export README must use the uppercase product brand.'
 $workspaceMarker=Get-Content -LiteralPath (Join-Path $destination 'inglorious.workspace.json') -Raw|ConvertFrom-Json
 Check ($workspaceMarker.name-ceq'INGLORIOUS Advanced') 'Generated workspace display name must use the uppercase product brand without renaming its file.'
 foreach($file in $result.files){$path=Join-Path $destination $file.path;Check ((Get-Item -LiteralPath $path).Length-eq$file.bytes -and (Get-FileHash -LiteralPath $path).Hash-eq$file.sha256) ('Persisted hash mismatch: '+$file.path)}
 foreach($name in $noise){Check (-not(Test-Path -LiteralPath (Join-Path $destination $name))) ('Private path copied: '+$name)}
 Check (-not(Test-Path -LiteralPath (Join-Path $destination 'workspace'))) 'Nested repositories, archives and earlier exports must stay out of the export.'
 Check ((Get-ChildItem -LiteralPath $destination -Recurse -File).Count-eq($result.fileCount+1)) 'Only planned files and manifest may exist.'
 $firstManifestHash=(Get-FileHash -LiteralPath (Join-Path $destination 'source-export-manifest.json')).Hash
 Rejected {&$exporter -SourceRoot $fixture -Destination $destination -Export} 'An existing destination must be rejected.'
 Check ((Get-FileHash -LiteralPath (Join-Path $destination 'source-export-manifest.json')).Hash-eq$firstManifestHash) 'Rejected overwrite must preserve existing export.'
 foreach($name in @('.','nested','workspace','workspace/exports','workspace/public/export','workspace/archive/export','workspace/exports-other/export','workspace/exports/deeper/export','workspace/exports/../outside','workspace/exports/../../outside')){
  RejectedWithMessage {&$exporter -SourceRoot $fixture -Destination (Join-Path $fixture $name) -Export} 'Destination must be a new direct child of SourceRoot/workspace/exports.' ('Unapproved internal destination must be rejected: '+$name)
 }
 $outsideSource=Join-Path $testRoot 'outside-source'
 RejectedWithMessage {&$exporter -SourceRoot $fixture -Destination $outsideSource -Export} 'Destination must be a new direct child of SourceRoot/workspace/exports.' 'An export outside its source project must be rejected.'
 Check (-not(Test-Path -LiteralPath $outsideSource)) 'Rejected external destinations must not be created.'
 $fixtureExporter=Join-Path $fixture 'scripts/Export-PublicSource.ps1'
 $exporterText=[IO.File]::ReadAllText($fixtureExporter)
 $allowlistGuard='if (($allow | Select-Object -Unique).Count -ne $allow.Count)'
 Check ($exporterText.Contains($allowlistGuard)) 'The allowlist mutation fixture must find its insertion point.'
 [IO.File]::WriteAllText($fixtureExporter,$exporterText.Replace($allowlistGuard,"`$allow.Add('workspace/archive/reviews/private.txt')`n"+$allowlistGuard))
 RejectedWithMessage {&$fixtureExporter -SourceRoot $fixture} 'An unsafe path entered the source allowlist.' 'Workspace material must fail closed even if accidentally added to the allowlist.'
 [IO.File]::WriteAllText($fixtureExporter,$exporterText)
 $linkedFixture=Join-Path $testRoot 'linked-source';New-Item -ItemType Directory -Path $linkedFixture | Out-Null
 $linkTarget=Join-Path $testRoot 'link-target';New-Item -ItemType Directory -Path (Join-Path $linkTarget 'exports') -Force | Out-Null
 $junction=Join-Path $linkedFixture 'workspace'
 New-Item -ItemType $(if([OperatingSystem]::IsWindows()){'Junction'}else{'SymbolicLink'}) -Path $junction -Target $linkTarget | Out-Null
 try {
  RejectedWithMessage {&$exporter -SourceRoot $linkedFixture -Destination (Join-Path $junction 'exports/blocked') -Export} 'Export refuses reparse points in source or destination paths.' 'The internal export exception must reject linked workspace directories.'
  Check (-not(Test-Path -LiteralPath (Join-Path $linkTarget 'exports/blocked'))) 'A linked destination must be rejected before output creation.'
 } finally { Remove-Item -LiteralPath $junction -Force }
 Rejected {&$exporter -SourceRoot $fixture -Destination '\\example.invalid\share\export' -Export} 'Network destination must be rejected before access.'
 Rejected {&$exporter -SourceRoot $fixture -Export} 'Explicit export requires explicit destination.'
 $unknown=Join-Path $fixture 'src/InGlorious.Core/Unreviewed.cs';[IO.File]::WriteAllText($unknown,'class Unreviewed {}')
 Rejected {&$exporter -SourceRoot $fixture} 'Unreviewed top-level app code must fail closed.'
 Remove-Item -LiteralPath $unknown
 $model=Join-Path $fixture 'src/InGlorious.Core/Models.cs';$original=[IO.File]::ReadAllText($model)
 [IO.File]::WriteAllText($model,$original+"`n// "+('C:'+'\Users'+'\PrivateFixture\settings'))
 $blocked=Join-Path $exportsRoot 'blocked-private-path'
 RejectedWithMessage {&$exporter -SourceRoot $fixture -Destination $blocked -Export} 'Local user path detected in allowlisted file: src/InGlorious.Core/Models.cs' 'Local user path must be rejected.'
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
 # Exercise the exact public contact exception with a synthetic private token
 # derived from the public address, independent of the current Windows username.
 $publicContact="inglorious@latrox.dev"
 $publicDomain=$publicContact.Split('@')[1]
 $privateContactToken=$publicDomain.Split('.')[0]
 [IO.File]::WriteAllText($privateWrapper,"`$args=@('-S','$privateContactToken')")
 [IO.File]::WriteAllText($model,$original+"`n// mailto:"+$publicContact)
 Check ((&$exporter -SourceRoot $fixture).mode-eq'plan-only') 'Only the exact explicitly public contact address is exempt.'
 foreach($otherAddress in @(('other@'+$publicDomain),('prefix'+$publicContact),($publicContact+'.invalid'),($publicContact+'@other.invalid'),("'"+$publicContact),$publicDomain)){
  [IO.File]::WriteAllText($model,$original+"`n// "+$otherAddress)
  Rejected {&$exporter -SourceRoot $fixture} 'The public contact exception must not mask other mailboxes, aliases or bare domains.'
 }
 # The same synthetic private token exercises public funding and badges. Keep
 # malformed identifiers assembled at runtime so this test remains exportable.
 $publicKoFi='https://ko-fi.com/latrox'
 [IO.File]::WriteAllText($model,$original+"`n// [Support]("+$publicKoFi+')')
 Check ((&$exporter -SourceRoot $fixture).mode-eq'plan-only') 'The exact public Ko-fi link is permitted.'
 foreach($otherLink in @(
  ('prefix'+$publicKoFi),($publicKoFi+'.invalid'),($publicKoFi+'/private'),($publicKoFi+'?account='+$privateContactToken),
  $publicKoFi.Replace('https:','http:'),$publicKoFi.Replace('ko-fi.com/','ko-fi.com.evil.invalid/'),
  ('https://example.invalid/?next='+$publicKoFi),$privateContactToken
 )){
  [IO.File]::WriteAllText($model,$original+"`n// "+$otherLink)
  Rejected {&$exporter -SourceRoot $fixture} 'The funding exception must not mask adjacent handles, altered URLs, nested URLs or bare aliases.'
 }
 [IO.File]::WriteAllText($model,$original)
 $funding=Join-Path $fixture '.github/FUNDING.yml';$fundingOriginal=[IO.File]::ReadAllText($funding)
 $fundingEntry='ko_fi: '+$publicKoFi.Substring($publicKoFi.LastIndexOf('/')+1)
 foreach($ending in @('',"`n","`r`n")){
  [IO.File]::WriteAllText($funding,$fundingEntry+$ending)
  Check ((&$exporter -SourceRoot $fixture).mode-eq'plan-only') 'The exact single-entry funding file permits its explicitly public account.'
 }
 foreach($badFunding in @(($fundingEntry+'-private'),('prefix_'+$fundingEntry),($fundingEntry+"`n# "+$privateContactToken),($fundingEntry+"`n"+$fundingEntry))){
  [IO.File]::WriteAllText($funding,$badFunding)
  Rejected {&$exporter -SourceRoot $fixture} 'The funding account exception must be restricted to the exact single entry.'
 }
 [IO.File]::WriteAllText($funding,$fundingOriginal)
 [IO.File]::WriteAllText($model,$original+"`n"+$fundingEntry+"`n")
 Rejected {&$exporter -SourceRoot $fixture} 'The funding account entry must not be exempt in another file.'
 $publicBadges=@(
  'https://img.shields.io/github/stars/LatroX-dev/INGLORIOUS-Advanced',
  'https://img.shields.io/github/downloads/LatroX-dev/INGLORIOUS-Advanced/total',
  'https://img.shields.io/github/v/release/LatroX-dev/INGLORIOUS-Advanced',
  'https://img.shields.io/github/actions/workflow/status/LatroX-dev/INGLORIOUS-Advanced/build.yml'
 )
 foreach($badge in $publicBadges){
  [IO.File]::WriteAllText($model,$original+"`n// ![Badge]("+$badge+'?style=flat&color=ef4444)')
  Check ((&$exporter -SourceRoot $fixture).mode-eq'plan-only') 'Reviewed complete badge endpoints and ordinary display queries are permitted.'
  foreach($otherBadge in @(
   ('prefix'+$badge),($badge+'/extra'),($badge+'?device='+$privateContactToken),
   $badge.Replace('/INGLORIOUS-Advanced','/INGLORIOUS-Advanced-private'),
   $badge.Replace('https:','http:'),$badge.Replace('img.shields.io/','img.shields.io.evil.invalid/'),
   ('https://example.invalid/?badge='+$badge)
  )){
   [IO.File]::WriteAllText($model,$original+"`n// "+$otherBadge)
   Rejected {&$exporter -SourceRoot $fixture} 'Badge exceptions must preserve checks on other endpoints, adjacent names, nested URLs and private query values.'
  }
 }
 foreach($otherEndpoint in @($publicBadges[1].Replace('/total','/latest'),$publicBadges[3].Replace('/build.yml','/private.yml'))){
  [IO.File]::WriteAllText($model,$original+"`n// "+$otherEndpoint)
  Rejected {&$exporter -SourceRoot $fixture} 'Unreviewed download and workflow badge endpoints must fail closed.'
 }
 [IO.File]::WriteAllText($model,$original)
 $last=&$exporter -SourceRoot $fixture
 Check ($last.mode-eq'plan-only') 'Restoring clean fixture yields a valid plan again.'
 Write-Output "PASS: $checks public-source-export checks; plans, exact hashes, exclusions, privacy and destination guards. No network, Git or hardware."
} finally {
 # Delete only the unique directory created above, after checking its resolved parent and name.
 $resolved=[IO.Path]::GetFullPath($testRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
 if([IO.Path]::GetDirectoryName($resolved)-cne$testParent -or -not$resolved.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved)-notmatch'^InGlorious-public-export-test-[a-f0-9]{32}$'){throw 'Unsafe test cleanup target; refusing deletion.'}
 $links=@(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object{($_.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0})
 if($links.Count){throw 'Unexpected reparse point in test tree; refusing recursive cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
