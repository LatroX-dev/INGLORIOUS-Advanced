# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([string]$ReportDirectory = 'artifacts/profile-switch/selector-checks')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath((Join-Path $root $ReportDirectory))
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Selector check artifacts must stay inside the project.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$compilerDirectory = Join-Path $root 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler = Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Local QMK-MSYS native GCC is required. Nothing is downloaded.' }
$source = Join-Path $PSScriptRoot 'profile_selector_tests.c'
$module = Join-Path $root 'firmware/inglorious/ig_profile_selector.h'
$previousPath = $env:PATH
$results = @()
try {
    $env:PATH = $compilerDirectory + ';' + (Join-Path $root 'firmware/toolchain/qmk-msys/{app}/usr/bin') + ';' + $previousPath
    foreach ($board in @('undefined') + (0..17 | ForEach-Object { [string]$_ })) {
        $executable = Join-Path $output ('selector-board-' + $board + '.exe')
        $defines = @(if ($board -ne 'undefined') { '-DIG_BOARD=' + $board })
        & $compiler '-std=c11' '-O2' '-Wall' '-Wextra' '-Werror' '-pedantic' @defines $source '-o' $executable
        if ($LASTEXITCODE -ne 0) { throw ('Selector test compilation failed for board ' + $board) }
        $lines = @(& $executable)
        if ($LASTEXITCODE -ne 0) { throw ('Selector checks failed for board ' + $board) }
        $match = [regex]::Match(($lines -join "`n"), 'PASS selector support([01]): (\d+) pure native checks; no hardware access\.')
        $supported = [int]($board -eq '16')
        if (!$match.Success -or [int]$match.Groups[1].Value -ne $supported) { throw 'Unexpected selector support result.' }
        $lines | Write-Output
        $results += [ordered]@{board=$board;supported=[bool]$supported;checks=[int]$match.Groups[2].Value;executableSha256=(Get-FileHash -LiteralPath $executable).Hash}
    }
} finally { $env:PATH = $previousPath }
$report = [ordered]@{
    schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false
    runtimeActivated=$false;moduleSha256=(Get-FileHash -LiteralPath $module).Hash
    testSourceSha256=(Get-FileHash -LiteralPath $source).Hash
    runnerSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash
    scope='Pure selector decode/debounce. Exact board16 mapping only; no runtime, GPIO, storage or USB dependency.'
    results=$results
}
[IO.File]::WriteAllText((Join-Path $output 'results.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
