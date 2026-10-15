#Requires -Version 7.0
<#
.SYNOPSIS
    Cuts the GitHub release of a version tag, with the packages and the API reference attached
    (S9.6).

.DESCRIPTION
    The last step of the release workflow (.github/workflows/release.yml), after the packages are
    published and verified. The notes record the API as explicitly unstable. Needs GH_TOKEN.
#>
[CmdletBinding()]
param(
    # Folder with the published .nupkg files.
    [Parameter(Mandatory)][string]$PackageDirectory,
    # Folder with the built API reference, attached as api-reference.zip.
    [Parameter(Mandatory)][string]$ApiReferenceDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$version = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1')
$tag = "v$version"

$archive = Join-Path ([IO.Path]::GetTempPath()) 'api-reference.zip'
Compress-Archive -Path (Join-Path $ApiReferenceDirectory '*') -DestinationPath $archive -Force

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' | ForEach-Object FullName)
if (-not $packages) { throw "No packages in '$PackageDirectory'." }

$notes = "SubZeroDev.Platform $version. The API is pre-1.0 and explicitly unstable: breaking changes are permitted between releases and are recorded here, per the brief's Lifespan reconciliation."
gh release create $tag @packages $archive --title $tag --notes $notes --repo $env:GITHUB_REPOSITORY
