[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$version=(Get-Content -LiteralPath (Join-Path $project 'VERSION') -Raw).Trim()
$executable=if($Platform -eq 'windows-x64'){'Briefcase.ServerManager.App.exe'}else{'Briefcase.ServerManager.App'}
if(-not(Test-Path -LiteralPath (Join-Path $publish $executable) -PathType Leaf)){throw 'Published manager application is missing.'}
if(Test-Path -LiteralPath $output){Remove-Item -LiteralPath $output -Recurse -Force}
New-Item -ItemType Directory -Path $output|Out-Null
$records=@()
foreach($file in @(Get-ChildItem -LiteralPath $publish -File|Where-Object {$_.Extension -notin @('.pdb','.dbg')})){
    Copy-Item -LiteralPath $file.FullName -Destination $output
    $records+=[ordered]@{name=$file.Name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
if($records.Count -lt 3){throw 'The Avalonia native payload is incomplete.'}
$identity=($records|Sort-Object name|ForEach-Object {$_.name+':'+$_.sha256}) -join "`n"
$identityBytes=[Text.Encoding]::UTF8.GetBytes($identity)
$identityHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identityBytes)).ToLowerInvariant()
[ordered]@{schema=1;version=$version;platform=$Platform;executable=$executable;payloadSha256=$identityHash;files=$records}|
    ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'payload.manifest.json') -Encoding utf8NoBOM
$output
