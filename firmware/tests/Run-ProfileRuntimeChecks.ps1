# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([string]$FixtureDirectory='artifacts/hardware-controls/fixtures', [string]$ReportDirectory='artifacts/profile-switch/runtime-checks')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtures=[IO.Path]::GetFullPath((Join-Path $root $FixtureDirectory))
$output=[IO.Path]::GetFullPath((Join-Path $root $ReportDirectory))
foreach($path in @($fixtures,$output)){if(!$path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Check inputs and outputs must stay inside the project.'}}
[IO.Directory]::CreateDirectory($output)|Out-Null
$fixture=Join-Path $fixtures 'gmmk3-pro-100-iso-default.bin'
$catalog=Get-Content -LiteralPath (Join-Path $fixtures 'manifest.json') -Raw|ConvertFrom-Json
$expected=@($catalog|Where-Object file -eq ([IO.Path]::GetFileName($fixture)))
if($expected.Count-ne1 -or (Get-Item -LiteralPath $fixture).Length-ne$expected[0].bytes -or (Get-FileHash -LiteralPath $fixture).Hash-cne$expected[0].sha256){throw 'Fixture validation failed.'}
$compilerDirectory=Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler=Join-Path $compilerDirectory 'gcc.exe'
$previousPath=$env:PATH
try {
 $env:PATH=$compilerDirectory+';'+(Join-Path $root 'firmware/toolchain/qmk-msys/{app}/usr/bin')+';'+$previousPath
 $exe=Join-Path $output 'profile-runtime.exe'
 $arguments=@('-std=c11','-O2','-Wall','-Wextra','-Werror','-DIG_PROFILE_BANK_ENABLE=1','-DIG_RETAINED_LOADER_BUILD=1',
  '-I'+(Join-Path $PSScriptRoot 'ports'),'-I'+(Join-Path (Split-Path $fixtures -Parent) 'port-checks'),'-I'+(Join-Path $root 'firmware/keymaps/gmmk3-pro-100-iso'),
  (Join-Path $PSScriptRoot 'profile_runtime_tests.c'),(Join-Path $root 'firmware/inglorious/ig_profile_bank.c'),'-lm','-o',$exe)
 & $compiler @arguments
 if($LASTEXITCODE-ne0){throw 'Profile runtime compile failed.'}
 $lines=@(& $exe $fixture)
 if($LASTEXITCODE-ne0){throw 'Profile runtime checks failed.'}
 $lines|Write-Output
 $match=[regex]::Match(($lines-join"`n"),'PASS profile runtime: (\d+) checks; no hardware access; (\d+) explicit mock page operations\.')
 if(!$match.Success){throw 'No runtime result found.'}
 $sources=@('firmware/inglorious/inglorious.c','firmware/inglorious/ig_profile_runtime.inc','firmware/inglorious/ig_profile_defaults.h','firmware/inglorious/ig_profile_bank.h','firmware/inglorious/ig_profile_bank.c','firmware/inglorious/ig_profile_selector.h','firmware/inglorious/ig_profile_flash.h','firmware/tests/profile_runtime_tests.c','firmware/tests/ports/port_checks.c','firmware/tests/Run-ProfileRuntimeChecks.ps1')
 $report=[ordered]@{schema=1;hardwareAccess=$false;checks=[int]$match.Groups[1].Value;mockPageOperations=[int]$match.Groups[2].Value;fixtureSha256=$expected[0].sha256;sources=@($sources|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}})}
 [IO.File]::WriteAllText((Join-Path $output 'results.json'),($report|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllLines((Join-Path $output 'native-checks.log'),$lines,[Text.UTF8Encoding]::new($false))
} finally {$env:PATH=$previousPath}
