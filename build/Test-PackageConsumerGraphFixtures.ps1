#Requires -Version 7.0
# Proves the assets check rejects source fallback, version drift and a missing module.
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "platform-graph-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $version = '0.1.0-fixture'
    $libraries = @{}
    foreach ($name in @('Abstractions', 'Core', 'Hosting', 'Observability', 'Persistence', 'Testing',
            'Identity', 'Organizations', 'Billing', 'Licensing', 'Audit', 'Mcp')) {
        $libraries["SubZeroDev.Platform.$name/$version"] = @{ type = 'package' }
    }
    # Source references between consumer projects are legitimate.
    $libraries['SubZeroDev.Platform.Sample.Web/1.0.0'] = @{ type = 'project' }
    $valid = @{ libraries = $libraries } | ConvertTo-Json -Depth 5
    $file = Join-Path $scratch 'project.assets.json'
    $valid | Set-Content -LiteralPath $file
    $validator = Join-Path $PSScriptRoot 'Test-PackageConsumerGraph.ps1'
    & $validator -AssetsFiles $file -Version $version

    foreach ($mutation in @('source', 'version', 'missing')) {
        $assets = $valid | ConvertFrom-Json -AsHashtable
        $key = "SubZeroDev.Platform.Mcp/$version"
        switch ($mutation) {
            'source' { $assets.libraries[$key].type = 'project'; $expected = "resolved as 'project', not a package" }
            'version' {
                $assets.libraries.Remove($key)
                $assets.libraries['SubZeroDev.Platform.Mcp/0.2.0'] = @{ type = 'package' }
                $expected = "resolved at '0.2.0', expected '$version'"
            }
            'missing' { $assets.libraries.Remove($key); $expected = 'Consumer graph did not restore SubZeroDev.Platform.Mcp.' }
        }
        $assets | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $file
        $failure = $null
        try { & $validator -AssetsFiles $file -Version $version }
        catch { $failure = $_.Exception.Message }
        if (-not $failure -or -not $failure.Contains($expected)) {
            throw "Mutation '$mutation' did not produce its expected rejection: $failure"
        }
    }
    Write-Host 'Package graph fixtures passed: valid consumer accepted; all three mutations rejected.'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
