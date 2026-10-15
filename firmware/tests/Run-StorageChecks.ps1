[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compilerDir=Join-Path $workspace 'firmware/toolchain/qmk-msys/{app}/mingw64/bin'
$compiler=Join-Path $compilerDir 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Existing local native GCC required; no installation is performed.' }
$patch=& (Join-Path $PSScriptRoot '../Patch-QmkStorage.ps1')
$actual=(Get-FileHash -LiteralPath $patch.source).Hash
if ($actual -ne $patch.patchedSha256) { throw 'Apply the audited storage patch before running its actual-source harness.' }
$oldPath=$env:PATH
$results=@()
try {
    $env:PATH=$compilerDir+';'+$oldPath
    foreach($board in @(2,10,16)) { foreach($width in @(2,8)) {
        $slotBytes=if($board -ge 10){7680}else{7168}
        $output=Join-Path $workspace "artifacts/firmware-storage-tests-$board-$width.exe"
        $arguments=@('-std=c11','-Wall','-Wextra','-Werror','-DIG_CACHE_STAGING',"-DIG_BOARD=$board",'-DWEAR_LEVELING_LOGICAL_SIZE=16384','-DWEAR_LEVELING_BACKING_SIZE=32768',"-DBACKING_STORE_WRITE_SIZE=$width",'-I'+(Join-Path $PSScriptRoot '../inglorious'),'-I'+(Join-Path $PSScriptRoot '../qmk_firmware/quantum'),'-I'+(Join-Path $PSScriptRoot '../qmk_firmware/quantum/wear_leveling'),'-I'+(Join-Path $PSScriptRoot '../qmk_firmware/lib/fnv'),(Join-Path $PSScriptRoot 'storage_tests.c'),(Join-Path $PSScriptRoot '../qmk_firmware/lib/fnv/hash_64a.c'),'-o',$output)
        $gate=@(if($board-eq16){'-DIG_RETAINED_LOADER_BUILD=1'})
        & $compiler @arguments @gate
        if($LASTEXITCODE -ne 0){ throw "Storage harness width$width compilation failed." }
        $lines=@(& $output)
        if($LASTEXITCODE -ne 0){ throw "Storage harness width$width failed." }
        $lines|Write-Output
        if(($lines -join '') -notmatch "^PASS: board$board width$width slot$slotBytes, ([0-9]+) actual QMK storage checks;"){throw 'Missing harness result.'}
        $results+=@{board=$board;width=$width;slotBytes=$slotBytes;passed=[int]$Matches[1]}
    } }
    # Compile the actual storage implementation for every enumerated board, then
    # prove that undefined/future IDs cannot silently acquire a slot geometry.
    $geometryArguments=@($arguments | Where-Object { $_ -notlike '-DIG_BOARD=*' })
    foreach($board in 1..16) {
        $gate=@(if($board-eq16){'-DIG_RETAINED_LOADER_BUILD=1'})
        & $compiler @geometryArguments "-DIG_BOARD=$board" @gate '-fsyntax-only'
        if($LASTEXITCODE -ne 0){throw "Storage geometry compilation failed for board$board."}
    }
    foreach($definition in @('-UIG_BOARD','-DIG_BOARD=0','-DIG_BOARD=17','-DIG_BOARD=255')) {
        $diagnostic=& $compiler @geometryArguments $definition '-fsyntax-only' 2>&1
        if($LASTEXITCODE -eq 0 -or ($diagnostic -join "`n") -notmatch 'Storage geometry requires an explicitly supported') { throw "Storage geometry guard did not reject $definition." }
    }
    foreach($gate in @('-UIG_RETAINED_LOADER_BUILD','-DIG_RETAINED_LOADER_BUILD=0')){
        $diagnostic=& $compiler @geometryArguments '-DIG_BOARD=16' $gate '-fsyntax-only' 2>&1
        if($LASTEXITCODE-eq0-or($diagnostic-join"`n")-notmatch'Board 16 requires its retained-loader build'){throw 'Storage retained-loader gate failed'}
    }
    Write-Output 'All 15 default board geometries and retained-loader board 16 compile; four unknown-board and two retained-loader guards rejected. No hardware access.'
}finally{$env:PATH=$oldPath}
if($ReportPath){
    $report=[ordered]@{schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false;results=$results;compiledBoardIds=@(1..16);rejectedUnknownBoardCount=4;rejectedRetainedLoaderGateCount=2;patchedQmkSha256=$actual;patchSha256=(Get-FileHash (Join-Path $PSScriptRoot '../Patch-QmkStorage.ps1')).Hash;storageHeaderSha256=(Get-FileHash (Join-Path $PSScriptRoot '../inglorious/ig_storage.h')).Hash;storageImplementationSha256=(Get-FileHash (Join-Path $PSScriptRoot '../inglorious/ig_storage.inc')).Hash;harnessSha256=(Get-FileHash (Join-Path $PSScriptRoot 'storage_tests.c')).Hash}
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write([Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 5)));$stream.Flush($true)}finally{$stream.Dispose()}
}
