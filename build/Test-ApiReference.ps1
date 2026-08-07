<#
.SYNOPSIS
    Builds the API reference and checks it against S9.4 of design/30-slices.md.

.DESCRIPTION
    Builds each of the six shipped projects (gating on CS1591 as an error, per
    src/Directory.Build.props -- a public type or member without a doc comment
    fails here, before docfx ever runs), then runs docfx over build/docfx.json
    and checks that the produced site documents every publicly-visible type.

    The comparison list comes from build/tools/PublicApiLister, a throwaway
    console app that ProjectReferences all six projects and reflects over
    their built assemblies. Reflection, not the XML doc file, is the source
    of truth here: GenerateDocumentationFile writes a `<member name="T:...">`
    entry for any type that carries a doc comment, including internal types a
    contributor chose to document anyway -- so the XML file over-counts
    "public" and would make this check pass against a reference that is
    silently missing entries docfx correctly declined to publish.

.EXAMPLE
    ./build/Test-ApiReference.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$docfxJson = Join-Path $PSScriptRoot 'docfx.json'
$outputDir = Join-Path $repoRoot 'artifacts' 'api-reference'

$projects = @(
    'SubZeroDev.Platform.Abstractions'
    'SubZeroDev.Platform.Core'
    'SubZeroDev.Platform.Hosting'
    'SubZeroDev.Platform.Observability'
    'SubZeroDev.Platform.Persistence'
    'SubZeroDev.Platform.Testing'
)

foreach ($project in $projects) {
    $csproj = Join-Path $repoRoot 'src' $project "$project.csproj"

    & dotnet build $csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "'$project' failed to build -- a public member without a doc comment fails here as CS1591, before the API reference is ever generated."
    }
}

$listerProject = Join-Path $PSScriptRoot 'tools' 'PublicApiLister' 'PublicApiLister.csproj'
$publicTypeNames = @(& dotnet run --project $listerProject -c Release --no-launch-profile 2>$null | Where-Object { $_ -match '\S' })
if ($LASTEXITCODE -ne 0) {
    throw "PublicApiLister failed to run."
}
# Docfx's own file-naming convention uses '-' where reflection's FullName uses a backtick for an
# open generic arity marker (IIntegrationEventHandler`1 vs ...IIntegrationEventHandler-1).
$publicTypeNames = $publicTypeNames | ForEach-Object { $_ -replace '`', '-' }

if ($publicTypeNames.Count -eq 0) {
    throw "No public types found across the six projects -- the comparison would be vacuous."
}

if (-not (Get-Command docfx -ErrorAction SilentlyContinue)) {
    throw "docfx is not on PATH. Install it with: dotnet tool install --global docfx"
}

Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue

# No 'build' subcommand: passing one restricts docfx to the build phase alone and skips metadata
# generation, silently reusing whatever (possibly stale, possibly absent) metadata already sits in
# artifacts/api-metadata rather than regenerating it from the six projects just built above.
& docfx $docfxJson
if ($LASTEXITCODE -ne 0) {
    throw "docfx build failed -- the API reference build itself failed, per S9.4."
}

$apiDir = Join-Path $outputDir 'api'
if (-not (Test-Path -LiteralPath $apiDir)) {
    throw "docfx produced no 'api' directory at '$apiDir'."
}
# One HTML page per type is docfx's own naming convention here (build/docfx.json does not split
# classes to member-level pages), so the page set is a direct, unambiguous stand-in for "every
# public type the reference documents" -- no need to parse the rendered page content.
$documentedTypeNames = @(
    Get-ChildItem -LiteralPath $apiDir -Filter '*.html' |
        ForEach-Object { $_.BaseName }
)

$missing = New-Object System.Collections.Generic.List[string]
foreach ($typeName in $publicTypeNames) {
    if ($typeName -notin $documentedTypeNames) {
        $missing.Add($typeName)
    }
}

if ($missing.Count -gt 0) {
    throw "The API reference is missing $($missing.Count) public type(s): $($missing -join ', ')"
}

Write-Host "The API reference at '$outputDir' documents all $($publicTypeNames.Count) public type(s) across the six packages." -ForegroundColor Green
