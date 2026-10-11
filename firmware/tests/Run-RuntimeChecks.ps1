[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
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
        $lines = @(& $output (Join-Path $workspace 'artifacts\protocol-fixtures') (Join-Path $workspace 'artifacts\onboard-fixtures'))
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
        colorOutputHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_color.h')).Hash;
        colorOutputTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'color_output_checks.h')).Hash;
        cacheStagingTestsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'cache_staging_checks.h')).Hash;
        storageHeaderSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_storage.h')).Hash;
        storageImplementationSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../inglorious/ig_storage.inc')).Hash;
        scope='Actual C runtime, read-only version reporting and all existing configuration, storage-failure, macro, recovery, timing, effects, standby and lighting-test regressions.'}
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write([Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 5)));$stream.Flush($true)}finally{$stream.Dispose()}
}
