[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string]$Platform,
    [string]$OutputDirectory,
    [switch]$NoRestore
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rid=if($Platform -eq 'windows-x64'){'win-x64'}else{'linux-x64'}
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory="artifacts/publish/$Platform"}
$output=[IO.Path]::GetFullPath((Join-Path $project $OutputDirectory))
$work=[IO.Path]::GetFullPath((Join-Path $project "artifacts/single-file-build/$Platform"))
foreach($path in @($output,$work)){
    $relative=[IO.Path]::GetRelativePath($project,$path).Replace('\','/')
    if($relative -eq '..' -or $relative.StartsWith('../') -or [IO.Path]::IsPathRooted($relative)){throw 'Build output must remain inside the repository.'}
    if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}
}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$application=Join-Path $work 'application'
$payload=Join-Path $work 'payload'
$restore=if($NoRestore){'--no-restore'}else{$null}
& dotnet publish (Join-Path $project 'src/Briefcase.ServerManager/Briefcase.ServerManager.csproj') -c Release -r $rid $restore -o $application
if($LASTEXITCODE){throw 'The Avalonia Native AOT publication failed.'}
& (Join-Path $PSScriptRoot 'Create-Payload.ps1') -PublishDirectory $application -Platform $Platform -OutputDirectory $payload
& dotnet publish (Join-Path $project 'src/Briefcase.ServerManager.Bootstrap/Briefcase.ServerManager.Bootstrap.csproj') -c Release -r $rid $restore "-p:PayloadDirectory=$payload" -o $output
if($LASTEXITCODE){throw 'The single-file Native AOT bootstrap publication failed.'}
Remove-Item -LiteralPath $work -Recurse -Force
$output
