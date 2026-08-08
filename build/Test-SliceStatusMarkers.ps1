<#
.SYNOPSIS
    Validates the internal consistency of design/30-slices.md's per-slice
    Status markers -- the active effort's slice ledger. The public site's
    roadmap currently renders the archived design/d3/30-slices.md; issue #80
    tracks pointing it back at the active effort.

.DESCRIPTION
    A separate script rather than an addition to Test-Documentation.ps1
    deliberately: that file is installed byte-identical from
    ghcr.io/the-running-dev/docs-template (see design/d3/90-decisions.md), and
    this repository's own established practice is not to hand-edit installed
    template files, so that re-running the installer keeps picking up
    upstream fixes. This script is repository-owned and runs alongside
    Test-Documentation.ps1 in CI instead.

    Checks every '## S<n> — <title>' heading for a '**Status:** shipped|in
    progress|queued' line immediately in its body, then the same invariants
    site/src/roadmap/roadmapData.ts's assertConsistent enforces at import
    time: at most one slice 'in progress'; at least one 'in progress' if any
    slice is 'queued'; and no 'shipped' slice ordered after a 'queued' one.
    Both the parser and this script exist because the roadmap's own build can
    only fail the site's build -- this is what fails the documentation gate
    on a pull request that merges a slice without updating its marker.

.PARAMETER Path
    The slices document to check. Defaults to design/30-slices.md relative to
    this script's location.

.EXAMPLE
    ./build/Test-SliceStatusMarkers.ps1
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Path
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if (-not $PSBoundParameters.ContainsKey('Path')) {
    $Path = Join-Path $PSScriptRoot '..' 'design' '30-slices.md'
}

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw [System.IO.FileNotFoundException]::new("Slices document not found: '$Path'.")
}

$text = [IO.File]::ReadAllText($Path) -replace "`r`n?", "`n"

$headingMatches = [regex]::Matches($text, '(?m)^## (.+)$')
if ($headingMatches.Count -eq 0) {
    throw "'$Path': no '## ' headings found -- is this the right document?"
}

$slices = @()
for ($i = 0; $i -lt $headingMatches.Count; $i++) {
    $headingText = $headingMatches[$i].Groups[1].Value
    $sliceMatch = [regex]::Match($headingText, '^(S(\d+)) — (.+)$')
    if (-not $sliceMatch.Success) { continue }

    $id = $sliceMatch.Groups[1].Value
    $contentStart = $headingMatches[$i].Index + $headingMatches[$i].Length
    $contentEnd = if ($i + 1 -lt $headingMatches.Count) { $headingMatches[$i + 1].Index } else { $text.Length }
    $body = $text.Substring($contentStart, $contentEnd - $contentStart)

    $statusMatch = [regex]::Match($body, '(?m)^\*\*Status:\*\*\s*(.+)$')
    if (-not $statusMatch.Success) {
        throw "'$Path': $id has no '**Status:**' line."
    }
    $statusText = $statusMatch.Groups[1].Value.Trim()

    $status = $null
    if ($statusText.StartsWith('shipped')) { $status = 'shipped' }
    elseif ($statusText.StartsWith('in progress')) { $status = 'in-progress' }
    elseif ($statusText.StartsWith('queued')) { $status = 'queued' }
    else {
        throw "'$Path': ${id}: unrecognised status '$statusText' -- expected 'shipped', 'in progress', or 'queued'."
    }

    $slices += [pscustomobject]@{ Id = $id; Status = $status }
}

if ($slices.Count -eq 0) {
    throw "'$Path': no 'S<n> — ' slice headings found among its '## ' headings."
}

$inProgress = @($slices | Where-Object Status -eq 'in-progress')
$hasQueued = [bool]($slices | Where-Object Status -eq 'queued')

if ($inProgress.Count -gt 1) {
    $ids = ($inProgress | ForEach-Object Id) -join ', '
    throw "'$Path': more than one slice marked 'in progress': $ids."
}

if ($inProgress.Count -eq 0 -and $hasQueued) {
    throw "'$Path': no slice is marked 'in progress' while a 'queued' slice exists."
}

$seenQueued = $false
foreach ($slice in $slices) {
    if ($slice.Status -eq 'queued') { $seenQueued = $true }
    if ($slice.Status -eq 'shipped' -and $seenQueued) {
        throw "'$Path': $($slice.Id) is 'shipped' but ordered after a 'queued' slice."
    }
}

Write-Host "Slice status markers consistent across $($slices.Count) slice(s) in '$Path'." -ForegroundColor Green
