#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$DownloadDirectory=(Join-Path $PSScriptRoot '../artifacts/downloads'),[switch]$Offline)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'The Windows installer compiler requires Windows.'}
function Assert-NoLink([string]$Path){for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){if((Test-Path -LiteralPath $part)-and((Get-Item -LiteralPath $part).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked compiler paths are not allowed.'}}}
$output=[IO.Path]::GetFullPath($OutputDirectory);$cache=[IO.Path]::GetFullPath($DownloadDirectory)
Assert-NoLink $output;Assert-NoLink $cache
if(Test-Path -LiteralPath $output){throw 'Use a new compiler directory.'}
$pins=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer-dependencies.json') -Raw|ConvertFrom-Json).innoSetup
$unpackPin=@((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-dependencies.json') -Raw|ConvertFrom-Json).downloads|Where-Object id -eq 'innounp')[0]
New-Item -ItemType Directory -Path $cache -Force|Out-Null
foreach($pin in @($pins,$unpackPin)){
 $file=Join-Path $cache $pin.file;Assert-NoLink $file
 if(-not(Test-Path -LiteralPath $file)){if($Offline){throw ('Missing compiler download: '+$pin.file)};Invoke-WebRequest -Uri $pin.url -OutFile $file}
 if((Get-FileHash -LiteralPath $file).Hash-cne$pin.sha256){throw ('Compiler dependency hash mismatch: '+$pin.file)}
}
$installer=Join-Path $cache $pins.file
if((Get-Item -LiteralPath $installer).Length-ne$pins.bytes){throw 'Compiler installer size differs.'}
$signature=Get-AuthenticodeSignature -LiteralPath $installer
if($signature.Status-ne'Valid'-or$signature.SignerCertificate.Subject-cne$pins.publisher-or$signature.SignerCertificate.Thumbprint-cne$pins.signerThumbprint){throw 'Official compiler signature could not be verified.'}
New-Item -ItemType Directory -Path $output|Out-Null
$unpacker=Join-Path $output 'unpacker';Expand-Archive -LiteralPath (Join-Path $cache $unpackPin.file) -DestinationPath $unpacker
$executable=Join-Path $unpacker 'innounp.exe'
if((Get-FileHash -LiteralPath $executable).Hash-cne'94377351F03257B06E221285B16D41108040151AB672B96FAF1D3D874A7D0A32'){throw 'Unpacker executable differs.'}
$compilerRoot=Join-Path $output 'compiler'
& $executable -x -y ('-d'+$compilerRoot) $installer *> (Join-Path $output 'extraction.log')
if($LASTEXITCODE-ne0){throw 'Compiler extraction failed. It was not installed.'}
$compiler=Join-Path $compilerRoot '{app}/ISCC.exe';Assert-NoLink $compiler
$signature=Get-AuthenticodeSignature -LiteralPath $compiler
if((Get-FileHash -LiteralPath $compiler).Hash-cne$pins.compilerSha256-or$signature.Status-ne'Valid'-or$signature.SignerCertificate.Subject-cne$pins.publisher){throw 'Extracted compiler verification failed.'}
[pscustomobject]@{compiler=$compiler;version=$pins.version;publisher='Pyrsys B.V.';installed=$false;hardwareAccess=$false}
