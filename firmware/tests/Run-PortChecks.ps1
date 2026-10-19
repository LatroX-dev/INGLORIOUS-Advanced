#Requires -Version 7.0
[CmdletBinding()]
param([string]$ReportPath, [switch]$Engineering, [Alias('Board')][string]$TargetBoard='all', [string]$FixtureDirectory, [string]$KnobFixtureDirectory, [string]$KnobOrderFixtureDirectory, [string]$KnobPressFixtureDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$knobFixtures=@()
$knobOrderFixtures=@()
$knobPressFixtures=@()
if($KnobPressFixtureDirectory){
    if(!$KnobOrderFixtureDirectory){throw 'Press fixtures require the preceding schema5 and schema6 fixtures.'}
    $KnobPressFixtureDirectory=[IO.Path]::GetFullPath($KnobPressFixtureDirectory)
    if(!$KnobPressFixtureDirectory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Press fixtures must stay inside the project.'}
    $knobPressFixtures=@(Get-ChildItem -LiteralPath $KnobPressFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
if($KnobOrderFixtureDirectory){
    if(!$KnobFixtureDirectory){throw 'Ordered knob checks also require the schema5 knob fixtures.'}
    $KnobOrderFixtureDirectory=[IO.Path]::GetFullPath($KnobOrderFixtureDirectory)
    if(!$KnobOrderFixtureDirectory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Ordered knob fixtures must stay inside the project.'}
    $knobOrderFixtures=@(Get-ChildItem -LiteralPath $KnobOrderFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
if($KnobFixtureDirectory){
    $KnobFixtureDirectory=[IO.Path]::GetFullPath($KnobFixtureDirectory)
    if(!$KnobFixtureDirectory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Knob fixtures must stay inside the project.'}
    $knobFixtures=@(Get-ChildItem -LiteralPath $KnobFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$compilerDirectory = Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Local QMK-MSYS native GCC is required. Nothing is downloaded.' }
$selection=& (Join-Path $PSScriptRoot '../Resolve-Targets.ps1') -Board $TargetBoard -Engineering:$Engineering
$targets=$selection.Manifest
if(@($selection.Targets|Where-Object boardId -eq 16).Count-and!$FixtureDirectory){throw 'Board 16 runtime checks require explicit offline fixtures.'}
$references = Get-Content -LiteralPath (Join-Path $root 'reference/qmk/sources.json') -Raw | ConvertFrom-Json
$utf8 = [Text.UTF8Encoding]::new($false)
$priorPath = $env:PATH
$results = @()
try {
    $env:PATH = $compilerDirectory + ';' + (Join-Path $root 'firmware/toolchain/qmk-msys/{app}/usr/bin') + ';' + $priorPath
    foreach ($board in @($selection.Targets | Where-Object boardId -gt 2)) {
        $referenceId=if($board.boardId-eq16){$board.sourceDerivative}else{$board.deviceId}
        $fixtureRoot = if($board.boardId-eq16){[IO.Path]::GetFullPath($FixtureDirectory)}elseif($board.boardId -ge 10) { Join-Path $root 'artifacts/gmmk3-port-fixtures' } else { Join-Path $root 'artifacts/port-fixtures' }
        if(!$fixtureRoot.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixtures must stay inside the project.'}
        $fixtures = Get-Content -LiteralPath (Join-Path $fixtureRoot 'manifest.json') -Raw | ConvertFrom-Json
        $generated = if($board.boardId-eq16){Join-Path (Split-Path $fixtureRoot -Parent) 'port-checks'}else{Join-Path $root ('artifacts/firmware-port-checks/' + $board.name)}
        [IO.Directory]::CreateDirectory($generated) | Out-Null
        foreach ($name in @(($referenceId + '.json'), ($referenceId + '-default.c'))) {
            $source = @($references | Where-Object file -eq $name)
            if ($source.Count -ne 1 -or $source[0].qmkCommit -ne $targets.qmkCommit -or (Get-FileHash -LiteralPath (Join-Path $root ('reference/qmk/' + $name))).Hash -ne $source[0].sha256) { throw ('Unverified reference: '+$name) }
        }
        $geometry = Get-Content -LiteralPath (Join-Path $root ('reference/qmk/' + $referenceId + '.json')) -Raw | ConvertFrom-Json
        $layout = @($geometry.layouts.($board.layout).layout)
        $leds = @($geometry.rgb_matrix.layout)
        if ($layout.Count -ne $board.keys -or $leds.Count -ne $board.leds) { throw 'Reference counts disagree with target manifest.' }
        $baseSource = [IO.File]::ReadAllText((Join-Path $root ('reference/qmk/' + $referenceId + '-default.c')))
        $baseSource = [regex]::Replace($baseSource, '(?s)/\*.*?\*/|//[^\r\n]*', '')
        $start = [regex]::Match($baseSource, '\bLAYOUT\w*\s*\(')
        if (-not $start.Success) { throw 'No independent default layout found.' }
        $at = $start.Index + $start.Length; $depth = 1; $end = $at
        while ($end -lt $baseSource.Length -and $depth -gt 0) {
            if ($baseSource[$end] -eq '(') { $depth++ }; if ($baseSource[$end] -eq ')') { $depth-- }; $end++
        }
        $tokens = @($baseSource.Substring($at, $end-$at-1).Split(',') | ForEach-Object Trim)
        if ($tokens.Count -ne $layout.Count) { throw 'Independent base-key count disagrees with physical layout.' }
        $matrix = [byte[]]::new($board.rows*$board.cols); [Array]::Fill[byte]($matrix,255)
        $physical = [byte[]]::new($matrix.Length)
        foreach ($key in $layout) { $physical[$key.matrix[0]*$board.cols+$key.matrix[1]] = 1 }
        for ($i=0; $i -lt $leds.Count; $i++) {
            if ($leds[$i].PSObject.Properties.Name -contains 'matrix') {
                $index=$leds[$i].matrix[0]*$board.cols+$leds[$i].matrix[1]
                if($matrix[$index] -ne 255){throw 'Duplicate LED matrix position.'}; $matrix[$index]=$i
            }
        }
        $matrixRows = for($row=0;$row -lt $board.rows;$row++) { '{'+($matrix[($row*$board.cols)..(($row+1)*$board.cols-1)] -join ',')+'}' }
        $points = @($leds | ForEach-Object { '{'+[int]$_.x+','+[int]$_.y+'}' })
        $keyPositions = @{}
        for($i=0;$i -lt $tokens.Count;$i++) { if (-not $keyPositions.ContainsKey($tokens[$i])) { $keyPositions[$tokens[$i]] = '{'+($layout[$i].matrix -join ',')+'}' } }
        $letters = @('KC_I','KC_N','KC_G','KC_L','KC_O','KC_R','KC_I','KC_O','KC_U','KC_S') | ForEach-Object { if(-not$keyPositions.ContainsKey($_)){throw 'Missing letter key.'}; $keyPositions[$_] }
        $recovery = @('KC_SPC','KC_B') | ForEach-Object { if(-not$keyPositions.ContainsKey($_)){throw 'Missing recovery key.'}; $keyPositions[$_] }
        $referenceText = "static const led_config_t reference_leds = { { $($matrixRows -join ',') }, { $($points -join ',') }, { $($leds.flags -join ',') } };`nstatic const uint8_t reference_physical[] = { $($physical -join ',') };`nstatic const uint8_t reference_letters[10][2] = { $($letters -join ',') };`nstatic const uint8_t reference_recovery[2][2] = { $($recovery -join ',') };`n"
        [IO.File]::WriteAllText((Join-Path $generated 'port_reference.h'),$referenceText,$utf8)
        [IO.File]::WriteAllText((Join-Path $generated 'port_model.h'),"#pragma once`n#define IG_BOARD $($board.boardId)`n#define MATRIX_ROWS $($board.rows)`n#define MATRIX_COLS $($board.cols)`n#define RGB_MATRIX_LED_COUNT $($board.leds)`n",$utf8)
        $executable = Join-Path $generated 'port-checks.exe'
        $gate=@(if($board.boardId-eq16){'-DIG_RETAINED_LOADER_BUILD=1'})
        & $compiler @gate '-std=c11' '-Wall' '-Wextra' '-Werror' ('-I'+(Join-Path $PSScriptRoot 'ports')) ('-I'+$generated) ('-I'+(Join-Path $root ('firmware/keymaps/'+$board.name))) (Join-Path $PSScriptRoot 'ports/port_checks.c') '-lm' '-o' $executable
        if ($LASTEXITCODE -ne 0) { throw ('Port harness compilation failed: '+$board.name) }
        $paths = @('default','configured','v4-configured') | ForEach-Object { Join-Path $fixtureRoot ($board.deviceId+'-'+$_+'.bin') }
        foreach($path in $paths) {
            $expected = @($fixtures | Where-Object file -eq ([IO.Path]::GetFileName($path)))
            if($expected.Count -ne 1 -or $expected[0].boardId -ne $board.boardId -or (Get-Item -LiteralPath $path).Length -ne $expected[0].bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $expected[0].sha256) { throw 'Serializer fixture identity/hash mismatch.' }
        }
        $fixtureArgs=@($paths)
        if($KnobFixtureDirectory){$fixtureArgs += [IO.Path]::GetFullPath($KnobFixtureDirectory)}
        if($KnobOrderFixtureDirectory){$fixtureArgs += $KnobOrderFixtureDirectory}
        if($KnobPressFixtureDirectory){$fixtureArgs += $KnobPressFixtureDirectory}
        $lines = @(& $executable @fixtureArgs)
        if ($LASTEXITCODE -ne 0) { throw ('Native port checks failed: '+$board.name) }
        $lines | Write-Output
        $match = [regex]::Match(($lines -join "`n"), 'PASS board(\d+): (\d+) native port checks; no hardware access\.')
        if (-not $match.Success -or [int]$match.Groups[1].Value -ne $board.boardId) { throw 'Missing expected board result.' }
        $results += [ordered]@{ deviceId=$board.deviceId; boardId=$board.boardId; checks=[int]$match.Groups[2].Value; fixtures=@($paths | ForEach-Object { @{ file=[IO.Path]::GetFileName($_); sha256=(Get-FileHash -LiteralPath $_).Hash } }); referenceSha256=(Get-FileHash -LiteralPath (Join-Path $generated 'port_reference.h')).Hash }
    }
} finally { $env:PATH=$priorPath }
if($ReportPath) {
    $report = [ordered]@{schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false;results=$results;sourceSha256=(Get-FileHash -LiteralPath (Join-Path $root 'firmware/inglorious/inglorious.c')).Hash;harnessSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ports/port_checks.c')).Hash;scope='Actual common C runtime across additional models with real desktop payloads and pinned physical geometry. For GMMK3, the separate reference audit checks derivation from preserved manufacturer sources. Storage fault injection uses a mock; the separate storage harness tests the patched real wear-leveling driver.'}
    $report.knobCycleTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'knob_cycle_checks.h')).Hash
    $report.hardwareControlsHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_hardware_controls.h')).Hash
    $report.hardwareControlsTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'hardware_controls_checks.h')).Hash
    $report.knobFixtures=$knobFixtures
    $report.knobOrderFixtures=$knobOrderFixtures
    $report.knobPressFixtures=$knobPressFixtures
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write($utf8.GetBytes(($report|ConvertTo-Json -Depth 8)));$stream.Flush($true)}finally{$stream.Dispose()}
}
