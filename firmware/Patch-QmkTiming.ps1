[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'qmk_firmware\tmk_core\protocol\usb_descriptor.c'),
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$originalHash = '0CCF86013CA002943F28AD0A913F3CC3F0BBE20A7ACC44BBE51C703CC54850B6'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Get-TextHash([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($Value)))
}
$sourceFile = [IO.Path]::GetFullPath($SourcePath)
$source = $utf8.GetString([IO.File]::ReadAllBytes($sourceFile))
$newLine = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }
$begin = '/* INGLORIOUS_DYNAMIC_INTERVAL_BEGIN */'
$end = '/* INGLORIOUS_DYNAMIC_INTERVAL_END */'
$originalSelection = '            Address = &ConfigurationDescriptor;'
$newSelection = @(
    '#ifdef IG_DYNAMIC_USB_INTERVAL',
    '            Address = ig_usb_configuration_descriptor();',
    '#else',
    $originalSelection,
    '#endif'
) -join $newLine
$baseline = $source
if ($source.Contains($begin)) {
    $blockPattern = [regex]::Escape($newLine + $begin) + '[\s\S]*?' + [regex]::Escape($end + $newLine)
    if ([regex]::Matches($source, $blockPattern).Count -ne 1 -or ([regex]::Matches($source, [regex]::Escape($newSelection))).Count -ne 1) { throw 'Unexpected existing timing patch structure.' }
    $baseline = [regex]::Replace($source, $blockPattern, '')
    $baseline = $baseline.Replace($newSelection, $originalSelection)
}
if ((Get-TextHash $baseline) -ne $originalHash) { throw 'usb_descriptor.c does not match the pinned QMK source. No file was changed.' }
$match = [regex]::Match($baseline, '(?m)^const USB_Descriptor_Configuration_t PROGMEM ConfigurationDescriptor = \{[\s\S]*?^\};')
if (!$match.Success) { throw 'Pinned descriptor initializer was not found.' }
$initializer = $match.Value
$parts = [Collections.Generic.List[string]]::new()
$parts.Add($begin)
$parts.Add('#ifdef IG_DYNAMIC_USB_INTERVAL')
$parts.Add('#    if !defined(PROTOCOL_CHIBIOS) || USB_POLLING_INTERVAL_MS != 1')
$parts.Add('#        error "InGlorious dynamic polling requires ChibiOS and a 1 ms base descriptor"')
$parts.Add('#    endif')
$parts.Add('// Extra descriptors reside in flash. RAW HID retains its fixed 1 ms endpoints.')
$parts.Add('extern uint8_t ig_timing_active_interval(void);')
foreach ($interval in @(2, 4, 8)) {
    $copy = $initializer.Replace('const USB_Descriptor_Configuration_t PROGMEM ConfigurationDescriptor =', "static const USB_Descriptor_Configuration_t PROGMEM IgConfigurationDescriptor$interval =")
    $copy = $copy.Replace('USB_POLLING_INTERVAL_MS', [string]$interval)
    $parts.Add($copy)
}
$parts.Add('static const USB_Descriptor_Configuration_t *ig_usb_configuration_descriptor(void) {')
$parts.Add('    switch (ig_timing_active_interval()) {')
foreach ($interval in @(2, 4, 8)) { $parts.Add("        case ${interval}: return &IgConfigurationDescriptor$interval;") }
$parts.Add('        default: return &ConfigurationDescriptor;')
$parts.Add('    }')
$parts.Add('}')
$parts.Add('#endif')
$parts.Add($end)
$insertion = $newLine + ($parts -join $newLine) + $newLine
$patched = $baseline.Insert($match.Index + $match.Length, $insertion)
if ([regex]::Matches($patched, [regex]::Escape($originalSelection)).Count -ne 1) { throw 'Configuration selection is not unique.' }
$patched = $patched.Replace($originalSelection, $newSelection)
if ($source.Contains($begin) -and $source -cne $patched) { throw 'The existing timing patch differs from this generator. No file was changed.' }
$changed = $source -cne $patched
if ($Apply -and $changed) { [IO.File]::WriteAllBytes($sourceFile, $utf8.GetBytes($patched)) }
[pscustomobject]@{
    source = $sourceFile
    originalSha256 = $originalHash
    patchedSha256 = Get-TextHash $patched
    applied = [bool]$Apply
    changed = [bool]($Apply -and $changed)
    flashDescriptorCopies = 3
    ramDescriptorCopies = 0
}
