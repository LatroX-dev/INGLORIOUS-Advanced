#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$DownloadDirectory=(Join-Path $PSScriptRoot '../artifacts/downloads'),[switch]$Offline)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'The Windows installer compiler requires Windows.'}
function Assert-NoLink([string]$Path){for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){if((Test-Path -LiteralPath $part)-and((Get-Item -LiteralPath $part -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked compiler paths are not allowed.'}}}
function Assert-CompilerDownload([string]$Path,[object]$Pin){
 Assert-NoLink $Path
 if(-not(Test-Path -LiteralPath $Path -PathType Leaf)-or(Get-FileHash -LiteralPath $Path).Hash-cne$Pin.sha256){throw ('Compiler dependency hash mismatch: '+$Pin.file)}
 if($Pin.PSObject.Properties['bytes']-and(Get-Item -LiteralPath $Path).Length-ne$Pin.bytes){throw ('Compiler dependency size mismatch: '+$Pin.file)}
}
function Get-CompilerDownload([object]$Pin){
 $file=Join-Path $cache $Pin.file;Assert-NoLink $file
 if(Test-Path -LiteralPath $file){Assert-CompilerDownload $file $Pin;return}
 if($Offline){throw ('Missing compiler download: '+$Pin.file)}
 $requests=@(@{Uri=$Pin.url;Headers=@{}})
 # Retry the exact official compiler asset through GitHub's API when its
 # browser download route fails. The existing checksum and signature stay required.
 if($Pin.file-ceq'innosetup-6.7.3.exe'-and
    $Pin.url-ceq'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'-and
    $Pin.sha256-ceq'9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'){
  $fallback=@{Uri='https://api.github.com/repos/jrsoftware/issrc/releases/assets/430321419';Headers=@{Accept='application/octet-stream'}}
  $requests+=@($fallback,$fallback)
 }
 for($attempt=0;$attempt-lt$requests.Count;$attempt++){
  $partial=$file+'.'+[Guid]::NewGuid().ToString('N')+'.partial';Assert-NoLink $partial
  try{
   try{Invoke-WebRequest -Uri $requests[$attempt].Uri -Headers $requests[$attempt].Headers -OutFile $partial -MaximumRedirection 5 -TimeoutSec 300}
   catch{
    if($attempt+1-ge$requests.Count){throw ('Compiler dependency download failed after '+$requests.Count+' attempt(s): '+$Pin.file)}
    Write-Warning ('Compiler dependency download failed; retrying the same pinned official asset: '+$Pin.file)
    continue
   }
   Assert-CompilerDownload $partial $Pin
   Assert-NoLink $file
   [IO.File]::Move($partial,$file)
   return
  }finally{if(Test-Path -LiteralPath $partial){Assert-NoLink $partial;Remove-Item -LiteralPath $partial -Force}}
 }
}
$output=[IO.Path]::GetFullPath($OutputDirectory);$cache=[IO.Path]::GetFullPath($DownloadDirectory)
Assert-NoLink $output;Assert-NoLink $cache
if(Test-Path -LiteralPath $output){throw 'Use a new compiler directory.'}
$pins=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer-dependencies.json') -Raw|ConvertFrom-Json).innoSetup
$unpackPin=@((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-dependencies.json') -Raw|ConvertFrom-Json).downloads|Where-Object id -eq 'innounp')[0]
New-Item -ItemType Directory -Path $cache -Force|Out-Null
foreach($pin in @($pins,$unpackPin)){
 Get-CompilerDownload $pin
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
