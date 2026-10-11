#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicSourceDirectory,
    [Parameter(Mandatory)][string]$Destination,
    [string]$QmkDirectory=(Join-Path $PSScriptRoot '../firmware/qmk_firmware'),
    [string]$SourceCache=(Join-Path $PSScriptRoot '../artifacts/source-cache'),
    [switch]$Download
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false)
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$public=[IO.Path]::GetFullPath($PublicSourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target=[IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
$qmk=[IO.Path]::GetFullPath($QmkDirectory)
$cache=[IO.Path]::GetFullPath($SourceCache)
$catalog=Get-Content -LiteralPath (Join-Path $root 'reference/source-distribution.json') -Raw|ConvertFrom-Json
if($catalog.schema-ne1){throw 'Unsupported corresponding-source catalog.'}
function Assert-NoLink([string]$Path){
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part)-and((Get-Item -LiteralPath $part).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked source or output path is not accepted.'}
    }
}
function Safe-Child([string]$Parent,[string]$Relative){
    if([string]::IsNullOrWhiteSpace($Relative)-or[IO.Path]::IsPathRooted($Relative)-or$Relative.Contains('\')-or$Relative.Contains(':')-or$Relative.Split('/')-contains'..'){throw 'Unsafe manifest path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $Parent $Relative))
    if(-not$path.StartsWith($Parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Source path leaves its reviewed root.'}
    Assert-NoLink $path
    return $path
}
function Verified([string]$Path,[long]$Bytes,[string]$Sha256){
    Assert-NoLink $Path
    if(-not(Test-Path -LiteralPath $Path -PathType Leaf)-or(Get-Item -LiteralPath $Path).Length-ne$Bytes-or$Sha256-cnotmatch'^[0-9A-F]{64}$'-or(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash-cne$Sha256){throw ('Source integrity check failed: '+[IO.Path]::GetFileName($Path))}
}
foreach($path in @($public,$target,$qmk,$cache)){Assert-NoLink $path}
if($public.Equals($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Use a reviewed public export, never the working directory.'}
if($target.Equals($public,[StringComparison]::OrdinalIgnoreCase)-or$target.StartsWith($public+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be outside the public source input.'}
if((Test-Path -LiteralPath $target)-or(Test-Path -LiteralPath ($target+'.zip'))){throw 'Use a new output directory and ZIP; nothing is overwritten.'}
$manifestPath=Join-Path $public 'source-export-manifest.json'
Assert-NoLink $manifestPath
$export=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($export.mode-cne'local-export'-or$export.hardwareAccess-ne$false-or$export.networkAccess-ne$false){throw 'The input is not a reviewed public source export.'}
$paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($file in $export.files){
    if(-not$paths.Add($file.path)){throw 'Duplicate source manifest path.'}
    if($file.path-match'(^|/)(backups|data|artifacts|bin|obj|\.git|vendor|toolchain|releases|candidates)(/|$)'-or$file.path-match'(?i)(local-firmware-bindings|\.bin$|\.exe$|\.dll$|\.log$)'){throw 'Private or built material is not permitted in the source export.'}
    Verified (Safe-Child $public $file.path) $file.bytes $file.sha256
}
foreach($required in @('LICENSE','COPYRIGHT.md','THIRD-PARTY-NOTICES.md','firmware/targets.json','src/InGlorious.Core/SetupFirmwareCatalog.json')){
    if(-not$paths.Contains($required)){throw ('Required release source is missing: '+$required)}
}
$actual=@(Get-ChildItem -LiteralPath $public -File -Recurse -Force)
foreach($file in $actual){
    Assert-NoLink $file.FullName
    $relative=[IO.Path]::GetRelativePath($public,$file.FullName).Replace('\','/')
    if($relative-cne'source-export-manifest.json'-and-not$paths.Contains($relative)){throw 'The public source input contains unreviewed files.'}
}
foreach($repo in $catalog.repositories){
    if($repo.id-cnotmatch'^[a-z0-9-]+$'-or$repo.commit-cnotmatch'^[0-9a-f]{40}$'){throw 'Invalid repository pin.'}
    $checkout=if($repo.relative-eq'.'){$qmk}else{Safe-Child $qmk $repo.relative}
    $commit=@(& git -C $checkout rev-parse ($repo.commit+'^{commit}'))
    if($LASTEXITCODE-ne0-or$commit.Count-ne1-or$commit[0]-cne$repo.commit){throw ('Pinned source commit is unavailable: '+$repo.id)}
}
foreach($archive in $catalog.archives){
    $path=Safe-Child $cache $archive.file
    if(-not(Test-Path -LiteralPath $path)){
        if(-not$Download){throw ('Source archive is missing; use -Download or populate the cache: '+$archive.file)}
        if($archive.url-cnotmatch'^https://(dfu-util\.sourceforge\.net/releases/|codeload\.github\.com/)'-and$archive.url-cne'https://github.com/libusb/libusb/releases/download/v1.0.29/libusb-1.0.29.tar.bz2'){throw 'Unapproved source archive origin.'}
        New-Item -ItemType Directory -Path $cache -Force|Out-Null
        Invoke-WebRequest -Uri $archive.url -OutFile $path
    }
    Verified $path $archive.bytes $archive.sha256
}
New-Item -ItemType Directory -Path $target|Out-Null
$appTarget=Join-Path $target 'inglorious'
New-Item -ItemType Directory -Path $appTarget|Out-Null
foreach($file in $export.files){
    $source=Safe-Child $public $file.path
    Verified $source $file.bytes $file.sha256
    $dest=Safe-Child $appTarget $file.path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force|Out-Null
    Copy-Item -LiteralPath $source -Destination $dest
    Verified $dest $file.bytes $file.sha256
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $appTarget 'source-export-manifest.json')
$upstream=Join-Path $target 'upstream'
New-Item -ItemType Directory -Path $upstream|Out-Null
$records=[Collections.Generic.List[object]]::new()
foreach($repo in $catalog.repositories){
    $checkout=if($repo.relative-eq'.'){$qmk}else{Safe-Child $qmk $repo.relative}
    $file=$repo.id+'-'+$repo.commit+'.tar'
    $out=Join-Path $upstream $file
    & git -C $checkout archive --format=tar ('--prefix='+$repo.id+'/') ('--output='+$out) $repo.commit
    if($LASTEXITCODE-ne0){throw ('Could not archive pinned source: '+$repo.id)}
    $records.Add([ordered]@{path='upstream/'+$file;bytes=(Get-Item -LiteralPath $out).Length;sha256=(Get-FileHash -LiteralPath $out).Hash;origin=$repo.url;commit=$repo.commit;restorePath=if($repo.relative-eq'.'){'firmware/qmk_firmware'}else{'firmware/qmk_firmware/'+$repo.relative}})
}
foreach($archive in $catalog.archives){
    if($archive.id-ceq'wb32-dfu-updater'){
        # The reference repository also bundles a driver ZIP. Distribute only the
        # reviewed protocol source/build files and license, never that driver.
        $prefix='wb32-dfu-updater-'+$archive.version+'/'
        $members=@('CMakeLists.txt','LICENSE','README.md','bootstrap.sh','config.h',
            'cmake/building_output.cmake','cmake/compiler_conf.cmake','cmake/config.h.in','cmake/install_common.cmake',
            'src_common/cmdinfo.c','src_common/cmdinfo.h','src_common/dfufw.c','src_common/dfufw.h',
            'src_common/dfufwdevice.c','src_common/dfufwdevice.h','src_common/hex2bin.c','src_common/hex2bin.h',
            'src_common/kbhit.c','src_common/kbhit.h','src_common/main.c','src_windows/getopt.c','src_windows/getopt.h')
        $reference=Join-Path $upstream ('wb32-reference-'+$archive.version)
        New-Item -ItemType Directory -Path $reference|Out-Null
        $arguments=@('-xzf',(Join-Path $cache $archive.file),'-C',$reference,'--strip-components=1')+@($members|ForEach-Object{$prefix+$_})
        & tar @arguments
        if($LASTEXITCODE-ne0){throw 'Could not extract reviewed WB32 source files.'}
        $sourceRecords=@(foreach($member in $members){
            $file=Safe-Child $reference $member
            [ordered]@{path=$member;bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash}
        })
        $records.Add([ordered]@{path='upstream/'+[IO.Path]::GetFileName($reference);origin=$archive.url;version=$archive.version;sourceArchiveSha256=$archive.sha256;files=$sourceRecords})
        continue
    }
    $out=Join-Path $upstream $archive.file
    Copy-Item -LiteralPath (Join-Path $cache $archive.file) -Destination $out
    Verified $out $archive.bytes $archive.sha256
    $records.Add([ordered]@{path='upstream/'+$archive.file;bytes=$archive.bytes;sha256=$archive.sha256;origin=$archive.url;version=$archive.version})
}
$readme=@'
# INGLORIOUS corresponding source

`inglorious/` is the complete reviewed application, adapter, firmware overlay, test and build source. It contains its own license notices and source-export manifest. `upstream/` contains the exact QMK base, required submodules, dfu-util, libusb and WB32 protocol-reference sources. Keep this package beside the matching binary release.

For QMK, extract the qmk archive into a temporary directory and move its qmk directory to `inglorious/firmware/qmk_firmware`. Extract chibios, chibios-contrib, lufa and printf into that tree's `lib` directory under their respective names. These archives do not contain Git metadata. The normal build bootstrap can instead obtain the same exact commits with metadata, as required by the build's commit checks. Project overlays and hash-checked patch scripts then apply the INGLORIOUS changes. See the app's docs/public-release.md and firmware/README.md for build instructions.

For the native firmware adapter tools, extract dfu-util and libusb separately and use their configure/make build instructions. docs/license-audit.md records the upstream MinGW procedure and how to rebuild the adapter/app with changed native hashes. Neither a private signing key nor a developer keyboard is needed.

This package contains no original keyboard firmware, personal settings or private device bindings. It is source, not a device backup. Tests do not certify untested hardware.
'@
[IO.File]::WriteAllText((Join-Path $target 'README.md'),$readme,$utf8)
$result=[ordered]@{schema=1;firmwareVersion=$catalog.firmwareVersion;hardwareAccess=$false;appSourceManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash;sourceCatalogSha256=(Get-FileHash -LiteralPath (Join-Path $root 'reference/source-distribution.json')).Hash;upstream=$records}
[IO.File]::WriteAllText((Join-Path $target 'corresponding-source-manifest.json'),($result|ConvertTo-Json -Depth 8),$utf8)
[IO.Compression.ZipFile]::CreateFromDirectory($target,$target+'.zip',[IO.Compression.CompressionLevel]::Optimal,$false)
((Get-FileHash -LiteralPath ($target+'.zip')).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($target+'.zip'))|Set-Content -LiteralPath ($target+'.zip.sha256') -Encoding utf8
[ordered]@{directory=$target;archive=$target+'.zip';sha256=(Get-FileHash -LiteralPath ($target+'.zip')).Hash;applicationFiles=$export.files.Count;upstreamArchives=$records.Count;hardwareAccess=$false}
