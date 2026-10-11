[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compilerDirectory=Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\mingw64\bin'
$compiler=Join-Path $compilerDirectory 'gcc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The extracted local native GCC is required; nothing will be installed.' }
$originalPath=$env:PATH
$results=@()
try {
    $env:PATH=$compilerDirectory+';'+(Join-Path $workspace 'firmware\toolchain\qmk-msys\{app}\usr\bin')+';'+$originalPath
    $output=Join-Path $workspace 'artifacts\firmware-bulk-tests.exe'
    $arguments=@('-std=c11','-Wall','-Wextra','-Werror',('-I'+(Join-Path $PSScriptRoot 'bulk_mocks')),(Join-Path $PSScriptRoot 'bulk_tests.c'),'-o',$output)
    foreach ($board in @(2,4,5,6,7,8,9,10,11,12,13,14,15)) {
        & $compiler @arguments ("-DIG_BOARD=$board")
        if ($LASTEXITCODE -ne 0) { throw "Bulk harness compilation failed for board $board." }
        $lines=@(& $output)
        if ($LASTEXITCODE -ne 0) { throw "Bulk harness failed for board $board." }
        $lines|Write-Output
        if(($lines -join '') -notmatch 'WB32 actual bulk source: ([0-9]+) offline assertions PASS;') { throw 'Missing bulk test count.' }
        $results+=@{board=$board;passed=[int]$Matches[1]}
    }
    $rejections=@('WB32_FLASH_PROGRAM_NO_ERASE','IG_BOARD=0','IG_BOARD=1','IG_BOARD=3','IG_BOARD=16','IG_BOARD=255','WEAR_LEVELING_EFL_FIRST_SECTOR=384','WEAR_LEVELING_EFL_OMIT_LAST_SECTOR_COUNT=1','WB32_FLASH_SECTOR_SIZE=128','BACKING_STORE_WRITE_SIZE=4','WEAR_LEVELING_EFL_FLASH_SIZE=262144','WEAR_LEVELING_BACKING_SIZE=16384')
    foreach ($definition in $rejections) {
        $diagnostic=& $compiler @arguments ('-D'+$definition) 2>&1
        if ($LASTEXITCODE -eq 0) { throw "Required compile guard did not reject $definition" }
        if (($diagnostic -join "`n") -notmatch '(error:|static assertion failed)') { throw "Missing expected compiler diagnostic for $definition" }
    }
    Write-Output "All $($rejections.Count) unsafe-build compile guards rejected. No hardware access."
} finally { $env:PATH=$originalPath }
if($ReportPath){
    $report=[ordered]@{schema=1;testedUtc=[DateTime]::UtcNow.ToString('o');hardwareAccess=$false;results=$results;rejectedUnsafeBuildCount=$rejections.Count;sourceSha256=(Get-FileHash (Join-Path $PSScriptRoot '../inglorious/wb32_bulk.c')).Hash;harnessSha256=(Get-FileHash (Join-Path $PSScriptRoot 'bulk_tests.c')).Hash}
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write([Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 5)));$stream.Flush($true)}finally{$stream.Dispose()}
}
