#Requires -Version 7.2
[CmdletBinding()]
param([string]$PackageDirectory=(Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath=Join-Path $root 'src/InGlorious.Desktop/packages.lock.json'
$lock=Get-Content -LiteralPath $lockPath -Raw|ConvertFrom-Json
$lines=[Collections.Generic.List[string]]::new()
$records=[Collections.Generic.List[object]]::new()
$lines.Add('INGLORIOUS Advanced desktop preview: dependency licenses and notices')
$lines.Add('Generated from the pinned desktop NuGet lock file. Upstream notices are reproduced unchanged below.')
foreach($property in $lock.dependencies.'net9.0'.PSObject.Properties|Sort-Object Name){
    $package=$property.Value
    if($package.type-eq'Project'){continue}
    $id=$property.Name;$version=$package.resolved
    if($id-cnotmatch'^[A-Za-z0-9.-]+$'-or$version-cnotmatch'^[0-9][A-Za-z0-9.-]*$'){throw 'Unsafe package identity.'}
    $directory=Join-Path $PackageDirectory ($id.ToLowerInvariant()+'/'+$version)
    [xml]$nuspec=Get-Content -LiteralPath (Join-Path $directory ($id.ToLowerInvariant()+'.nuspec'))
    if($nuspec.package.metadata.id-cne$id-or$nuspec.package.metadata.version-cne$version){throw 'Package metadata disagrees with the lock file.'}
    $files=@(Get-ChildItem -LiteralPath $directory -File|Where-Object Name -match '^(LICENSE|COPYING|THIRD.PARTY.NOTICES)(\..*)?$'|Sort-Object Name)
    $texts=[Collections.Generic.List[object]]::new()
    foreach($file in $files){$texts.Add([ordered]@{source='nuget:'+ $id+'/'+$version+'/'+$file.Name;path=$file.FullName})}
    if($texts.Count-eq0){
        $fallback=if($id-eq'Avalonia.BuildServices'-and$version-eq'11.3.2'){'Avalonia-BuildServices-MIT.txt'}
            elseif($id.StartsWith('Avalonia')-and$version-eq'12.1.4'){'Avalonia-MIT.txt'}
            elseif($id-eq'MicroCom.Runtime'-and$version-eq'0.11.6'){'MicroCom-MIT.txt'}
            elseif($id-eq'Tmds.DBus.Protocol'-and$version-eq'0.94.1'){'Tmds-DBus-MIT.txt'}
            else{throw ('A reviewed license source is required for '+$id+' '+$version)}
        $texts.Add([ordered]@{source='Licenses/'+$fallback;path=(Join-Path $root ('Licenses/'+$fallback))})
    }
    $notices=@()
    $lines.Add('');$lines.Add(('='*78));$lines.Add($id+' '+$version);$lines.Add(('='*78))
    foreach($text in $texts){
        $content=[IO.File]::ReadAllText($text.path)
        $lines.Add('');$lines.Add('Source: '+$text.source);$lines.Add($content)
        $notices += [ordered]@{source=$text.source;sha256=(Get-FileHash -LiteralPath $text.path).Hash}
    }
    $records.Add([ordered]@{id=$id;version=$version;nugetContentHash=$package.contentHash;notices=$notices})
}
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $root 'Licenses/Desktop-ThirdParty.txt'),($lines-join"`n"),$utf8)
[IO.File]::WriteAllText((Join-Path $root 'reference/desktop-license-inventory.json'),([ordered]@{schema=1;lockSha256=(Get-FileHash -LiteralPath $lockPath).Hash;packages=$records}|ConvertTo-Json -Depth 8),$utf8)
[pscustomobject]@{packages=$records.Count;hardwareAccess=$false;networkAccess=$false}
