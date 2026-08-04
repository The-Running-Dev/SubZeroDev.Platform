<#
.SYNOPSIS
    Overlays the built landing page onto a built documentation site, so the
    combined tree serves the landing page at "/" and the docs at "/docs".

.DESCRIPTION
    Two independent projects, one GitHub Pages deployment. The docs build
    (docs-build.ps1) already puts the actual documentation under docs/docs/...
    -- routeBasePath: 'docs' in docs/docusaurus.config.ts sees to that -- but
    its own site root is docs/src/pages/index.md, generated from README.md.
    That generated homepage is superseded once the landing page exists: this
    script overwrites it with the landing page's own index.html and merges in
    its assets, leaving everything under docs/ inside the output untouched.

    The merge is safe because the two builds never write the same paths.
    Docusaurus nests its bundle under assets/css/ and assets/js/; Vite writes
    flat hashed files directly into assets/. Ported from
    SubZeroDev.GameEngine/build/Merge-LandingPage.ps1, where this was verified
    against a real build of both projects, not assumed.

    The guard over docs/ hashes every file's content before and after, not
    just the count -- a same-path overwrite leaves the count unchanged, so a
    count alone cannot see it.

.PARAMETER LandingDist
    Path to the built landing page (a Vite `dist/` directory).

.PARAMETER DocsOutput
    Path to the built documentation site (docs-build.ps1's -OutputPath).

.EXAMPLE
    ./build/Merge-LandingPage.ps1 -LandingDist ./site/dist -DocsOutput ./artifacts/docs
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LandingDist = 'site/dist',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$DocsOutput = 'artifacts/docs'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Get-DocsTreeFingerprint {
    <#
    .SYNOPSIS
        Relative path -> SHA-256 content hash for every file under $Path.

    .DESCRIPTION
        A file *count* before/after the merge cannot see a same-path
        overwrite -- the count is unchanged either way -- so this hashes
        content too. Cheap at documentation-site scale (tens of files), and
        it is what turns "the docs tree still has N files" into "the docs
        tree is byte-identical to before."
    #>
    param (
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fingerprint = @{}
    Get-ChildItem -LiteralPath $Path -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
        $fingerprint[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $fingerprint
}

function Assert-DocsTreeUnchanged {
    param (
        [Parameter(Mandatory)]
        [hashtable]$Before,

        [Parameter(Mandatory)]
        [hashtable]$After,

        [Parameter(Mandatory)]
        [string]$DocsSubtree
    )

    $added = @($After.Keys | Where-Object { -not $Before.ContainsKey($_) })
    $removed = @($Before.Keys | Where-Object { -not $After.ContainsKey($_) })
    $changed = @(
        $Before.Keys |
            Where-Object { $After.ContainsKey($_) -and $After[$_] -ne $Before[$_] }
    )

    if ($added.Count -eq 0 -and $removed.Count -eq 0 -and $changed.Count -eq 0) {
        return
    }

    $parts = @()
    if ($added.Count -gt 0) { $parts += "added: $($added -join ', ')" }
    if ($removed.Count -gt 0) { $parts += "removed: $($removed -join ', ')" }
    if ($changed.Count -gt 0) { $parts += "changed: $($changed -join ', ')" }

    throw (
        "The merge changed content under '$DocsSubtree' ($($parts -join '; ')). " +
        "The landing page must never write into docs/ -- aborting rather than shipping a " +
        "possibly-corrupted docs tree."
    )
}

if (-not (Test-Path -LiteralPath $LandingDist -PathType Container)) {
    throw "Landing page build not found at '$LandingDist'. Run 'npm --prefix site run build' first."
}

if (-not (Test-Path -LiteralPath $DocsOutput -PathType Container)) {
    throw "Documentation build not found at '$DocsOutput'. Run docs-build.ps1 first."
}

$docsSubtree = Join-Path $DocsOutput 'docs'
if (-not (Test-Path -LiteralPath $docsSubtree -PathType Container)) {
    throw "'$DocsOutput' does not look like a docs-build.ps1 output -- no 'docs' subdirectory found. Refusing to merge into a directory that isn't a real docs build, to avoid silently producing a broken site."
}
$docsFingerprintBefore = Get-DocsTreeFingerprint -Path $docsSubtree

$landingIndex = Join-Path $LandingDist 'index.html'
if (-not (Test-Path -LiteralPath $landingIndex -PathType Leaf)) {
    throw "'$LandingDist' has no index.html -- is this really a Vite build output?"
}

# Overwrite the docs build's generated-from-README homepage with the landing
# page. This is the one intentional collision: both projects produce a root
# index.html, and the landing page wins.
Copy-Item -LiteralPath $landingIndex -Destination (Join-Path $DocsOutput 'index.html') -Force

$landingAssets = Join-Path $LandingDist 'assets'
if (Test-Path -LiteralPath $landingAssets -PathType Container) {
    $destAssets = Join-Path $DocsOutput 'assets'
    New-Item -ItemType Directory -Path $destAssets -Force | Out-Null
    Copy-Item -Path (Join-Path $landingAssets '*') -Destination $destAssets -Recurse -Force
}

# Anything else Vite emitted at the dist root (favicons, manifest files, etc.)
# that is not index.html or assets/ -- copy it across too, without touching
# docs/.
Get-ChildItem -LiteralPath $LandingDist -Force |
    Where-Object { $_.Name -notin @('index.html', 'assets') } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DocsOutput -Recurse -Force
    }

$docsFingerprintAfter = Get-DocsTreeFingerprint -Path $docsSubtree
Assert-DocsTreeUnchanged -Before $docsFingerprintBefore -After $docsFingerprintAfter -DocsSubtree $docsSubtree

$roadmapPage = Join-Path $DocsOutput 'roadmap/index.html'
if (-not (Test-Path -LiteralPath $roadmapPage -PathType Leaf)) {
    throw "The landing build did not leave a static roadmap route at '$roadmapPage'."
}

Write-Host "[MERGE] Landing page overlaid onto '$DocsOutput'. docs/ untouched ($($docsFingerprintAfter.Count) files, content-verified)." -ForegroundColor Green
