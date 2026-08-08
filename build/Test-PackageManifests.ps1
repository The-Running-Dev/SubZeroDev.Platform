<#
.SYNOPSIS
    Validates the six shipped packages' manifests: S9.1, S9.5 and S9.6 of
    design/d3/30-slices.md.

.DESCRIPTION
    Packs SubZeroDev.Platform.slnx to a scratch directory and checks the
    produced .nupkg set rather than trusting the build to have produced the
    right thing implicitly:

    - Exactly the six shipped packages are produced (S9.1) -- neither sample
      project is packable, so a passing `dotnet pack` over the solution
      already proves this; this script asserts it rather than assuming it.
    - Every package's version is 0.x (S9.6) -- the brief's stated
      reconciliation of "third parties compile against this" against
      "the API stays unstable" is the 0.x major version itself, so a 1.x (or
      later) package here would be the release silently making a promise
      Lifespan has not authorised.
    - No package's dependency list names SubZeroDev.Platform.Testing (S9.5)
      -- Testing exists to write tests against the other five, and a shipped
      dependency on it would carry Testing's own dependencies (xunit,
      Testcontainers, ...) into every consumer's production restore.

    Doc-comment completeness (the other half of S9.1) is not re-checked here:
    src/Directory.Build.props already sets GenerateDocumentationFile and the
    root Directory.Build.props sets TreatWarningsAsErrors, so a public member
    without a doc comment fails the `dotnet pack` this script performs with
    CS1591 before this script's own assertions ever run.

.PARAMETER Version
    The package version to pack. Defaults to the version Directory.Build.props
    already carries (VersionPrefix), so a plain local run checks exactly what
    an unversioned build would produce.

.EXAMPLE
    ./build/Test-PackageManifests.ps1

.EXAMPLE
    ./build/Test-PackageManifests.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solution = Join-Path $repoRoot 'SubZeroDev.Platform.slnx'

$expectedPackages = @(
    'SubZeroDev.Platform.Abstractions'
    'SubZeroDev.Platform.Core'
    'SubZeroDev.Platform.Hosting'
    'SubZeroDev.Platform.Observability'
    'SubZeroDev.Platform.Persistence'
    'SubZeroDev.Platform.Testing'
)

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "szdfp-pack-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $scratch | Out-Null

try {
    $packArgs = @($solution, '-c', 'Release', '-o', $scratch)
    if ($PSBoundParameters.ContainsKey('Version')) {
        $packArgs += "-p:Version=$Version"
    }

    & dotnet pack @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack exited $LASTEXITCODE."
    }

    $produced = Get-ChildItem -LiteralPath $scratch -Filter '*.nupkg' | Sort-Object Name
    $producedIds = @($produced | ForEach-Object { $_.BaseName -replace '\.\d+\.\d+\.\d+.*$', '' })

    $missing = @($expectedPackages | Where-Object { $_ -notin $producedIds })
    if ($missing.Count -gt 0) {
        throw "Missing package(s): $($missing -join ', ')."
    }

    $extra = @($producedIds | Where-Object { $_ -notin $expectedPackages })
    if ($extra.Count -gt 0) {
        throw "Unexpected package(s) produced: $($extra -join ', ')."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($package in $produced) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
            if (-not $nuspecEntry) {
                throw "'$($package.Name)': no .nuspec entry found."
            }

            $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
            try {
                [xml]$nuspec = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }

            $metadata = $nuspec.package.metadata
            $packageVersion = $metadata.version
            if ($packageVersion -notmatch '^0\.') {
                throw "'$($package.Name)': version '$packageVersion' is not 0.x."
            }

            $dependencyIds = @($nuspec.SelectNodes('//*[local-name()="dependency"]') | ForEach-Object { $_.id })
            if ('SubZeroDev.Platform.Testing' -in $dependencyIds) {
                throw "'$($package.Name)': declares a dependency on SubZeroDev.Platform.Testing."
            }

            $xmlDocEntry = $zip.Entries | Where-Object { $_.FullName -like 'lib/*/*.xml' } | Select-Object -First 1
            if (-not $xmlDocEntry) {
                throw "'$($package.Name)': carries no doc-comment XML."
            }
        }
        finally {
            $zip.Dispose()
        }
    }

    Write-Host "All six package manifests are consistent with S9.1, S9.5 and S9.6." -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
