#Requires -Version 7.2
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][string]$ToolCatalogPath,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [switch]$Offline,
 [switch]$TestInstaller
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not$IsWindows){throw 'Build the Windows installer on Windows.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package=[IO.Path]::GetFullPath($PackageDirectory);$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Installer output must be new.'}
$manifest=Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw|ConvertFrom-Json
$version=$manifest.version
if($version-cnotmatch'^\d+\.\d+\.\d+$'-or$manifest.product-cne'INGLORIOUS Advanced'-or$manifest.platform-cne'win-x64'){throw 'Unexpected application package.'}
$application=Join-Path $package 'application'
# Reapply the strict publish allowlist. The compiler never uses a wildcard file include.
$validated=& (Join-Path $PSScriptRoot 'New-WindowsPackage.ps1') -PublishDirectory $application -Destination ($output+'-validation-only') -ExpectedVersion $version -ToolCatalogPath $ToolCatalogPath
if($manifest.schema-ne1-or$manifest.entryPoint-cne'INGLORIOUS Advanced.exe'-or$manifest.files.Count-ne$validated.verifiedFiles.Count){throw 'Package manifest does not contain the complete verified application.'}
$manifestMembers=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($record in $manifest.files){if(-not$manifestMembers.TryAdd($record.path,$record)){throw 'Duplicate package manifest member.'}}
foreach($record in $validated.verifiedFiles){
 if(-not$manifestMembers.ContainsKey($record.path)){throw 'Package manifest member differs from the verified application.'}
 $listed=$manifestMembers[$record.path]
 if($listed.bytes-ne$record.bytes-or$listed.sha256-cne$record.sha256){throw 'Package manifest content differs from the verified application.'}
}
New-Item -ItemType Directory -Path $output|Out-Null
$compiler=& (Join-Path $PSScriptRoot 'Initialize-InstallerCompiler.ps1') -OutputDirectory (Join-Path $output 'compiler-tools') -Offline:$Offline
$lines=[Collections.Generic.List[string]]::new()
foreach($record in $validated.verifiedFiles){
 if($record.path-match'(^/|\\|:|(^|/)\.\.?(/|$)|["{}\x00-\x1f])'-or$record.path.EndsWith('.pdb',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe installer file member.'}
 $source=Join-Path $application $record.path
 if((Get-Item -LiteralPath $source).Length-ne$record.bytes-or(Get-FileHash -LiteralPath $source).Hash-cne$record.sha256){throw 'Application package changed before compiling.'}
 $relativeDirectory=[IO.Path]::GetDirectoryName($record.path).Replace('/','\')
 $destination='{app}'+$(if($relativeDirectory){'\'+$relativeDirectory}else{''})
 $lines.Add(('Source: "{0}"; DestDir: "{1}"; Flags: ignoreversion' -f $source,$destination))
}
$innoLicense=Join-Path ([IO.Path]::GetDirectoryName($compiler.compiler)) 'License.txt'
$lines.Add(('Source: "{0}"; DestDir: "{{app}}\Licenses"; DestName: "Inno-Setup.txt"; Flags: ignoreversion' -f $innoLicense))
$include=Join-Path $output 'verified-files.iss'
[IO.File]::WriteAllLines($include,$lines,[Text.UTF8Encoding]::new($true))
# Render a simple installer panel from the existing brand assets; no new logo artwork.
Add-Type -AssemblyName System.Drawing
$image=Join-Path $output 'wizard.png';$small=Join-Path $root 'src/InGlorious.Core/Assets/inglorious-mark.png'
$bitmap=[Drawing.Bitmap]::new(492,942);$graphics=[Drawing.Graphics]::FromImage($bitmap);$logo=[Drawing.Image]::FromFile($small)
$fonts=[Drawing.Text.PrivateFontCollection]::new();$fonts.AddFontFile((Join-Path $root 'src/InGlorious.Core/Assets/Fonts/INGLORIOUS-Oxanium-SemiBold.ttf'))
$font=[Drawing.Font]::new($fonts.Families[0],34,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
$subfont=[Drawing.Font]::new($fonts.Families[0],25,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
$format=[Drawing.StringFormat]::new();$format.Alignment=[Drawing.StringAlignment]::Center
$white=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(245,240,240));$red=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(239,75,86))
try{
 $graphics.Clear([Drawing.Color]::FromArgb(20,22,24));$graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
 $graphics.DrawImage($logo,[Drawing.Rectangle]::new(74,155,344,344))
 $graphics.DrawString('INGLORIOUS',$font,$white,[Drawing.RectangleF]::new(16,525,460,52),$format)
 $graphics.DrawString('ADVANCED',$subfont,$red,[Drawing.RectangleF]::new(16,575,460,42),$format)
 $bitmap.Save($image,[Drawing.Imaging.ImageFormat]::Png)
}finally{$graphics.Dispose();$bitmap.Dispose();$logo.Dispose();$font.Dispose();$subfont.Dispose();$fonts.Dispose();$format.Dispose();$white.Dispose();$red.Dispose()}
$arguments=@('/Qp',('/O'+$output),('/DAppVersion='+$version),('/DFileList='+$include),('/DBrandImage='+$image),('/DBrandSmallImage='+$small),('/DBrandIcon='+(Join-Path $root 'src/InGlorious.Core/Assets/inglorious.ico')))
if($TestInstaller){$arguments+=@('/DApplicationId={D5748319-8891-40CE-9DB9-9FA2D1B189B8}','/DApplicationMutex=INGLORIOUS.Advanced.InstallerTest.Running','/DInstallationMutex=INGLORIOUS.Advanced.InstallerTest.Setup','/DStartupValueName=INGLORIOUS Advanced Installer Test','/DStartupRegistryKey=Software\INGLORIOUS-Installer-Checks\Run','/DStartupApprovalKey=Software\INGLORIOUS-Installer-Checks\StartupApproved\Run','/DTestInstallerMode=1',('/DUserDataDirectory='+(Join-Path $output 'fake-user-data')))}
& $compiler.compiler @arguments (Join-Path $root 'packaging/windows/INGLORIOUS-Advanced.iss') *> (Join-Path $output 'compile.log')
if($LASTEXITCODE-ne0){Get-Content -LiteralPath (Join-Path $output 'compile.log') -Tail 30|Out-Host;throw 'Native installer compilation failed.'}
$setup=Join-Path $output ('INGLORIOUS-Advanced-'+$version+'-win-x64-Setup.exe')
if(-not(Test-Path -LiteralPath $setup)-or(Get-Item -LiteralPath $setup).VersionInfo.ProductVersion.Trim()-cne$version){throw 'Unexpected installer output version.'}
[IO.File]::WriteAllText(($setup+'.sha256'),((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($setup)+"`n"),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{installer=$setup;version=$version;files=$validated.verifiedFiles.Count+1;testInstaller=[bool]$TestInstaller;compilerVerified=$true;installerSigned=$false;hardwareAccess=$false}
