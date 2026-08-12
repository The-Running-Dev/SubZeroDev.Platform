<#
.SYNOPSIS
    Fails any project under src/ or samples/ that references anything under
    workloads/: S3.14 of design/g1/30-slices.md, invariant 45 of
    design/g1/20-contract.md.

.DESCRIPTION
    G1's game service is a hosted product workload, not part of the framework
    this repository is (ADR-001). It lives under workloads/ -- a top-level tree
    outside src/ -- precisely so the product/framework boundary stays auditable
    at a glance (design/g1/90-decisions.md, 2026-08-08).

    The direction is what matters. A workload may consume the framework's
    packages; the framework may never consume a workload, because a framework
    that depends on one of its own consumers cannot be extracted, versioned or
    reasoned about separately. Nothing in MSBuild or npm enforces that on its
    own, so this script is the enforcement.

    Every project file under src/ and samples/ is read and every path-valued
    reference it carries -- ProjectReference, Import, None/Content/Compile
    Include or Update -- is resolved against the project's own directory and
    compared to workloads/. A reference that resolves inside workloads/ fails
    the build naming the project, the reference and the offending path.

    Package references are checked by name as well: a workload published under
    an @subzerodev or SubZeroDev.*.Workload identity would evade a path check
    entirely, and the point is the dependency direction rather than the
    mechanism that expresses it.

.EXAMPLE
    ./build/Test-WorkloadIsolation.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workloadsRoot = Join-Path $repositoryRoot 'workloads'

if (-not (Test-Path -LiteralPath $workloadsRoot)) {
    Write-Host 'No workloads/ tree present; nothing for this gate to check.' -ForegroundColor Yellow
    exit 0
}

$workloadsFull = (Resolve-Path -LiteralPath $workloadsRoot).Path
$scanned = 0
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($tree in @('src', 'samples')) {
    $treeRoot = Join-Path $repositoryRoot $tree
    if (-not (Test-Path -LiteralPath $treeRoot)) { continue }

    $projects = Get-ChildItem -LiteralPath $treeRoot -Recurse -File -Include '*.csproj', '*.props', '*.targets', 'package.json'

    foreach ($project in $projects) {
        $scanned++
        $projectDirectory = $project.DirectoryName

        if ($project.Name -eq 'package.json') {
            $manifest = Get-Content -LiteralPath $project.FullName -Raw | ConvertFrom-Json
            foreach ($section in @('dependencies', 'devDependencies')) {
                if (-not $manifest.PSObject.Properties.Name.Contains($section)) { continue }
                foreach ($entry in $manifest.$section.PSObject.Properties) {
                    # A workload published under its own package name has no path to resolve; it is
                    # checked by identity, the same reasoning `PackageReference` gets below.
                    if ($entry.Name -match '^(@subzerodev/|SubZeroDev\..*\.Workload)') {
                        $violations.Add("$($project.FullName): dependency '$($entry.Name)' names a workload package.")
                    }
                    if ($entry.Value -is [string] -and $entry.Value -match 'workloads[/\\]') {
                        $violations.Add("$($project.FullName): dependency '$($entry.Name)' resolves into workloads/ ('$($entry.Value)').")
                    }
                }
            }
            continue
        }

        [xml]$document = Get-Content -LiteralPath $project.FullName -Raw

        $referenceNodes = $document.SelectNodes('//*[@Include or @Update or @Project]')
        foreach ($node in $referenceNodes) {
            foreach ($attribute in @('Include', 'Update', 'Project')) {
                $value = $node.GetAttribute($attribute)
                if ([string]::IsNullOrWhiteSpace($value)) { continue }

                # A package reference has no path to resolve; it is checked by identity instead.
                if ($node.LocalName -eq 'PackageReference') {
                    if ($value -match '^(@subzerodev/|SubZeroDev\..*\.Workload)') {
                        $violations.Add("$($project.FullName): PackageReference '$value' names a workload package.")
                    }
                    continue
                }

                if ($value -notmatch '[/\\]') { continue }

                $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projectDirectory, $value))
                $workloadsPrefix = $workloadsFull + [System.IO.Path]::DirectorySeparatorChar
                $insideWorkloads = ($candidate -eq $workloadsFull) -or
                    $candidate.StartsWith($workloadsPrefix, [System.StringComparison]::OrdinalIgnoreCase)
                if ($insideWorkloads) {
                    $violations.Add("$($project.FullName): <$($node.LocalName) $attribute=""$value""> resolves into workloads/ ('$candidate').")
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'Framework code references a workload. The dependency direction is one-way:' -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host "  $violation" -ForegroundColor Red
    }
    throw "WorkloadReferenceFromFramework: $($violations.Count) reference(s) from src/ or samples/ into workloads/."
}

Write-Host "No project under src/ or samples/ references workloads/ ($scanned project file(s) checked)." -ForegroundColor Green
