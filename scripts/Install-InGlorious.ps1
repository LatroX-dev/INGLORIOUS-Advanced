#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [switch]$Install,
    [string]$TestRoot,
    [switch]$TestAcknowledgeBackgroundNotice
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$notice = 'Gemeinsames Dimmen von Numpad und Tastatur benötigt INGLORIOUS im Hintergrund. Nur das Numpad dimmt auch ohne laufende App. Gespeicherte Farben, Effekte und Belegungen laufen auf den Geräten weiter. Es wird kein Autostart eingerichtet.'
function Assert-NoLink([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked paths are not allowed.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Resolve-Child([string]$Parent,[string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -match '(^/|\\|:|(^|/)\.\.?(/|$)|[\x00-\x1f])') { throw 'Invalid package path.' }
    $value = [IO.Path]::GetFullPath((Join-Path $Parent $Relative))
    if (-not $value.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes its directory.' }
    Assert-NoLink $value
    return $value
}
function Assert-Hash([string]$Path,[long]$Length,[string]$Hash) {
    Assert-NoLink $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -ne $Length -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Hash) { throw ('File integrity check failed: '+[IO.Path]::GetFileName($Path)) }
}
$package = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
Assert-NoLink $package
$manifestPath = Join-Path $package 'package-manifest.json'
Assert-NoLink $manifestPath
if (-not (Test-Path -LiteralPath $manifestPath) -or (Get-Item -LiteralPath $manifestPath).Length -gt 2MB) { throw 'Package manifest is missing or too large.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.product -cne 'INGLORIOUS Advanced' -or $manifest.platform -cne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.entryPoint -cne 'INGLORIOUS Advanced.exe') { throw 'Unsupported package manifest.' }
if ($manifest.files.Count -lt 3 -or $manifest.files.Count -gt 3000) { throw 'Invalid package file count.' }
if ($manifest.installerSha256 -notmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -cne $manifest.installerSha256) { throw 'The installer does not match this package manifest.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files = @()
foreach ($file in $manifest.files) {
    if (-not $names.Add([string]$file.path) -or $file.path -ieq 'installation.json' -or [long]$file.bytes -lt 1 -or [long]$file.bytes -gt 512MB -or $file.sha256 -notmatch '^[A-F0-9]{64}$') { throw 'Invalid or duplicate manifest entry.' }
    $source = Resolve-Child (Join-Path $package 'application') ([string]$file.path)
    Assert-Hash $source ([long]$file.bytes) ([string]$file.sha256)
    $files += [pscustomobject]@{path=[string]$file.path;source=$source;bytes=[long]$file.bytes;sha256=[string]$file.sha256}
}
$entryStem = [IO.Path]::GetFileNameWithoutExtension([string]$manifest.entryPoint)
foreach ($suffix in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
    if (-not $names.Contains($entryStem+$suffix)) { throw 'The application entry files are missing or mixed.' }
}
# Reject mixed application identities, including obsolete names accidentally left in a publish folder.
foreach ($otherStem in @('InGlorious Core','INGLORIOUS Core','INGLORIOUS Advanced')) {
    if ($otherStem -ine $entryStem) {
        foreach ($suffix in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
            if ($names.Contains($otherStem+$suffix)) { throw 'Mixed Core/Advanced application entry files are not allowed.' }
        }
    }
}
if ($TestAcknowledgeBackgroundNotice -and -not $TestRoot) { throw 'Test confirmation is only permitted inside an isolated temporary test directory.' }
if ($TestRoot) {
    $test = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (-not $test.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($test) -notlike 'InGlorious-installer-test-*') { throw 'TestRoot must be an isolated InGlorious-installer-test-* child of the system temporary directory.' }
    $destination = Join-Path $test ('installed/'+$manifest.version)
    $shortcut = Join-Path $test ('shortcuts/INGLORIOUS Advanced '+$manifest.version+'.lnk')
} else {
    $destination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Programs/INGLORIOUS Advanced/'+$manifest.version)
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ('INGLORIOUS Advanced '+$manifest.version+'.lnk')
}
$destination = [IO.Path]::GetFullPath($destination); $shortcut = [IO.Path]::GetFullPath($shortcut)
Assert-NoLink $destination; Assert-NoLink $shortcut
if ($destination.StartsWith($package+'\',[StringComparison]::OrdinalIgnoreCase) -or $package.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase) -or $destination -eq $package) { throw 'Package and install destination must be separate.' }
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $shortcut)) { throw 'This version or its shortcut already exists. No existing installation is overwritten.' }
$plan = [ordered]@{mode='plan';product='INGLORIOUS Advanced';version=$manifest.version;destination=$destination;shortcut=$shortcut;files=$files.Count;backgroundNotice=$notice;autostart=$false;hardwareAccess=$false;appStarted=$false}
if (-not $Install) { [pscustomobject]$plan; return }
if ($TestRoot) {
    if (-not $TestAcknowledgeBackgroundNotice) { throw 'Isolated installation test requires explicit simulated notice acknowledgement.' }
} else {
    Add-Type -AssemblyName PresentationFramework
    $answer = [Windows.MessageBox]::Show("INGLORIOUS Advanced $($manifest.version) für diesen Benutzer installieren?`n`n$notice`n`nZiel: $destination`n`nEin Eintrag im Startmenü wird erstellt. Die App wird nicht gestartet.",'INGLORIOUS installieren','YesNo','Information','No')
    if ($answer -ne [Windows.MessageBoxResult]::Yes) { Write-Output 'Installation cancelled; no application files written.'; return }
}
$parent = [IO.Path]::GetDirectoryName($destination)
Assert-NoLink $parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent ('.install-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$locks = [Collections.Generic.List[IO.FileStream]]::new()
try {
    # Hold every reviewed source against replacement until its verified copy is complete.
    foreach ($file in $files) { $locks.Add([IO.FileStream]::new($file.source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)) }
    foreach ($file in $files) {
        Assert-Hash $file.source $file.bytes $file.sha256
        $target = Resolve-Child $staging $file.path
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        $readStream = [IO.FileStream]::new($file.source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $writeStream = [IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
        try { $readStream.CopyTo($writeStream); $writeStream.Flush($true) } finally { $readStream.Dispose(); $writeStream.Dispose() }
        Assert-Hash $target $file.bytes $file.sha256
    }
    $receipt = [ordered]@{schema=1;product='INGLORIOUS Advanced';version=$manifest.version;installedUtc=[DateTime]::UtcNow.ToString('o');files=$manifest.files;shortcut=$shortcut;backgroundNoticeAccepted=$true;autostart=$false;hardwareAccess=$false;appStarted=$false}
    [IO.File]::WriteAllText((Join-Path $staging 'installation.json'),($receipt|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Assert-NoLink $destination
    [IO.Directory]::Move($staging,$destination)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($shortcut)) -Force | Out-Null
    Assert-NoLink $shortcut
    if (Test-Path -LiteralPath $shortcut) { throw 'Shortcut appeared during installation; it is not overwritten.' }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = Join-Path $destination $manifest.entryPoint
        $link.WorkingDirectory = $destination
        $link.Description = 'INGLORIOUS Advanced'
        $link.IconLocation = $link.TargetPath+',0'
        $link.Save()
        if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut could not be created.' }
    } finally { if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) } }
    $plan.mode='installed'; [pscustomobject]$plan
} catch {
    # Never clean a computed installation tree automatically; preserve it for diagnosis.
    throw ('Installation incomplete. Existing files were not overwritten. Review '+$destination+' and '+$staging+'. '+$_.Exception.Message)
} finally { foreach ($handle in $locks) { $handle.Dispose() } }
