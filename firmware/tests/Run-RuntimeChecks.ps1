[CmdletBinding()]
param([string]$ReportPath, [string]$KnobFixtureDirectory, [string]$KnobOrderFixtureDirectory, [string]$KnobPressFixtureDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$knobFixtures = @()
$knobOrderFixtures=@()
$knobPressFixtures=@()
if($KnobPressFixtureDirectory){
    if(!$KnobOrderFixtureDirectory){throw 'Press fixtures require the preceding schema5 and schema6 fixtures.'}
    $KnobPressFixtureDirectory=[IO.Path]::GetFullPath($KnobPressFixtureDirectory)
    if(!$KnobPressFixtureDirectory.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Press fixtures must stay inside the project.'}
    $knobPressFixtures=@(Get-ChildItem -LiteralPath $KnobPressFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
if($KnobOrderFixtureDirectory){
    if(!$KnobFixtureDirectory){throw 'Ordered knob checks also require the schema5 knob fixtures.'}
    $KnobOrderFixtureDirectory=[IO.Path]::GetFullPath($KnobOrderFixtureDirectory)
    if(!$KnobOrderFixtureDirectory.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Ordered knob fixtures must stay inside the project.'}
    $knobOrderFixtures=@(Get-ChildItem -LiteralPath $KnobOrderFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
if($KnobFixtureDirectory){
    $KnobFixtureDirectory=[IO.Path]::GetFullPath($KnobFixtureDirectory)
    if(!$KnobFixtureDirectory.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Knob fixtures must stay inside the project.'}
    $knobFixtures=@(Get-ChildItem -LiteralPath $KnobFixtureDirectory -File | Where-Object{$_.Extension-eq'.bin'-or$_.Name-eq'manifest.json'} | Sort-Object Name | ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$compilerDirectory = Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\mingw64\bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The extracted local QMK-MSYS GCC is required. This script does not install anything.' }
$originalPath = $env:PATH
$results = @()
try {
    $env:PATH = $compilerDirectory + ';' + (Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\usr\bin') + ';' + $originalPath
    foreach ($board in @('numpad', 'pro')) {
        $output = Join-Path $workspace "artifacts\firmware-runtime-tests-$board.exe"
        $arguments = @('-std=c11', '-Wall', '-Wextra', '-Werror', ('-I' + $PSScriptRoot), (Join-Path $PSScriptRoot 'runtime_tests.c'), '-lm', '-o', $output)
        if ($board -eq 'pro') { $arguments += '-DIG_TEST_PRO' }
        & $compiler @arguments
        if ($LASTEXITCODE -ne 0) { throw "Offline harness compilation failed with exit $LASTEXITCODE for $board." }
        $fixtureArgs=@((Join-Path $workspace 'artifacts\protocol-fixtures'),(Join-Path $workspace 'artifacts\onboard-fixtures'))
        if($KnobFixtureDirectory){$fixtureArgs += [IO.Path]::GetFullPath($KnobFixtureDirectory)}
        if($KnobOrderFixtureDirectory){$fixtureArgs += $KnobOrderFixtureDirectory}
        if($KnobPressFixtureDirectory){$fixtureArgs += $KnobPressFixtureDirectory}
        $lines = @(& $output @fixtureArgs)
        if ($LASTEXITCODE -ne 0) { throw "Offline harness checks failed with exit $LASTEXITCODE for $board." }
        $lines | Write-Output
        $summary = @($lines | Where-Object { $_ -match '^PASS: board([12]), ([0-9]+) firmware runtime checks;' })
        if ($summary.Count -ne 1 -or $summary[0] -notmatch '^PASS: board([12]), ([0-9]+) firmware runtime checks;' -or [int]$Matches[1] -ne $(if($board -eq 'pro'){1}else{2})) { throw 'Native runtime did not report the expected board result.' }
        $results += [ordered]@{board=$board;passed=[int]$Matches[2]}
    }
} finally { $env:PATH = $originalPath }
if($ReportPath){
    $report=[ordered]@{schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false;results=$results;
        sourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/inglorious.c')).Hash;
        versionHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_version.h')).Hash;
        harnessSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'runtime_tests.c')).Hash;
        versionTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'version_reporting_checks.h')).Hash;
        hardwareControlsHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_hardware_controls.h')).Hash;
        colorOutputHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_color.h')).Hash;
        colorOutputTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'color_output_checks.h')).Hash;
        brightnessBindingTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'brightness_binding_checks.h')).Hash;
        knobCycleTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'knob_cycle_checks.h')).Hash;
        knobFixtures=$knobFixtures;
        knobOrderFixtures=$knobOrderFixtures;
        knobPressFixtures=$knobPressFixtures;
        cacheStagingTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'cache_staging_checks.h')).Hash;
        storageHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_storage.h')).Hash;
        storageImplementationSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_storage.inc')).Hash;
        scope='Actual C runtime, read-only version reporting and all existing configuration, storage-failure, macro, recovery, timing, effects, standby and lighting-test regressions.'}
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write([Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 5)));$stream.Flush($true)}finally{$stream.Dispose()}
}
