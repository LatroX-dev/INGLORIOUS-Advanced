[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compilerDirectory = Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\mingw64\bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The extracted local QMK-MSYS GCC is required. Nothing is installed by this runner.' }
$priorPath = $env:PATH
try {
    $env:PATH = $compilerDirectory + ';' + (Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\usr\bin') + ';' + $priorPath
    foreach ($board in @('numpad', 'pro')) {
        $output = Join-Path $workspace "artifacts\timing-checks-$board.exe"
        $arguments = @('-std=c11', '-Wall', '-Wextra', '-Werror', ('-I' + $PSScriptRoot), (Join-Path $PSScriptRoot 'timing_tests.c'), '-o', $output)
        if ($board -eq 'pro') { $arguments += '-DIG_TEST_PRO' }
        & $compiler @arguments
        if ($LASTEXITCODE -ne 0) { throw "Timing harness compilation failed: $board, exit $LASTEXITCODE" }
        & $output
        if ($LASTEXITCODE -ne 0) { throw "Timing harness failed: $board, exit $LASTEXITCODE" }
    }
} finally { $env:PATH = $priorPath }
