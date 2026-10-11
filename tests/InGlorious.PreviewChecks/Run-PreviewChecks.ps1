[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compilerDirectory=Join-Path $workspace 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler=Join-Path $compilerDirectory 'gcc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'The existing local QMK-MSYS GCC is required. Nothing is installed.'}
$null=New-Item -ItemType Directory -Force -Path (Join-Path $workspace 'artifacts')
$output=Join-Path $workspace 'artifacts/preview-oracle.exe'
$vectors=Join-Path $workspace 'artifacts/preview-oracle.bin'
$priorPath=$env:PATH
try {
    $env:PATH=$compilerDirectory+';'+$priorPath
    & $compiler '-std=c11' '-Wall' '-Wextra' '-Werror' (Join-Path $PSScriptRoot 'effect_oracle.c') '-o' $output
    if($LASTEXITCODE-ne0){throw 'Effect oracle compilation failed.'}
    & $output $vectors
    if($LASTEXITCODE-ne0){throw 'Effect oracle generation failed.'}
    & dotnet run --project (Join-Path $PSScriptRoot 'InGlorious.PreviewChecks.csproj') -c Release -- $vectors
    if($LASTEXITCODE-ne0){throw 'Preview comparison failed.'}
} finally {$env:PATH=$priorPath}
