[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compilerDirectory=Join-Path $workspace 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler=Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The extracted local GCC is required. Nothing is installed or downloaded.' }
$oldPath=$env:PATH
try {
 $env:PATH=$compilerDirectory+';'+$oldPath
 $output=Join-Path $workspace 'artifacts/firmware-effects-tests.exe'
 & $compiler -std=c11 -O2 -Wall -Wextra -Werror (Join-Path $PSScriptRoot 'effects_tests.c') -o $output
 if ($LASTEXITCODE -ne 0) { throw 'Effect renderer harness compilation failed.' }
 & $output
 if ($LASTEXITCODE -ne 0) { throw 'Effect renderer checks failed.' }
} finally { $env:PATH=$oldPath }
