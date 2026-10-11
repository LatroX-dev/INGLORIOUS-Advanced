[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compilerDirectory=Join-Path $workspace 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler=Join-Path $compilerDirectory 'gcc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Existing QMK-MSYS GCC required; no automatic installation.'}
$artifacts=Join-Path $workspace 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$output=Join-Path $artifacts 'sequence-oracle.exe';$vectors=Join-Path $artifacts 'sequence-oracle.bin'
$priorPath=$env:PATH
try {
 $env:PATH=$compilerDirectory+';'+$priorPath
 & $compiler '-std=c11' '-Wall' '-Wextra' '-Werror' (Join-Path $PSScriptRoot 'sequence_oracle.c') '-o' $output
 if($LASTEXITCODE-ne0){throw 'Sequence oracle compile failed.'}
 & $output $vectors
 if($LASTEXITCODE-ne0){throw 'Sequence oracle failed.'}
 & dotnet run --project (Join-Path $PSScriptRoot 'InGlorious.ColorSequenceChecks.csproj') -c Release -- (Join-Path $artifacts 'protocol-fixtures') $vectors
 if($LASTEXITCODE-ne0){throw 'Sequence checks failed.'}
} finally {$env:PATH=$priorPath}
