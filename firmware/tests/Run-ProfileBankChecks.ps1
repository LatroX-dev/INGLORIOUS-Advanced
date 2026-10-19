# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([string]$ReportDirectory = 'artifacts/profile-switch/bank-checks')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath((Join-Path $root $ReportDirectory))
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Profile bank check artifacts must stay inside the project.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$compilerDirectory = Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Local QMK-MSYS native GCC is required. Nothing is downloaded.' }
$source = Join-Path $PSScriptRoot 'profile_bank_tests.c'
$module = Join-Path $root 'firmware/inglorious/ig_profile_bank.c'
$header = Join-Path $root 'firmware/inglorious/ig_profile_bank.h'
$executable = Join-Path $output 'profile-bank-checks.exe'
$previousPath = $env:PATH
try {
    $env:PATH = $compilerDirectory + ';' + $previousPath
    & $compiler '-std=c11' '-O2' '-Wall' '-Wextra' '-Wconversion' '-Wshadow' '-Werror' '-pedantic' $module $source '-o' $executable
    if ($LASTEXITCODE -ne 0) { throw 'Profile bank checks did not compile.' }
    $lines = @(& $executable)
    if ($LASTEXITCODE -ne 0) { throw 'Profile bank fault-injection checks failed.' }
    $result = ($lines -join "`n") | ConvertFrom-Json
    if (!$result.passed -or $result.hardwareAccess) { throw 'Unexpected profile bank check result.' }
    $lines | Write-Output
    $report = [ordered]@{
        schema = 1; testedUtc = [DateTime]::UtcNow.ToString('o'); hardwareAccess = $false
        moduleSha256 = (Get-FileHash -LiteralPath $module).Hash
        headerSha256 = (Get-FileHash -LiteralPath $header).Hash
        testSourceSha256 = (Get-FileHash -LiteralPath $source).Hash
        runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
        executableSha256 = (Get-FileHash -LiteralPath $executable).Hash
        scope = 'Pure 48-KiB fixed-pair codec; simulated WB32 page phases. No physical integration or hardware acceptance.'
        result = $result
    }
    [IO.File]::WriteAllText((Join-Path $output 'results.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
} finally { $env:PATH = $previousPath }
