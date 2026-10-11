#requires -Version 7.2
<#
.SYNOPSIS
Checks fifteen editor references without opening a device or changing files.
.DESCRIPTION
Always checks manifest hashes, geometry, matrix/LED correspondence and default
keymap argument counts. If the local QMK Git repository is present, additionally
compares the original eighteen references with their immutable pinned Git blobs.
Six GMMK3 models are independently checked against 42 pinned manufacturer files,
including the reviewed matrix corrections and retained electrical RGB tables.
No network requests, compiler, firmware utility, or Git write command is used.
#>
[CmdletBinding()]
param(
    [string]$ReferenceRoot = (Join-Path $PSScriptRoot '../reference/qmk'),
    [string]$QmkRoot = (Join-Path $PSScriptRoot '../firmware/qmk_firmware'),
    [string]$ManufacturerRoot,
    [string]$PortRoot = (Join-Path $PSScriptRoot '../firmware/ports/gmmk3'),
    [string]$Python = 'python',
    [switch]$RequireCheckout
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$commit = '7a1bbf37c5c07da4ea0a162bb139083a46ef40a1'
$script:checks = 0
function Assert-Reference([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "QMK reference check failed: $Message" }
    $script:checks++
}
function Test-Integer($Value, [int]$Minimum, [int]$Maximum) {
    return $null -ne $Value -and $Value -is [ValueType] -and
        [double]$Value -eq [Math]::Truncate([double]$Value) -and
        [double]$Value -ge $Minimum -and [double]$Value -le $Maximum
}
function Get-LayoutArguments([string]$Source, [string]$Layout) {
    # Count C macro arguments while preserving nested calls such as MO(1).
    $clean = [regex]::Replace($Source, '(?s)/\*.*?\*/|(?m)//[^\r\n]*', '')
    $calls = [regex]::Matches($clean, '\b' + [regex]::Escape($Layout) + '\s*\(')
    foreach ($call in $calls) {
        $depth = 1; $start = $call.Index + $call.Length
        $arguments = [Collections.Generic.List[string]]::new()
        for ($i = $start; $i -lt $clean.Length; $i++) {
            $character = $clean[$i]
            if ($character -eq '(') { $depth++ }
            elseif ($character -eq ')') {
                $depth--
                if ($depth -eq 0) {
                    $arguments.Add($clean.Substring($start, $i - $start).Trim())
                    break
                }
            }
            elseif ($character -eq ',' -and $depth -eq 1) {
                $arguments.Add($clean.Substring($start, $i - $start).Trim())
                $start = $i + 1
            }
        }
        Assert-Reference ($depth -eq 0) "$Layout has an unterminated argument list"
        Assert-Reference (@($arguments | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0) "$Layout contains an empty key argument"
        [pscustomobject]@{ Count = $arguments.Count }
    }
}

# Independent, reviewed board facts from the pinned upstream definitions.
$boards = @(
    @{id='pro-iso'; path='pro/rev1/iso'; layout='LAYOUT'; rows=11; cols=8; keys=84; leds=99; sides=16; right=224; encoder=1; mcu='STM32F303'; boot='stm32-dfu'; legacy=$true},
    @{id='numpad'; path='numpad'; layout='LAYOUT'; rows=5; cols=4; keys=18; leds=31; sides=14; right=224; encoder=1; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$true},
    @{id='pro-ansi'; path='pro/rev1/ansi'; layout='LAYOUT'; rows=11; cols=8; keys=83; leds=98; sides=16; right=224; encoder=1; mcu='STM32F303'; boot='stm32-dfu'; legacy=$false},
    @{id='pro-rev2-ansi'; path='pro/rev2/ansi'; layout='LAYOUT'; rows=11; cols=8; keys=83; leds=98; sides=16; right=224; encoder=1; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false},
    @{id='pro-rev2-iso'; path='pro/rev2/iso'; layout='LAYOUT'; rows=11; cols=8; keys=84; leds=99; sides=16; right=224; encoder=1; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false},
    @{id='gmmk2-65-ansi'; path='gmmk2/p65/ansi'; layout='LAYOUT_65_ansi_blocker'; rows=9; cols=8; keys=67; leds=87; sides=20; right=224; encoder=0; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false},
    @{id='gmmk2-65-iso'; path='gmmk2/p65/iso'; layout='LAYOUT_65_iso_blocker'; rows=9; cols=8; keys=68; leds=88; sides=20; right=224; encoder=0; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false},
    @{id='gmmk2-96-ansi'; path='gmmk2/p96/ansi'; layout='LAYOUT'; rows=14; cols=8; keys=99; leds=119; sides=20; right=209; encoder=0; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false},
    @{id='gmmk2-96-iso'; path='gmmk2/p96/iso'; layout='LAYOUT'; rows=14; cols=8; keys=100; leds=120; sides=20; right=209; encoder=0; mcu='WB32F3G71'; boot='wb32-dfu'; legacy=$false}
)
$ReferenceRoot = [IO.Path]::GetFullPath($ReferenceRoot)
if (-not $ManufacturerRoot) { $ManufacturerRoot = Join-Path ([IO.Path]::GetDirectoryName($ReferenceRoot)) 'glorious-gmmk3' }
$ManufacturerRoot = [IO.Path]::GetFullPath($ManufacturerRoot)
$PortRoot = [IO.Path]::GetFullPath($PortRoot)
$manifest = @(Get-Content -LiteralPath (Join-Path $ReferenceRoot 'sources.json') -Raw | ConvertFrom-Json -AsHashtable)
Assert-Reference ($manifest.Count -eq 30) 'Expected exactly 30 model source records'
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) {
    Assert-Reference ($entry.file -is [string] -and $entry.file -cmatch '^[a-z0-9-]+\.(json|c)$') 'Invalid reference filename'
    Assert-Reference ($names.Add($entry.file)) "Duplicate manifest file $($entry.file)"
}

$checkoutVerified = Test-Path -LiteralPath (Join-Path $QmkRoot '.git')
if ($RequireCheckout) { Assert-Reference $checkoutVerified 'Pinned QMK checkout is required but absent' }
if ($checkoutVerified) {
    $QmkRoot = [IO.Path]::GetFullPath($QmkRoot)
    $actualCommit = & git -C $QmkRoot rev-parse HEAD
    Assert-Reference ($LASTEXITCODE -eq 0 -and $actualCommit -ceq $commit) 'QMK checkout HEAD is not the reviewed commit'
}
$results = [Collections.Generic.List[object]]::new()
foreach ($board in $boards) {
    foreach ($suffix in @('.json', '-default.c')) {
        $file = $board.id + $suffix
        $entry = @($manifest | Where-Object { $_.file -ceq $file })
        Assert-Reference ($entry.Count -eq 1) "Missing manifest entry $file"
        $entry = $entry[0]
        $sourcePath = 'keyboards/gmmk/' + $board.path + $(if ($suffix -eq '.json') { '/keyboard.json' } else { '/keymaps/default/keymap.c' })
        $urlRef = if ($board.legacy) { 'master' } else { $commit }
        Assert-Reference ($entry.url -ceq "https://raw.githubusercontent.com/qmk/qmk_firmware/$urlRef/$sourcePath") "Unexpected source URL for $file"
        if (-not $board.legacy) {
            Assert-Reference ($entry.sourcePath -ceq $sourcePath -and $entry.qmkCommit -ceq $commit) "Wrong pinned provenance for $file"
        }
        $path = Join-Path $ReferenceRoot $file
        Assert-Reference (Test-Path -LiteralPath $path -PathType Leaf) "Missing $file"
        $length = (Get-Item -LiteralPath $path).Length
        Assert-Reference ($length -gt 0 -and $length -lt 262144) "Invalid source size for $file"
        if (-not $board.legacy) { Assert-Reference ($entry.bytes -eq $length) "Length mismatch for $file" }
        Assert-Reference ($entry.sha256 -cmatch '^[A-F0-9]{64}$' -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $entry.sha256) "SHA256 mismatch for $file"
        if ($checkoutVerified) {
            $upstreamBlob = & git -C $QmkRoot rev-parse "${commit}:$sourcePath"
            Assert-Reference ($LASTEXITCODE -eq 0 -and $upstreamBlob -cmatch '^[a-f0-9]{40}$') "Missing pinned Git blob for $file"
            $referenceBlob = & git -C $QmkRoot hash-object --no-filters -- $path
            Assert-Reference ($LASTEXITCODE -eq 0 -and $referenceBlob -ceq $upstreamBlob) "Reference differs from pinned upstream bytes: $file"
        }
    }
    $data = Get-Content -LiteralPath (Join-Path $ReferenceRoot ($board.id + '.json')) -Raw | ConvertFrom-Json -AsHashtable
    Assert-Reference ($data.processor -ceq $board.mcu -and $data.bootloader -ceq $board.boot) "$($board.id): unexpected processor/bootloader definition"
    Assert-Reference ($data.matrix_pins.rows.Count -eq $board.rows -and $data.matrix_pins.cols.Count -eq $board.cols) "$($board.id): wrong matrix dimensions"
    Assert-Reference (@($data.matrix_pins.rows | Sort-Object -Unique).Count -eq $board.rows -and @($data.matrix_pins.cols | Sort-Object -Unique).Count -eq $board.cols) "$($board.id): duplicate matrix pins"
    Assert-Reference ($data.layouts.Count -eq 1 -and $data.layouts.Contains($board.layout)) "$($board.id): wrong layout macro"
    $keys = @($data.layouts[$board.layout].layout)
    Assert-Reference ($keys.Count -eq $board.keys) "$($board.id): wrong physical key count"
    $matrixKeys = [Collections.Generic.HashSet[string]]::new()
    foreach ($key in $keys) {
        Assert-Reference ($key.matrix.Count -eq 2 -and (Test-Integer $key.matrix[0] 0 ($board.rows - 1)) -and (Test-Integer $key.matrix[1] 0 ($board.cols - 1))) "$($board.id): key outside matrix"
        Assert-Reference ($matrixKeys.Add(($key.matrix -join ','))) "$($board.id): duplicate matrix key"
        foreach ($axis in @('x','y')) {
            Assert-Reference ($null -ne $key[$axis] -and $key[$axis] -is [ValueType] -and [double]::IsFinite([double]$key[$axis]) -and $key[$axis] -ge 0 -and $key[$axis] -le 32) "$($board.id): invalid key geometry"
        }
        foreach ($dimension in @('w','h')) {
            if ($key.Contains($dimension)) { Assert-Reference ([double]::IsFinite([double]$key[$dimension]) -and $key[$dimension] -gt 0 -and $key[$dimension] -le 8) "$($board.id): invalid key dimension" }
        }
    }
    $leds = @($data.rgb_matrix.layout)
    Assert-Reference ($leds.Count -eq $board.leds) "$($board.id): wrong LED count"
    $matrixLeds = [Collections.Generic.HashSet[string]]::new()
    $sides = [Collections.Generic.List[object]]::new()
    foreach ($led in $leds) {
        Assert-Reference ((Test-Integer $led.x 0 224) -and (Test-Integer $led.y 0 64)) "$($board.id): invalid LED coordinates"
        if ($led.Contains('matrix')) {
            Assert-Reference ($led.matrix.Count -eq 2 -and $matrixKeys.Contains(($led.matrix -join ','))) "$($board.id): LED is not attached to a physical key"
            Assert-Reference ($matrixLeds.Add(($led.matrix -join ','))) "$($board.id): two LEDs attached to the same matrix key"
        } else {
            Assert-Reference ($led.flags -eq 2 -and ($led.x -eq 0 -or $led.x -eq $board.right)) "$($board.id): unexpected non-key LED"
            $sides.Add($led)
        }
    }
    Assert-Reference ($sides.Count -eq $board.sides) "$($board.id): wrong side LED count"
    $leftY = @($sides | Where-Object x -eq 0 | ForEach-Object y | Sort-Object)
    $rightY = @($sides | Where-Object x -eq $board.right | ForEach-Object y | Sort-Object)
    Assert-Reference ($leftY.Count -eq $board.sides / 2 -and ($leftY -join ',') -ceq ($rightY -join ',') -and @($leftY | Sort-Object -Unique).Count -eq $leftY.Count) "$($board.id): side strips cannot be paired by height"
    $unlit = @($matrixKeys | Where-Object { -not $matrixLeds.Contains($_) })
    Assert-Reference ($unlit.Count -eq $board.encoder) "$($board.id): unexpected unlit key"
    if ($board.encoder) {
        Assert-Reference ($data.Contains('encoder') -and $data.encoder.rotary.Count -eq 1) "$($board.id): missing encoder"
        $expectedUnlit = if ($board.id -eq 'numpad') { '2,3' } else { '0,1' }
        Assert-Reference ($unlit[0] -ceq $expectedUnlit) "$($board.id): unlit key is not the encoder button"
    } else { Assert-Reference (-not $data.Contains('encoder')) "$($board.id): unexpected encoder" }
    $calls = @(Get-LayoutArguments (Get-Content -LiteralPath (Join-Path $ReferenceRoot ($board.id + '-default.c')) -Raw) $board.layout)
    Assert-Reference ($calls.Count -eq 2) "$($board.id): expected two upstream default layers"
    foreach ($call in $calls) { Assert-Reference ($call.Count -eq $keys.Count) "$($board.id): default layer key count differs from layout" }
    $results.Add([pscustomobject]@{ id=$board.id; layout=$board.layout; matrixRows=$board.rows; matrixColumns=$board.cols; physicalKeys=$keys.Count; leds=$leds.Count; sideLeds=$sides.Count; encoderButtons=$board.encoder; pinnedGitBytesVerified=$checkoutVerified })
}
$manufacturerOutput = @(& $Python (Join-Path $PSScriptRoot 'Test-Gmmk3References.py') '--reference-root' $ReferenceRoot '--manufacturer-root' $ManufacturerRoot '--port-root' $PortRoot)
Assert-Reference ($LASTEXITCODE -eq 0) 'Independent manufacturer reference verification failed'
$manufacturer = ($manufacturerOutput -join "`n") | ConvertFrom-Json
Assert-Reference ($manufacturer.result -ceq 'PASS' -and @($manufacturer.models).Count -eq 6 -and $manufacturer.originalFiles -eq 42 -and $manufacturer.normalizedFiles -eq 39) 'Unexpected manufacturer verification result'
foreach ($model in $manufacturer.models) { $results.Add($model) }
[pscustomobject]@{
    result = 'PASS'
    checks = $script:checks + $manufacturer.checks
    qmkCommit = $commit
    manifestFiles = $manifest.Count
    manufacturerOriginalFiles = $manufacturer.originalFiles
    manufacturerNormalizedFiles = $manufacturer.normalizedFiles
    manufacturerCatalogSha256 = $manufacturer.manufacturerCatalogSha256
    normalizationProvenanceSha256 = $manufacturer.provenanceSha256
    provenance = $(if ($checkoutVerified) { 'Original 18 files equal pinned QMK Git blobs; 12 GMMK3 references match independent derivation from 42 hash-pinned manufacturer files.' } else { 'Original 18 files checked against manifest only; 12 GMMK3 references match independent derivation from 42 hash-pinned manufacturer files.' })
    hardwareAccess = $false
    hardwareSupportImplied = $false
    models = $results.ToArray()
} | ConvertTo-Json -Depth 6
