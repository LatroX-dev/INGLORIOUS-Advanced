#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Native','Firmware','All')][string]$Scope='All',
    [string]$DownloadDirectory=(Join-Path $PSScriptRoot '../artifacts/downloads'),
    [string]$NativeDestination=(Join-Path $PSScriptRoot '../artifacts/dependencies'),
    [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'This bootstrap prepares the Windows release toolchain. Desktop source builds work separately on macOS and Linux.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pins=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'build-dependencies.json')|ConvertFrom-Json
if($pins.schema-ne1){throw 'Unsupported dependency catalog.'}
function Assert-NoLink([string]$Path){
    $value=[IO.Path]::GetFullPath($Path)
    while($value){
        if((Test-Path -LiteralPath $value)-and((Get-Item -LiteralPath $value -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw ('Linked dependency path: '+$value)}
        $value=[IO.Path]::GetDirectoryName($value)
    }
}
function Get-VerifiedDownload([string]$Id){
    $entry=@($pins.downloads|Where-Object id -CEQ $Id)
    if($entry.Count-ne1){throw 'Dependency is not pinned.'}
    $entry=$entry[0]
    $file=Join-Path ([IO.Path]::GetFullPath($DownloadDirectory)) $entry.file
    Assert-NoLink $file
    if(-not(Test-Path -LiteralPath $file)){
        if($Offline){throw ('Offline dependency is missing: '+$entry.file)}
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force|Out-Null
        $partial=$file+'.'+[Guid]::NewGuid().ToString('N')+'.partial'
        Invoke-WebRequest -Uri $entry.url -OutFile $partial -MaximumRedirection 5
        if((Get-FileHash -LiteralPath $partial).Hash-cne$entry.sha256){throw ('Downloaded dependency checksum differs: '+$entry.file)}
        Move-Item -LiteralPath $partial -Destination $file
    }
    if((Get-FileHash -LiteralPath $file).Hash-cne$entry.sha256){throw ('Cached dependency checksum differs: '+$entry.file)}
    return $file
}
function Invoke-Git([string[]]$Arguments){
    & git @Arguments
    if($LASTEXITCODE-ne0){throw ('Git failed: '+($Arguments-join' '))}
}
$nativePath=$null;$bashPath=$null;$qmkPath=$null
if($Scope-in@('Native','All')){
    $archive=Get-VerifiedDownload 'dfu-util'
    $destination=[IO.Path]::GetFullPath($NativeDestination)
    Assert-NoLink $destination
    $nativePath=Join-Path $destination 'dfu-util-0.11-binaries/win64'
    if(-not(Test-Path -LiteralPath $nativePath)){
        New-Item -ItemType Directory -Path $destination -Force|Out-Null
        $entries=@(& tar -tf $archive)
        if($LASTEXITCODE-ne0-or@($entries|Where-Object {$_-match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))'-or-not$_.StartsWith('dfu-util-0.11-binaries/',[StringComparison]::Ordinal)}).Count){throw 'Unexpected archive path.'}
        & tar -xf $archive -C $destination
        if($LASTEXITCODE-ne0){throw 'Native dependency extraction failed.'}
    }
    foreach($entry in $pins.nativeFiles.PSObject.Properties){
        $file=Join-Path $nativePath $entry.Name;Assert-NoLink $file
        if(-not(Test-Path -LiteralPath $file)-or(Get-FileHash -LiteralPath $file).Hash-cne$entry.Value){throw ('Native dependency changed: '+$entry.Name)}
    }
}
if($Scope-in@('Firmware','All')){
    $archive=Get-VerifiedDownload 'qmk-msys'
    $unpackerArchive=Get-VerifiedDownload 'innounp'
    $toolchain=Join-Path $root 'firmware/toolchain';Assert-NoLink $toolchain
    $unpacker=Join-Path $toolchain 'innounp/innounp.exe'
    if(-not(Test-Path -LiteralPath $unpacker)){
        $unpackerDirectory=Join-Path $toolchain 'innounp'
        if(Test-Path -LiteralPath $unpackerDirectory){throw 'Incomplete unpacker directory; choose a clean checkout.'}
        Expand-Archive -LiteralPath $unpackerArchive -DestinationPath $unpackerDirectory
    }
    $unpackerHash='94377351F03257B06E221285B16D41108040151AB672B96FAF1D3D874A7D0A32'
    if((Get-FileHash -LiteralPath $unpacker).Hash-cne$unpackerHash){throw 'Unpacker executable checksum differs.'}
    $msys=Join-Path $toolchain 'qmk-msys'
    $bashPath=Join-Path $msys '{app}/usr/bin/bash.exe'
    if(-not(Test-Path -LiteralPath $bashPath)){
        if(Test-Path -LiteralPath $msys){throw 'Incomplete QMK toolchain directory; choose a clean checkout.'}
        New-Item -ItemType Directory -Path $msys|Out-Null
        & $unpacker -x -y ('-d'+$msys) $archive *> (Join-Path $toolchain 'qmk-extract.log')
        if($LASTEXITCODE-ne0-or-not(Test-Path -LiteralPath $bashPath)){throw 'QMK toolchain extraction failed.'}
    }
    New-Item -ItemType Directory -Path (Join-Path $msys '{app}/tmp') -Force|Out-Null
    $compiler=@(& $bashPath --noprofile --norc -c 'export PATH=/opt/qmk/bin:/mingw64/bin:/usr/bin:/bin; arm-none-eabi-gcc --version')
    if($LASTEXITCODE-ne0-or$compiler.Count-eq0-or$compiler[0]-notmatch'15\.2\.0'){throw 'Expected ARM compiler 15.2.0 is unavailable.'}
    $qmkPath=Join-Path $root 'firmware/qmk_firmware';Assert-NoLink $qmkPath
    if(-not(Test-Path -LiteralPath $qmkPath)){
        if($Offline){throw 'Pinned QMK checkout is missing in offline mode.'}
        Invoke-Git @('init',$qmkPath)
        Invoke-Git @('-C',$qmkPath,'config','core.autocrlf','false')
        Invoke-Git @('-C',$qmkPath,'config','core.longpaths','true')
        Invoke-Git @('-C',$qmkPath,'remote','add','origin',$pins.qmk.url)
        Invoke-Git @('-C',$qmkPath,'fetch','--depth=1','origin',$pins.qmk.commit)
        Invoke-Git @('-C',$qmkPath,'checkout','--detach',$pins.qmk.commit)
    }
    $head=(& git -C $qmkPath rev-parse HEAD).Trim()
    if($LASTEXITCODE-ne0-or$head-cne$pins.qmk.commit){throw 'Existing QMK checkout has another revision; it was not changed.'}
    foreach($entry in $pins.qmk.submodules.PSObject.Properties){
        $expected=(& git -C $qmkPath rev-parse ($pins.qmk.commit+':'+$entry.Name)).Trim()
        if($LASTEXITCODE-ne0-or$expected-cne$entry.Value){throw 'QMK submodule pin disagrees with the dependency catalog.'}
        $submodule=Join-Path $qmkPath $entry.Name
        if(-not(Test-Path -LiteralPath (Join-Path $submodule '.git'))){
            if($Offline){throw ('Pinned submodule is missing: '+$entry.Name)}
            Invoke-Git @('-C',$qmkPath,'-c','core.autocrlf=false','-c','core.longpaths=true','submodule','update','--init','--depth=1','--',$entry.Name)
        }
        $actual=(& git -C $submodule rev-parse HEAD).Trim()
        if($LASTEXITCODE-ne0-or$actual-cne$entry.Value){throw ('Existing submodule has another revision: '+$entry.Name)}
    }
}
[pscustomobject]@{scope=$Scope;nativeAssetDirectory=$nativePath;bash=$bashPath;qmk=$qmkPath;hardwareAccess=$false;driversInstalled=$false}
