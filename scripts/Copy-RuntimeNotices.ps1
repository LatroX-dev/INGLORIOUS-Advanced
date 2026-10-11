#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$Runtime)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$deps=Get-Content -LiteralPath (Join-Path $publish 'INGLORIOUS Advanced.deps.json') -Raw|ConvertFrom-Json -AsHashtable
$package='runtimepack.Microsoft.NETCore.App.Runtime.'+$Runtime+'/'
$entry=@($deps.libraries.Keys|Where-Object { $_.StartsWith($package,[StringComparison]::Ordinal) })
if($entry.Count-ne1){throw 'Published runtime package is not unambiguous.'}
$version=$entry[0].Substring($package.Length)
if($version-cnotmatch'^9\.0\.[0-9]+$'){throw 'Unexpected published runtime version.'}
$globalPackages=(& dotnet nuget locals global-packages --list)
if($LASTEXITCODE-ne0-or$globalPackages-notmatch'^global-packages:\s+(.+)$'){throw 'Could not locate restored runtime licenses.'}
$source=Join-Path $Matches[1] ('microsoft.netcore.app.runtime.'+$Runtime+'/'+$version)
$destination=Join-Path $publish 'Licenses'
New-Item -ItemType Directory -Path $destination -Force|Out-Null
foreach($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')){
    $sourceFile=Join-Path $source $name
    $targetFile=Join-Path $destination ('dotnet-'+$Runtime+'-'+$name)
    if(-not(Test-Path -LiteralPath $sourceFile -PathType Leaf)){throw ('Runtime license is missing: '+$name)}
    Copy-Item -LiteralPath $sourceFile -Destination $targetFile
    if((Get-FileHash -LiteralPath $sourceFile).Hash-cne(Get-FileHash -LiteralPath $targetFile).Hash){throw 'Runtime license copy differs.'}
}
