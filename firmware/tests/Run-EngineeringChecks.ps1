#requires -Version 7.2
[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$firmware=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolve=Join-Path $firmware 'Resolve-Targets.ps1'
$prepare=Join-Path $firmware 'Prepare-Overlay.ps1'
$build=Join-Path $firmware 'Build-Firmware.ps1'
$manifest=Get-Content -LiteralPath (Join-Path $firmware 'targets.json') -Raw|ConvertFrom-Json
$first15=@($manifest.targets|Where-Object boardId -lt 16)|ConvertTo-Json -Depth 10 -Compress
$originalSha='E0EA1ACAC7BDAF62347E2FB27A2360F0BF41F56DEAB6518EA06A15D0A810165E'
if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($first15)))-cne$originalSha){throw 'The original fifteen target definitions changed'}
$normal=& $resolve
if($normal.Engineering-or$normal.Targets.Count-ne16){throw 'Normal target selection failed'}
foreach($target in @($normal.Targets|Where-Object boardId -lt 16)){
 if($target.firmwareVersion-cne'0.11.8'-or$target.firmwareBuildId-cne'000B0801'-or$target.applicationOffsetBytes-ne0-or$target.protectedPrefixBytes-ne0){throw 'Original board version/layout changed'}
}
$explicit=& $resolve -Board gmmk3-pro-100-iso
$engineering=& $resolve -Engineering -Board gmmk3-pro-100-iso
foreach($selection in @($explicit,$engineering)){
 if($selection.Targets.Count-ne1-or$selection.Targets[0].boardId-ne16){throw 'Exact board 16 selection failed'}
 $target=$selection.Targets[0]
 if($target.applicationOffsetBytes-ne28672-or$target.protectedPrefixBytes-ne28672-or$target.identityBytes-ne256){throw 'Retained-loader layout changed'}
 if($target.firmwareVersion-cne'0.11.8'-or$target.firmwareBuildId-cne'000B0801'){throw 'Board 16 identity changed'}
}
$cases=@(
 @{name='resolver engineering default';run={& $resolve -Engineering};reason='exact board'},
 @{name='resolver engineering all';run={& $resolve -Engineering -Board all};reason='exact board'},
 @{name='resolver normal target in engineering';run={& $resolve -Engineering -Board gmmk3-100-iso};reason='exact board'},
 @{name='resolver unsupported variant';run={& $resolve -Board gmmk3-pro-100-ansi};reason='Unknown firmware target'},
 @{name='overlay engineering all';run={& $prepare -Engineering -Board all};reason='exact board'},
 @{name='build engineering all';run={& $build -Engineering -Board all};reason='exact board'},
 @{name='build no explicit engineering output';run={& $build -Engineering -Board gmmk3-pro-100-iso};reason='explicit separate OutputDirectory'},
 @{name='build engineering production output';run={& $build -Engineering -Board gmmk3-pro-100-iso -OutputDirectory (Join-Path $firmware 'build')};reason='separate directory below artifacts'}
)
$results=@()
foreach($case in $cases){
 $rejected=$false
 try{& $case.run | Out-Null}catch{if($_.Exception.Message-notmatch[regex]::Escape($case.reason)){throw};$rejected=$true}
 if(!$rejected){throw "Guard did not reject $($case.name)"}
 $results+=$case.name
}
Write-Output "PASS: 16 normal targets, unchanged first 15 layouts and coherent 0.11.8 identities, retained-loader board 16 and explicit engineering selection; $($results.Count) unsafe selections rejected before compiler/overlay output. No hardware access."
if($ReportPath){
 $report=[ordered]@{schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false;defaultTargetCount=16;retainedLoaderBoard=16;originalFifteenTargetDefinitionsSha256=$originalSha;rejectedSelections=$results;resolverSha256=(Get-FileHash -LiteralPath $resolve).Hash}
 $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$stream.Write([Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 5)));$stream.Flush($true)}finally{$stream.Dispose()}
}
