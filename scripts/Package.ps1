[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string]$Platform,
    [string]$OutputDirectory = 'artifacts/release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath((Join-Path $project $OutputDirectory))
$version=(Get-Content -LiteralPath (Join-Path $project 'VERSION') -Raw).Trim()
if($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'){throw 'VERSION must contain major.minor.patch.'}
if(-not(Test-Path -LiteralPath $publish -PathType Container)){throw 'Publish directory is missing.'}
$executable=if($Platform -eq 'windows-x64'){'Briefcase.ServerManager.exe'}else{'Briefcase.ServerManager'}
if(-not(Test-Path -LiteralPath (Join-Path $publish $executable) -PathType Leaf)){throw 'Published manager is missing.'}
$files=@(Get-ChildItem -LiteralPath $publish -File|Where-Object {$_.Extension -ne '.pdb'})
if(-not $files -or @($files|Where-Object {$_.Extension -in @('.json','.config')}).Count){throw 'Unexpected framework-dependent publication.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$stage=Join-Path $project ('artifacts/package-'+$Platform)
if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
New-Item -ItemType Directory -Path $stage|Out-Null
foreach($file in $files){Copy-Item -LiteralPath $file.FullName -Destination $stage}
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination $stage
$archive=Join-Path $output "Briefcase.ServerManager-$Platform-$version.zip"
if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive -Force}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force
$archive
