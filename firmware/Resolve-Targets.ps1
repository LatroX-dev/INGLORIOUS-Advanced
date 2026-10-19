#requires -Version 7.2
[CmdletBinding()]
param([string]$Board = 'all', [switch]$Engineering)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$defaultPath=Join-Path $PSScriptRoot 'targets.json'
$default=Get-Content -Raw -LiteralPath $defaultPath | ConvertFrom-Json
if($default.schema-ne1-or$default.targets.Count-ne16-or$default.storageBytes-ne32768){throw 'Unsupported default target manifest'}
$manifest=$default;$path=$defaultPath
if($Engineering){
 if($Board-cne'gmmk3-pro-100-iso'){throw 'Engineering builds require the exact board gmmk3-pro-100-iso; all/default selection is forbidden.'}
 $path=Join-Path $PSScriptRoot 'engineering/targets.json'
 $manifest=Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
 if($manifest.schema-ne1-or!$manifest.engineering-or$manifest.targets.Count-ne1-or$manifest.qmkCommit-cne$default.qmkCommit-or$manifest.storageBytes-ne$default.storageBytes-or$manifest.defaultManifestSha256-cne(Get-FileHash -LiteralPath $defaultPath).Hash){throw 'Unsupported engineering target manifest'}
 $target=$manifest.targets[0]
}
$targets=@($manifest.targets|Where-Object{$Board-eq'all'-or$_.name-ceq$Board-or$_.deviceId-ceq$Board})
if($targets.Count-eq0){throw "Unknown firmware target: $Board"}
foreach($target in $targets){
 if($target.boardId-eq16){
  if($target.name-cne'gmmk3-pro-100-iso'-or$target.deviceId-cne$target.name-or$target.qmk-cne'gmmk/inglorious3pro/p100/iso'-or$target.runtimeProductId-cne'1C10'-or$target.mcu-cne'WB32FQ95'-or$target.flashBytes-ne262144-or$target.ramBytes-ne36864){throw 'Retained-loader board identity or geometry changed'}
  if($target.applicationOffsetBytes-ne28672-or$target.protectedPrefixBytes-ne28672-or$target.identityBytes-ne256){throw 'Board 16 requires its protected resident-loader and identity-page layout'}
  if($target.firmwareVersion-cne'0.11.11'-or$target.firmwareBuildId-cne'000B0B01'-or$target.versionHeaderPath-cne'boards/gmmk3-pro-100-iso/ig_board_version.h'){throw 'Board 16 version identity changed'}
  if(!$target.PSObject.Properties['profileBankLayout']-or$target.profileBankLayout-cne'profile-pairs-v1'){throw 'Board 16 requires its reviewed hardware-profile storage layout'}
 }else{
  if($target.PSObject.Properties['profileBankLayout']){throw 'Hardware-profile storage is supported only on the reviewed board 16 target'}
  foreach($pair in @{applicationOffsetBytes=0;protectedPrefixBytes=0;identityBytes=0;firmwareVersion='0.11.9';firmwareBuildId='000B0901';versionHeaderPath='inglorious/ig_version.h'}.GetEnumerator()){
   $target|Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force
  }
 }
}
[pscustomobject]@{Manifest=$manifest;Path=$path;Targets=$targets;Engineering=[bool]$Engineering}
