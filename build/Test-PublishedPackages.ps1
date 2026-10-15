#Requires -Version 7.0
<#
.SYNOPSIS
    Restores the packages just published and reruns the full test suite and the sample against
    them (S9.2, S9.3).

.DESCRIPTION
    The post-publish script of the release workflow's dotnet job (.github/workflows/release.yml).
    The shared dotnet workflow runs it on a fresh Linux runner after the push, with
    PACKAGE_VERSION set to the version it published and NUGET_FEED_TOKEN to a token that reads
    GitHub Packages.

    UsePlatformPackages switches the samples' and the test project's Platform references from
    ProjectReference to PackageReference (see the two samples' csproj and
    tests/SubZeroDev.Platform.Tests.csproj). Testing's own package dependencies carry Hosting,
    Persistence, Observability, Core and Abstractions transitively, so this covers every package.

    The sample then runs in both roles in a non-development environment with the outbound OTLP
    ports blocked, as build.yml runs it against project references (build/Test-SampleRoundTrip.ps1).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

foreach ($name in 'GITHUB_REPOSITORY_OWNER', 'PACKAGE_VERSION', 'NUGET_FEED_TOKEN') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set." }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'SubZeroDev.Platform.slnx'
$properties = @('-p:UsePlatformPackages=true', "-p:PlatformPackageVersion=$env:PACKAGE_VERSION")

dotnet nuget add source "https://nuget.pkg.github.com/$env:GITHUB_REPOSITORY_OWNER/index.json" `
    --name github --username $env:GITHUB_REPOSITORY_OWNER --password $env:NUGET_FEED_TOKEN `
    --store-password-in-clear-text

dotnet restore $solution @properties
dotnet build $solution --no-restore -c Release @properties
# S9.3: the full suite, not just the sample's round trip.
dotnet test $solution --no-build -c Release --verbosity normal

sudo iptables -A OUTPUT -o lo -j ACCEPT
sudo iptables -A OUTPUT -p tcp --dport 4317 -j REJECT
sudo iptables -A OUTPUT -p tcp --dport 4318 -j REJECT

$env:ASPNETCORE_ENVIRONMENT = 'Production'
& (Join-Path $PSScriptRoot 'Test-SampleRoundTrip.ps1')
exit $LASTEXITCODE
