# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([string]$ReportDirectory = 'artifacts/profile-switch/flash-adapter-checks')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath((Join-Path $root $ReportDirectory))
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Profile flash checks must stay inside the project.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$compilerDirectory = Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Local QMK-MSYS native GCC is required. Nothing is downloaded.' }
$module = Join-Path $root 'firmware/inglorious/ig_profile_flash.c'
$header = Join-Path $root 'firmware/inglorious/ig_profile_flash.h'
$source = Join-Path $PSScriptRoot 'profile_flash_tests.c'
$mocks = Join-Path $PSScriptRoot 'profile_flash_mocks'
$executable = Join-Path $output 'profile-flash-checks.exe'
$previousPath = $env:PATH
try {
    $env:PATH = $compilerDirectory + ';' + $previousPath
    & $compiler '-std=c11' '-O2' '-Wall' '-Wextra' '-Wconversion' '-Wshadow' '-Werror' '-pedantic' '-DIG_PROFILE_BANK_ENABLE=1' '-DIG_BOARD=16' '-DIG_RETAINED_LOADER_BUILD=1' '-DQMK_KEYBOARD_H="profile_flash_qmk_mock.h"' ('-I' + $mocks) $module $source '-o' $executable
    if ($LASTEXITCODE -ne 0) { throw 'Profile flash checks did not compile.' }
    $lines = @(& $executable)
    if ($LASTEXITCODE -ne 0) { throw 'Profile flash checks failed.' }
    $result = ($lines -join "`n") | ConvertFrom-Json
    if (!$result.passed -or $result.hardwareAccess -or !$result.actualAdapter) { throw 'Unexpected flash check result.' }
    $lines | Write-Output
    $report = [ordered]@{
        schema=1; testedUtc=[DateTime]::UtcNow.ToString('o'); hardwareAccess=$false
        moduleSha256=(Get-FileHash -LiteralPath $module).Hash; headerSha256=(Get-FileHash -LiteralPath $header).Hash
        testSourceSha256=(Get-FileHash -LiteralPath $source).Hash; runnerSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash
        mockHalSha256=(Get-FileHash -LiteralPath (Join-Path $mocks 'hal.h')).Hash
        mockQmkSha256=(Get-FileHash -LiteralPath (Join-Path $mocks 'profile_flash_qmk_mock.h')).Hash
        executableSha256=(Get-FileHash -LiteralPath $executable).Hash
        scope='Actual EFL adapter against native HAL mocks and process-local VirtualAlloc RAM; no physical flash or device access.'
        result=$result
    }
    [IO.File]::WriteAllText((Join-Path $output 'results.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
} finally { $env:PATH = $previousPath }
