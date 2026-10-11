[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script = Join-Path $workspace 'firmware\Patch-QmkTiming.ps1'
$source = Join-Path $workspace 'firmware\qmk_firmware\tmk_core\protocol\usb_descriptor.c'
$fixtureDirectory = Join-Path $workspace ('artifacts\timing-descriptor-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
$fixture = Join-Path $fixtureDirectory 'usb_descriptor.c'
[IO.File]::Copy($source, $fixture)
$before = (Get-FileHash -LiteralPath $fixture).Hash
$plan = & $script -SourcePath $fixture
if ((Get-FileHash -LiteralPath $fixture).Hash -ne $before -or $plan.applied) { throw 'Default plan changed a source file.' }
$result = & $script -SourcePath $fixture -Apply
$again = & $script -SourcePath $fixture -Apply
if ($again.changed -or (Get-FileHash -LiteralPath $fixture).Hash -ne $result.patchedSha256) { throw 'Patch is not stable and idempotent.' }
$patched = [IO.File]::ReadAllText($fixture)
$original = [regex]::Match($patched, '(?m)^const USB_Descriptor_Configuration_t PROGMEM ConfigurationDescriptor = \{[\s\S]*?^\};').Value
if (!$original) { throw 'Original flash descriptor missing.' }
foreach ($interval in @(2, 4, 8)) {
    $copy = [regex]::Match($patched, "(?m)^static const USB_Descriptor_Configuration_t PROGMEM IgConfigurationDescriptor$interval = \{[\s\S]*?^\};").Value
    $expected = $original.Replace('const USB_Descriptor_Configuration_t PROGMEM ConfigurationDescriptor =', "static const USB_Descriptor_Configuration_t PROGMEM IgConfigurationDescriptor$interval =").Replace('USB_POLLING_INTERVAL_MS', [string]$interval)
    if (!$copy -or $copy -cne $expected) { throw "Descriptor $interval changed a non-polling field." }
    foreach ($direction in @('IN', 'OUT')) {
        if (![regex]::IsMatch($copy, "\.EndpointAddress\s*=\s*\(ENDPOINT_DIR_$direction \| RAW_${direction}_EPNUM\),[\s\S]*?\.PollingIntervalMS\s*=\s*0x01\b")) { throw "Fixed RAW HID $direction interval was lost." }
    }
}
[IO.File]::AppendAllText($fixture, "`n/* unexpected source edit */`n")
$rejected = $false
try { & $script -SourcePath $fixture -Apply | Out-Null } catch { $rejected = $true }
if (!$rejected) { throw 'Modified QMK source was accepted.' }
Write-Output "PASS descriptor patch: plan read-only, idempotence, three exact flash variants, RAW intervals unchanged, modified source rejected. Fixture: $fixtureDirectory"
