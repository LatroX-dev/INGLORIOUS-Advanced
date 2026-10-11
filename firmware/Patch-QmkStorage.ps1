[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'qmk_firmware\quantum\wear_leveling\wear_leveling.c'),
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$originalHash = '9BE27E0CF0346B61DC6A2934A03FF0F259C02520C3787B61EF832D61BC3DDDFB'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Get-TextHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($Text))) }
$path = [IO.Path]::GetFullPath($SourcePath)
$source = $utf8.GetString([IO.File]::ReadAllBytes($path))
$nl = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }
$edits = [Collections.Generic.List[object]]::new()
function Add-Edit([string]$Before, [string]$After) { $edits.Add(@($Before.Replace("`r`n", "`n").Replace("`n", $nl), $After.Replace("`r`n", "`n").Replace("`n", $nl))) }
Add-Edit '#include "wear_leveling_internal.h"' @'
#include "wear_leveling_internal.h"
#ifdef IG_CACHE_STAGING
#    include "ig_storage.h"
static bool ig_storage_borrowed, ig_storage_read_only_reload, ig_storage_fault;
#endif
'@
Add-Edit '        backing_store_read_bulk((WEAR_LEVELING_LOGICAL_SIZE), entry.raw16, 4);' '        if (!backing_store_read_bulk((WEAR_LEVELING_LOGICAL_SIZE), entry.raw16, 4)) return WEAR_LEVELING_FAILED;'
Add-Edit '        backing_store_read_bulk((WEAR_LEVELING_LOGICAL_SIZE), entry.raw32, 2);' '        if (!backing_store_read_bulk((WEAR_LEVELING_LOGICAL_SIZE), entry.raw32, 2)) return WEAR_LEVELING_FAILED;'
Add-Edit '        backing_store_read((WEAR_LEVELING_LOGICAL_SIZE) + 0, &entry.raw64);' '        if (!backing_store_read((WEAR_LEVELING_LOGICAL_SIZE) + 0, &entry.raw64)) return WEAR_LEVELING_FAILED;'
Add-Edit '    wear_leveling.write_address = address;' @'
    wear_leveling.write_address = address;
#ifdef IG_CACHE_STAGING
    // ABORT is a read-only replay, even on a corrupt/full log. Never erase.
    if (ig_storage_read_only_reload) return status;
#endif
'@
Add-Edit 'wear_leveling_status_t wear_leveling_init(void) {' @'
#ifdef IG_CACHE_STAGING
static wear_leveling_status_t ig_wear_leveling_init_internal(void) {
#else
wear_leveling_status_t wear_leveling_init(void) {
#endif
'@
Add-Edit 'wear_leveling_status_t wear_leveling_erase(void) {' @'
wear_leveling_status_t wear_leveling_erase(void) {
#ifdef IG_CACHE_STAGING
    if (ig_storage_borrowed || ig_storage_fault) return WEAR_LEVELING_FAILED;
#endif
'@
Add-Edit 'wear_leveling_status_t wear_leveling_write(const uint32_t address, const void *value, size_t length) {' @'
#ifdef IG_CACHE_STAGING
static wear_leveling_status_t ig_wear_leveling_write_internal(const uint32_t address, const void *value, size_t length, bool force) {
#else
wear_leveling_status_t wear_leveling_write(const uint32_t address, const void *value, size_t length) {
#endif
'@
Add-Edit '    if (memcmp(value, &wear_leveling.cache[address], length) == 0) {' @'
#ifdef IG_CACHE_STAGING
    if (!force && memcmp(value, &wear_leveling.cache[address], length) == 0) {
#else
    if (memcmp(value, &wear_leveling.cache[address], length) == 0) {
#endif
'@
Add-Edit '    memcpy(&wear_leveling.cache[address], value, length);' @'
    // A forced stage flush aliases this exact cache range; avoid overlapping memcpy.
    if (value != &wear_leveling.cache[address]) memcpy(&wear_leveling.cache[address], value, length);
'@
$tail = @'

/* INGLORIOUS_CACHE_STAGING_BEGIN */
#ifdef IG_CACHE_STAGING
wear_leveling_status_t wear_leveling_init(void) {
    if (ig_storage_borrowed) return WEAR_LEVELING_FAILED;
    return ig_wear_leveling_init_internal();
}
wear_leveling_status_t wear_leveling_write(uint32_t address, const void *value, size_t length) {
    if (ig_storage_borrowed || ig_storage_fault || !value || address > WEAR_LEVELING_LOGICAL_SIZE || length > WEAR_LEVELING_LOGICAL_SIZE - address) return WEAR_LEVELING_FAILED;
    return ig_wear_leveling_write_internal(address, value, length, false);
}
#    include "ig_storage.inc"
#endif
/* INGLORIOUS_CACHE_STAGING_END */

'@
$tail = $tail.Replace("`r`n", "`n").Replace("`n", $nl)
$baseline = $source
$alreadyPatched = $source.Contains('/* INGLORIOUS_CACHE_STAGING_BEGIN */')
if ($alreadyPatched) {
    if (!$baseline.EndsWith($tail, [StringComparison]::Ordinal)) { throw 'Unexpected existing storage patch tail.' }
    $baseline = $baseline.Substring(0, $baseline.Length - $tail.Length)
    for ($i = $edits.Count - 1; $i -ge 0; $i--) {
        $edit = $edits[$i]
        if ([regex]::Matches($baseline, [regex]::Escape($edit[1])).Count -ne 1) { throw 'Unexpected existing storage patch structure.' }
        $baseline = $baseline.Replace($edit[1], $edit[0])
    }
}
if ((Get-TextHash $baseline) -ne $originalHash) { throw 'wear_leveling.c does not match pinned QMK. No file was changed.' }
$patched = $baseline
foreach ($edit in $edits) {
    if ([regex]::Matches($patched, [regex]::Escape($edit[0])).Count -ne 1) { throw 'Pinned storage patch anchor is not unique.' }
    $patched = $patched.Replace($edit[0], $edit[1])
}
$patched += $tail
if ($alreadyPatched -and $source -cne $patched) { throw 'Existing storage patch differs from this generator. No file was changed.' }
$changed = $source -cne $patched
if ($Apply -and $changed) { [IO.File]::WriteAllBytes($path, $utf8.GetBytes($patched)) }
[pscustomobject]@{source=$path; originalSha256=$originalHash; patchedSha256=(Get-TextHash $patched); applied=[bool]$Apply; changed=[bool]($Apply -and $changed); secondStagingBytes=0}
