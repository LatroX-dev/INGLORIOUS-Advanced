#requires -Version 7.2
[CmdletBinding()]
param([string]$Board = 'all', [switch]$Engineering, [switch]$ProfileBanks)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Write-IfDifferent([string]$Path,[string]$Text) {
 if (!(Test-Path -LiteralPath $Path) -or [IO.File]::ReadAllText($Path) -cne $Text) { [IO.File]::WriteAllText($Path,$Text) }
}
function Copy-IfDifferent([string]$Source,[string]$Destination) {
 $target=Join-Path $Destination ([IO.Path]::GetFileName($Source))
 if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $Source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { Copy-Item -LiteralPath $Source -Destination $target -Force }
}
function Assert-Pinned([string]$Relative) {
 if($Relative.StartsWith('keyboards/gmmk/inglorious3pro/',[StringComparison]::Ordinal)){
  $source=Join-Path $PSScriptRoot ('ports/gmmk3/'+$Relative.Substring('keyboards/gmmk/inglorious3pro/'.Length))
  if(!(Test-Path -LiteralPath $source)-or(Get-FileHash -LiteralPath $source).Hash-ne(Get-FileHash -LiteralPath (Join-Path $qmk $Relative)).Hash){throw "Modified engineering derivative source: $Relative"}
  return
 }
 if($Relative.StartsWith('keyboards/gmmk/inglorious3/',[StringComparison]::Ordinal)){
  $source=Join-Path $PSScriptRoot ('ports/gmmk3/'+$Relative.Substring('keyboards/gmmk/inglorious3/'.Length))
  if(!(Test-Path -LiteralPath $source)-or(Get-FileHash -LiteralPath $source).Hash-ne(Get-FileHash -LiteralPath (Join-Path $qmk $Relative)).Hash){throw "Modified normalized manufacturer source: $Relative"}
  return
 }
 $expected=& git -C $qmk rev-parse ($manifest.qmkCommit+':'+$Relative)
 if($LASTEXITCODE -ne 0){throw "Missing pinned source: $Relative"}
 $actual=& git -C $qmk hash-object --no-filters -- $Relative
 if($LASTEXITCODE -ne 0 -or $actual -cne $expected){throw "Modified upstream board source: $Relative"}
}
function Get-BaseArguments([string]$Text,[string]$Layout,[string]$Layer='') {
 $clean=[regex]::Replace($Text,'(?s)/\*.*?\*/|(?m)//[^\r\n]*','')
 $prefix=if($Layer){'\['+[regex]::Escape($Layer)+'\]\s*=\s*'}else{'\b'}
 $match=[regex]::Match($clean,$prefix+[regex]::Escape($Layout)+'\s*\(')
 if(!$match.Success){throw "Missing base layout $Layout"}
 $depth=1;$start=$match.Index+$match.Length;$values=[Collections.Generic.List[string]]::new()
 for($i=$start;$i-lt$clean.Length;$i++){
  if($clean[$i]-eq'('){$depth++}
  elseif($clean[$i]-eq')'){$depth--;if($depth-eq0){$values.Add($clean.Substring($start,$i-$start).Trim());break}}
  elseif($clean[$i]-eq','-and$depth-eq1){$values.Add($clean.Substring($start,$i-$start).Trim());$start=$i+1}
 }
 if($depth-ne0-or$values.Count-eq0){throw 'Unterminated or empty base keymap'}
 return ,$values.ToArray()
}
function Get-OsSwitchDefinition($Target,$Json,$Keys,[string[]]$Codes) {
 if($Target.boardId-lt10-or$Target.boardId-gt16){return "#define IG_HAS_OS_SWITCH 0`n"}
 $variant=[regex]::Match($Target.qmk,'/(p(?:65|75|100))/(ansi|iso)$')
 if(!$variant.Success){throw 'Missing exact manufacturer switch variant'}
 $relative='reference/glorious-gmmk3/'+$variant.Groups[1].Value
 $keymapPath=$relative+'/'+$variant.Groups[2].Value+'/keymaps/default/keymap.c'
 # These original files were hash-checked against the pinned provenance above.
 foreach($path in @($keymapPath,($relative+'/config.h'))){
  if(@($provenance.originalSources|Where-Object path -CEQ $path).Count-ne1){throw "Unverified switch source: $path"}
 }
 $original=[IO.File]::ReadAllText((Join-Path $workspace $keymapPath))
 $configText=[IO.File]::ReadAllText((Join-Path $workspace ($relative+'/config.h')))
 $pinMatches=[regex]::Matches($configText,'(?m)^#define DIP_SWITCH_PINS\s+\{\s*([A-Z][0-9]+)\s*\}')
 if($pinMatches.Count-ne1){throw 'Expected one fixed manufacturer OS switch pin'}
 $pin=$pinMatches[0].Groups[1].Value
 $otherPins=@($Json.matrix_pins.rows)+@($Json.matrix_pins.cols)+@($Json.encoder.rotary.pin_a)+@($Json.encoder.rotary.pin_b)
 $otherPins+=@([regex]::Matches($configText,'(?m)^#define \w*PIN(?:_[0-9]+)?\s+([A-Z][0-9]+)\s*$')|ForEach-Object{$_.Groups[1].Value})
 if($pin-in$otherPins){throw "OS switch pin overlaps matrix/encoder/LED/SPI: $($Target.name)"}
 $polarity=[regex]::Match($original,'(?s)bool dip_switch_update_user\([^)]*\)\s*\{\s*if\s*\(index\s*==\s*0\)\s*\{\s*if\s*\(active\)\s*\{\s*layer_off\((WIN_BL|MACOS_BL)\);\s*layer_on\((WIN_BL|MACOS_BL)\);\s*\}\s*else\s*\{\s*layer_off\((WIN_BL|MACOS_BL)\);\s*layer_on\((WIN_BL|MACOS_BL)\);')
 if(!$polarity.Success-or$polarity.Groups[1].Value-ceq$polarity.Groups[2].Value-or$polarity.Groups[1].Value-cne$polarity.Groups[4].Value-or$polarity.Groups[2].Value-cne$polarity.Groups[3].Value){throw 'Unrecognized manufacturer OS switch polarity'}
 # QMK DIP inputs use pull-ups and active=!readPin(). Preserve each layout's polarity.
 $macLevel=if($polarity.Groups[2].Value-ceq'MACOS_BL'){0}else{1}
 $win=Get-BaseArguments $original $Target.layout 'WIN_BL';$mac=Get-BaseArguments $original $Target.layout 'MACOS_BL'
 if($win.Count-ne$Keys.Count-or$mac.Count-ne$Keys.Count){throw 'Manufacturer OS layout geometry differs'}
 $aliases=@{KC_LWIN='KC_LGUI';KC_RWIN='KC_RGUI';KC_LOPT='KC_LALT';KC_ROPT='KC_RALT';KC_LCMD='KC_LGUI';KC_RCMD='KC_RGUI'}
 $values=@{KC_LGUI=0xE3;KC_LALT=0xE2;KC_RGUI=0xE7;KC_RALT=0xE6;KC_APP=0x65}
 $bindings=@()
 for($i=0;$i-lt$win.Count;$i++){
  $w=$win[$i];$m=$mac[$i];if($aliases.ContainsKey($w)){$w=$aliases[$w]};if($aliases.ContainsKey($m)){$m=$aliases[$m]}
  if($w-ceq'MO(WIN_FL)'-and$m-ceq'MO(MACOS_FL)'){continue} # Keep all four user layers independent of OS mode.
  $code=$Codes[$i];if($aliases.ContainsKey($code)){$code=$aliases[$code]}
  if($code-cne$w){throw "Manufacturer Windows binding differs from normalized base: $($Target.name) key $i"}
  if($w-ceq$m){continue}
  if(!$values.ContainsKey($w)-or!$values.ContainsKey($m)){throw "Unrecognized native OS binding: $w -> $m"}
  $bindings+='{'+$Keys[$i].matrix[0]+','+$Keys[$i].matrix[1]+',0x'+$values[$w].ToString('X2')+',0x'+$values[$m].ToString('X2')+'}'
 }
 if($bindings.Count-lt3-or$bindings.Count-gt4){throw 'Unexpected native OS binding count'}
 return "#define IG_HAS_OS_SWITCH 1`n#define IG_OS_SWITCH_PIN $pin`n#define IG_OS_MAC_LEVEL $macLevel`n#define IG_OS_BINDINGS { "+($bindings-join', ')+" }`n"
}
function Find-Key([string]$Code,$Keys,[string[]]$Codes) {
 $indices=@(0..($Codes.Count-1)|Where-Object{$Codes[$_] -ceq $Code})
 if($indices.Count-ne1){throw "Expected exactly one physical $Code key"}
 return ,@([int]$Keys[$indices[0]].matrix[0],[int]$Keys[$indices[0]].matrix[1])
}
$qmk=Join-Path $PSScriptRoot 'qmk_firmware'
$selection=& (Join-Path $PSScriptRoot 'Resolve-Targets.ps1') -Board $Board -Engineering:$Engineering
$manifest=$selection.Manifest
$actualCommit=(& git -C $qmk rev-parse HEAD).Trim()
if($LASTEXITCODE-ne0-or$actualCommit-cne$manifest.qmkCommit){throw 'The QMK checkout does not match the target manifest'}
$targets=$selection.Targets
if($ProfileBanks -and (@($targets).Count-ne1 -or $targets[0].boardId-ne16)){throw 'Profile-bank builds require the single reviewed board16 target.'}
if(@($targets|Where-Object boardId -ge 10).Count){
 $workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
 $provenance=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'ports/gmmk3/provenance.json')|ConvertFrom-Json
 if($provenance.schema-ne1-or$provenance.qmkCommit-cne$manifest.qmkCommit){throw 'Invalid normalized manufacturer provenance'}
 $sourceCatalog=Join-Path $workspace 'reference/glorious-gmmk3/sources.json'
 if((Get-FileHash -LiteralPath $sourceCatalog).Hash-cne'50BB0ABF4FF6B627318F8DF0496C232DAFB29C1A1A5E01D92B6DA4F49CC1AD88'){throw 'Original manufacturer source catalog changed'}
 foreach($entry in @($provenance.originalSources)+@($provenance.outputs)){
  $file=[IO.Path]::GetFullPath((Join-Path $workspace $entry.path))
  if(!$file.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or!(Test-Path -LiteralPath $file)-or(Get-Item -LiteralPath $file).Length-ne$entry.bytes-or(Get-FileHash -LiteralPath $file).Hash-cne$entry.sha256){throw "Manufacturer provenance failed: $($entry.path)"}
 }
 $prefixes=@()
 if(@($targets|Where-Object{$_.boardId-ge10-and$_.boardId-le15}).Count){$prefixes+='keyboards/gmmk/inglorious3/'}
 if(@($targets|Where-Object boardId -eq 16).Count){$prefixes+='keyboards/gmmk/inglorious3pro/'}
 foreach($prefix in $prefixes){
 $existing=@(& git -C $qmk ls-tree -r --name-only HEAD -- $prefix)
 if($LASTEXITCODE-ne0-or$existing.Count){throw 'The private board prefix must not overwrite an upstream board'}
 foreach($entry in $provenance.outputs|Where-Object{$_.path.StartsWith('firmware/ports/gmmk3/',[StringComparison]::Ordinal)}){
  $relative=$entry.path.Substring('firmware/ports/gmmk3/'.Length)
  if($prefix.EndsWith('inglorious3pro/')-and$relative-cnotmatch'^p100/(?:config\.h|halconf\.h|mcuconf\.h|iso/.+)$'){continue}
  $source=Join-Path $workspace $entry.path
  $destination=Join-Path $qmk ($prefix+$relative)
  New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
  Copy-IfDifferent $source (Split-Path $destination -Parent)
 }
 }
}
& (Join-Path $PSScriptRoot 'Patch-QmkTiming.ps1') -Apply | Out-Null
& (Join-Path $PSScriptRoot 'Patch-QmkStorage.ps1') -Apply | Out-Null
$effects=Get-ChildItem -LiteralPath (Join-Path $qmk 'quantum/rgb_matrix/animations') -Filter '*.h' | ForEach-Object { foreach($match in [regex]::Matches([IO.File]::ReadAllText($_.FullName),'ENABLE_RGB_MATRIX_[A-Z_0-9]+')) { $match.Value } } | Sort-Object -Unique
foreach($target in $targets){
 $profileBankEnabled=[bool]$target.PSObject.Properties['profileBankLayout']
 $dir=Join-Path $PSScriptRoot ('keymaps/'+$target.name);New-Item -ItemType Directory -Path $dir -Force | Out-Null
 $board=Join-Path $qmk ('keyboards/'+$target.qmk)
 $jsonRelative='keyboards/'+$target.qmk+'/keyboard.json';$keymapRelative='keyboards/'+$target.qmk+'/keymaps/default/keymap.c'
 Assert-Pinned $jsonRelative;Assert-Pinned $keymapRelative
 $json=Get-Content -Raw -LiteralPath (Join-Path $qmk $jsonRelative) | ConvertFrom-Json
 $keys=@($json.layouts.($target.layout).layout)
 $hasEncoder=$json.features.PSObject.Properties['encoder'] -and $json.features.encoder
 if($hasEncoder -and (!$json.features.PSObject.Properties['mousekey'] -or !$json.features.mousekey)){throw "Encoder presets require the pinned mouse-key feature: $($target.name)"}
 if($json.processor-cne$target.mcu-or$json.matrix_pins.rows.Count-ne$target.rows-or$json.matrix_pins.cols.Count-ne$target.cols-or$keys.Count-ne$target.keys-or$json.rgb_matrix.layout.Count-ne$target.leds-or[bool]$hasEncoder-ne$target.encoder){throw "Manifest disagrees with QMK for $($target.name)"}
 $codes=Get-BaseArguments ([IO.File]::ReadAllText((Join-Path $qmk $keymapRelative))) $target.layout
 if($codes.Count-ne$keys.Count){throw "Base key count mismatch: $($target.name)"}
 $codes=@($codes|ForEach-Object{$_ -creplace '^QK_GESC$','KC_ESC' -creplace '^MO\(_FL\)$','MO(1)'})
 $keymap="// SPDX-License-Identifier: GPL-2.0-or-later`n// Base layout from pinned QMK, copyright Glorious LLC and QMK contributors.`n#include QMK_KEYBOARD_H`nconst uint16_t PROGMEM keymaps[4][MATRIX_ROWS][MATRIX_COLS] = {`n [0] = $($target.layout)(`n  "+($codes -join ', ')+")`n};`n"
 if($target.encoder){$keymap+="const uint16_t PROGMEM encoder_map[4][NUM_ENCODERS][NUM_DIRECTIONS] = { [0] = { ENCODER_CCW_CW(KC_VOLD, KC_VOLU) } };`n"}
 Write-IfDifferent (Join-Path $dir 'keymap.c') $keymap
 $physical=[int[]]::new($target.rows*$target.cols)
 foreach($key in $keys){$index=$key.matrix[0]*$target.cols+$key.matrix[1];if($key.matrix[0]-lt0-or$key.matrix[0]-ge$target.rows-or$key.matrix[1]-lt0-or$key.matrix[1]-ge$target.cols-or$physical[$index]){throw 'Invalid physical matrix position'};$physical[$index]=1}
 Write-IfDifferent (Join-Path $dir 'ig_physical.h') ("#pragma once`nstatic const uint8_t ig_physical[MATRIX_ROWS * MATRIX_COLS] = {"+($physical -join ',')+"};`n")
 $sequence=if($target.slider){@('KC_P1','KC_P2','KC_P3','KC_P4','KC_P5','KC_P6','KC_P7','KC_P8','KC_P9','KC_P0')}else{@('KC_I','KC_N','KC_G','KC_L','KC_O','KC_R','KC_I','KC_O','KC_U','KC_S')}
 $positions=@(foreach($code in $sequence){
  $pos=Find-Key $code $keys $codes
  $leds=@($json.rgb_matrix.layout|Where-Object{$_.PSObject.Properties['matrix'] -and $_.matrix[0]-eq$pos[0]-and$_.matrix[1]-eq$pos[1]})
  if($leds.Count-ne1){throw "Light-test key $code must address exactly one LED: $($target.name)"}
  '{'+($pos-join',')+'}'
 })
 $recover=if($target.slider){@('KC_PPLS','KC_PENT')}else{@('KC_SPC','KC_B')}
 $r1=Find-Key $recover[0] $keys $codes;$r2=Find-Key $recover[1] $keys $codes
 $boardHeader="// Generated from pinned QMK base keycodes, not remappable bindings.`n#pragma once`n#define IG_HAS_ENCODER $([int]$target.encoder)`n#define IG_LIGHT_TEST_PATTERN $(if($target.slider){2}else{1})`n#define IG_LIGHT_TEST_KEYS { "+($positions-join', ')+" }`n#define IG_RECOVERY_KEY1_ROW $($r1[0])`n#define IG_RECOVERY_KEY1_COL $($r1[1])`n#define IG_RECOVERY_KEY2_ROW $($r2[0])`n#define IG_RECOVERY_KEY2_COL $($r2[1])`n"
 if($target.encoder){$push=Find-Key $(if($target.slider){'KC_CALC'}else{'KC_MUTE'}) $keys $codes;$boardHeader+="#define IG_ENCODER_KEY_ROW $($push[0])`n#define IG_ENCODER_KEY_COL $($push[1])`n"}
 # Validate inherited external-flash CS against pinned sources, not LED-driver CS.
 $externalPins=@();$parent=$board
 while($parent.StartsWith((Join-Path $qmk 'keyboards/gmmk'),[StringComparison]::OrdinalIgnoreCase)){
  $file=Join-Path $parent 'config.h'
  if(Test-Path -LiteralPath $file){Assert-Pinned ([IO.Path]::GetRelativePath($qmk,$file).Replace('\','/'));foreach($m in [regex]::Matches([IO.File]::ReadAllText($file),'(?m)^#define EXTERNAL_FLASH_SPI_SLAVE_SELECT_PIN ([A-Z][0-9]+)\s*$')){$externalPins+=$m.Groups[1].Value}}
  $parent=Split-Path $parent -Parent
 }
 if($externalPins.Count-ne$(if($target.externalSpiCs){1}else{0})-or($target.externalSpiCs-and$externalPins[0]-cne$target.externalSpiCs)){throw "External flash pin mismatch: $($target.name)"}
 if($target.externalSpiCs){$boardHeader+="#define IG_EXTERNAL_SPI_CS $($target.externalSpiCs)`n"}
 $boardHeader+=Get-OsSwitchDefinition $target $json $keys $codes
 Write-IfDifferent (Join-Path $dir 'ig_board.h') $boardHeader
 $config=@'
#pragma once
#undef VENDOR_ID
#define VENDOR_ID 0xFEED
#undef PRODUCT_ID
#define PRODUCT_ID 0x__PID__
#undef MANUFACTURER
#define MANUFACTURER "INGLORIOUS"
#undef PRODUCT
#define PRODUCT "__PRODUCT__"
#define IG_BOARD __BOARD__
#include "ig_board.h"
#define RAW_USAGE_PAGE 0xFF60
#define RAW_USAGE_ID 0x0061
#define RAW_EPSIZE 32
#define IG_DYNAMIC_USB_INTERVAL 1
#undef WEAR_LEVELING_BACKING_SIZE
#define WEAR_LEVELING_BACKING_SIZE 32768
#undef WEAR_LEVELING_LOGICAL_SIZE
#define WEAR_LEVELING_LOGICAL_SIZE 16384
#define WEAR_LEVELING_EFL_FLASH_SIZE __CAPACITY__
#define RGB_MATRIX_DEFAULT_MODE RGB_MATRIX_SOLID_COLOR
#define RGB_MATRIX_DEFAULT_VAL 255
#define RGB_MATRIX_SLEEP
#define LAYER_STATE_8BIT
#define DYNAMIC_KEYMAP_LAYER_COUNT 4
'@
 $config=$config.Replace('__PID__',$target.runtimeProductId).Replace('__PRODUCT__',$target.product).Replace('__BOARD__',[string]$target.boardId).Replace('__CAPACITY__',[string]$target.flashBytes)
 if($Engineering){$config+="`n#define IG_ENGINEERING_BUILD 1`n"}
 if($target.boardId-eq16){$config+="`n#define IG_RETAINED_LOADER_BUILD 1`n"}
 if($profileBankEnabled){$config+="`n#define IG_PROFILE_BANK_ENABLE 1`n"}
 if($target.slider){$config+="`n#define IG_HAS_SLIDER 1`n"}
 foreach($effect in $effects){$config+="`n#undef $effect"}
 Write-IfDifferent (Join-Path $dir 'config.h') ($config+"`n")
 $rules=@'
RAW_ENABLE = yes
EEPROM_DRIVER = wear_leveling
WEAR_LEVELING_DRIVER = embedded_flash
FLASH_DRIVER = none
BOOTMAGIC_ENABLE = no
CONSOLE_ENABLE = no
COMMAND_ENABLE = no
LTO_ENABLE = no
SRC += $(KEYMAP_PATH)/inglorious.c
SRC += $(KEYMAP_PATH)/ig_timing.c
DEBOUNCE_TYPE = custom
CFLAGS += -fstack-usage
OPT_DEFS += -DIG_CACHE_STAGING
MCU_LDSCRIPT = IG___NAME__
'@
 $rules+="`nENCODER_MAP_ENABLE = $(if($target.encoder){'yes'}else{'no'})`n"
 if($target.boardId-eq16){$rules+="LDFLAGS += -Wl,--undefined=ig_fw_identity`n"}
 if($target.mcu.StartsWith('WB32',[StringComparison]::Ordinal)){$rules+="USE_CHIBIOS_CONTRIB = yes`nSRC += `$(KEYMAP_PATH)/wb32_bulk.c`n"}
 if($profileBankEnabled){$rules+="SRC += `$(KEYMAP_PATH)/ig_profile_bank.c`nSRC += `$(KEYMAP_PATH)/ig_profile_flash.c`n"}
 Write-IfDifferent (Join-Path $dir 'rules.mk') ($rules.Replace('__NAME__',$target.name)+"`n")
 $ldSource=if($target.mcu-ceq'STM32F303'){'lib/chibios/os/common/startup/ARMCMx/compilers/GCC/ld/STM32F303xC.ld'}elseif($target.boardId-eq16){'lib/chibios-contrib/os/common/startup/ARMCMx/compilers/GCC/ld/WB32FQ95xC.ld'}elseif($target.mcu-ceq'WB32FQ95'){'lib/chibios-contrib/os/common/startup/ARMCMx/compilers/GCC/ld/WB32FQ95xB.ld'}else{'lib/chibios-contrib/os/common/startup/ARMCMx/compilers/GCC/ld/WB32F3G71xB.ld'}
 $ld=[IO.File]::ReadAllText((Join-Path $qmk $ldSource))
 $flashRegex='(flash0 \(rx\) : org = 0x08000000, len = )\d+k'
 if([regex]::Matches($ld,$flashRegex).Count-ne1){throw 'Unexpected linker-script flash layout'}
 $ld=$ld -replace $flashRegex,('${1}'+(($target.flashBytes-$manifest.storageBytes)/1024)+'k')
 if($target.boardId-eq16){
  $codeBytes=$target.flashBytes-$manifest.storageBytes-$target.applicationOffsetBytes-$target.identityBytes
  if($profileBankEnabled){$codeBytes=0x2A000-$target.applicationOffsetBytes}
  $ld=$ld -replace 'flash0 \(rx\) : org = 0x08000000, len = \d+k',('flash0 (rx) : org = 0x{0:X8}, len = 0x{1:X}' -f (0x08000000+$target.applicationOffsetBytes),$codeBytes)
  if($ld-notmatch'ram0\s+\(wx\) : org = 0x20000000, len = 36k'){throw 'Engineering linker RAM layout changed'}
 }
 Write-IfDifferent (Join-Path $dir ('IG_'+$target.name+'.ld')) $ld
 $dest=Join-Path $board 'keymaps/inglorious';New-Item -ItemType Directory -Path $dest -Force | Out-Null
 foreach($file in @('keymap.c','ig_physical.h','ig_board.h','config.h','rules.mk')){Copy-IfDifferent (Join-Path $dir $file) $dest}
 if($target.boardId-eq16){
  foreach($generatedRoot in @($dir,$dest)){
   $obsolete=Join-Path $generatedRoot 'ig_engineering_version.h'
   if(Test-Path -LiteralPath $obsolete){Remove-Item -LiteralPath $obsolete}
  }
  Copy-IfDifferent (Join-Path $PSScriptRoot $target.versionHeaderPath) $dir
  Copy-IfDifferent (Join-Path $dir 'ig_board_version.h') $dest
 }
 foreach($file in @('inglorious.c','ig_effects.h','ig_color.h','ig_hardware_controls.h','ig_timing.h','ig_version.h','ig_timing.c','ig_storage.h','ig_storage.inc')){Copy-IfDifferent (Join-Path $PSScriptRoot ('inglorious/'+$file)) $dest}
 if($profileBankEnabled){foreach($file in @('ig_profile_bank.h','ig_profile_bank.c','ig_profile_flash.h','ig_profile_flash.c','ig_profile_runtime.inc','ig_profile_selector.h','ig_profile_defaults.h')){Copy-IfDifferent (Join-Path $PSScriptRoot ('inglorious/'+$file)) $dest}}
 if($target.mcu.StartsWith('WB32',[StringComparison]::Ordinal)){Copy-IfDifferent (Join-Path $PSScriptRoot 'inglorious/wb32_bulk.c') $dest}
 $lddir=Join-Path $board 'ld';New-Item -ItemType Directory -Path $lddir -Force | Out-Null
 Copy-IfDifferent (Join-Path $dir ('IG_'+$target.name+'.ld')) $lddir
 Write-Host ("Prepared "+$target.name+": board="+$target.boardId+", "+$target.rows+'x'+$target.cols+", LEDs="+$target.leds+", recovery="+($recover -join '+'))
}
