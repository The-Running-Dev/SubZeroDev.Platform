#Requires -Version 7.0
<#
.SYNOPSIS
    Packs at the release version and checks the package manifests before anything is published
    (S9.1, S9.5).

.DESCRIPTION
    The post-build script of the release workflow's dotnet job (.github/workflows/release.yml):
    build/Test-PackageManifests.ps1 at the version of the release tag.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$version = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1')
& (Join-Path $PSScriptRoot 'Test-PackageManifests.ps1') -Version $version
exit $LASTEXITCODE
