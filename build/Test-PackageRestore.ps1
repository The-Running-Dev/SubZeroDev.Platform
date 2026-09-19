#Requires -Version 7.0
<#
.SYNOPSIS
    Restores, builds and tests the S18 sample solution against the CI package artifacts.
.PARAMETER PackageDirectory
    Directory containing the twelve checked nupkg artifacts.
.PARAMETER Version
    The shared 0.x version produced by Test-PackageManifests.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^0\.')][string]$Version
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "platform-consumer-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $scratch | Out-Null
$solution = Join-Path $repoRoot 'samples/PackageConsumers.slnx'
$properties = @('-p:UsePlatformPackages=true', "-p:PlatformPackageVersion=$Version")
try {
    # A fresh cache plus source mapping makes the local artifacts the only possible source
    # for Platform, even if this version was previously published to a remote feed.
    $config = Join-Path $scratch 'NuGet.Config'
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    @"
<configuration>
  <packageSources><clear />
    <add key="artifacts" value="$escapedFeed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="artifacts"><package pattern="SubZeroDev.Platform.*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config
    & dotnet restore $solution @properties --configfile $config --packages (Join-Path $scratch 'packages') --force --no-http-cache
    if ($LASTEXITCODE -ne 0) { throw "Consumer restore failed ($LASTEXITCODE)." }

    $assets = @('samples/SubZeroDev.Platform.Sample.Local', 'samples/SubZeroDev.Platform.Sample.Web',
        'samples/SubZeroDev.Platform.Sample.Worker', 'tests/SubZeroDev.Platform.Tests') |
        ForEach-Object { Join-Path $repoRoot "$_/obj/project.assets.json" }
    & (Join-Path $PSScriptRoot 'Test-PackageConsumerGraph.ps1') -AssetsFiles $assets -Version $Version

    & dotnet build $solution @properties --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw "Consumer build failed ($LASTEXITCODE)." }
    & dotnet test $solution @properties --no-build --no-restore -c Release --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "Consumer tests failed ($LASTEXITCODE)." }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
