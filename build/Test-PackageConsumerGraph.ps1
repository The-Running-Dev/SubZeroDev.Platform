#Requires -Version 7.0
<#
.SYNOPSIS
    S18.1: rejects source-project fallback or a wrong version in consumer assets.
.PARAMETER AssetsFiles
    NuGet project.assets.json files for all three sample hosts and their assertion project.
.PARAMETER Version
    The lockstep version the consumer must have restored.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$AssetsFiles,
    [Parameter(Mandatory)][string]$Version
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$expected = @('Abstractions', 'Core', 'Hosting', 'Observability', 'Persistence', 'Testing',
    'Identity', 'Organizations', 'Billing', 'Licensing', 'Audit', 'Mcp')
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $AssetsFiles) {
    $assets = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $assets.libraries.GetEnumerator()) {
        $id, $resolvedVersion = $entry.Key -split '/', 2
        if (-not $id.StartsWith('SubZeroDev.Platform.', [StringComparison]::Ordinal)) { continue }
        if ($id.StartsWith('SubZeroDev.Platform.Sample.', [StringComparison]::Ordinal)) { continue }
        if ($entry.Value.type -ne 'package') {
            throw "'$file': '$id' resolved as '$($entry.Value.type)', not a package."
        }
        if ($resolvedVersion -ne $Version) {
            throw "'$file': '$id' resolved at '$resolvedVersion', expected '$Version'."
        }
        [void]$seen.Add($id)
    }
}
foreach ($name in $expected) {
    if (-not $seen.Contains("SubZeroDev.Platform.$name")) {
        throw "Consumer graph did not restore SubZeroDev.Platform.$name."
    }
}
Write-Host "All twelve Platform packages restored at $Version; no Platform source-project fallback."
