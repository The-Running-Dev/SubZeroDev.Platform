<#
.SYNOPSIS
    Proves that every S17.5 negative mutation is rejected by its capability scenario.

.DESCRIPTION
    Reads the nine declared fixtures, activates them one at a time, and runs exactly the matching
    SQLite capability scenario. A fixture counts as detected only when dotnet test reports exactly
    one failed test and that failure carries the fixture's S17.5 marker. Build failures, crashes,
    missing tests and unrelated assertion failures therefore fail this gate instead of masquerading
    as successful mutation detection.

.PARAMETER Root
    Repository root. Defaults to the current directory.
#>
[CmdletBinding()]
param([string] $Root = $PWD.Path)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$fixturePath = Join-Path $Root 'tests' 'SubZeroDev.Platform.Tests' 'S17MutationFixtures.json'
$testProject = Join-Path $Root 'tests' 'SubZeroDev.Platform.Tests' 'SubZeroDev.Platform.Tests.csproj'
$fixtures = @(Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json)

if ($fixtures.Count -ne 9) {
    throw "S17.5 requires nine mutation fixtures; '$fixturePath' declares $($fixtures.Count)."
}

$escaped = [System.Collections.Generic.List[string]]::new()

foreach ($fixture in $fixtures) {
    $capability = [string]$fixture.capability
    $results = Join-Path ([System.IO.Path]::GetTempPath()) "platform-s17-mutation-$capability-$([Guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory -Path $results)

    try {
        $env:PLATFORM_S17_MUTATION = $capability
        $arguments = @(
            'test', $testProject,
            '--no-build', '--no-restore', '--configuration', 'Release',
            '--filter', "FullyQualifiedName~SqliteOperatedScenarioTests&Capability=$capability",
            '--results-directory', $results,
            '--logger', 'trx;LogFileName=mutation.trx',
            '--verbosity', 'quiet'
        )

        $testProcess = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -NoNewWindow -Wait -PassThru
        $testStatus = $testProcess.ExitCode

        $trxPath = Join-Path $results 'mutation.trx'
        if (-not (Test-Path -LiteralPath $trxPath)) {
            $escaped.Add("${capability}: dotnet test produced no TRX result")
            continue
        }

        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $testResults = @($trx.TestRun.Results.UnitTestResult)
        $marker = "[S17.5:$capability]"
        $outcome = ''
        $message = ''
        if ($testResults.Count -gt 0) {
            $outcome = [string]$testResults[0].outcome
            $message = [string]$testResults[0].Output.ErrorInfo.Message
        }
        $hasMarker = $message.Contains($marker, [StringComparison]::Ordinal)

        $mutationEscaped = ($testStatus -eq 0) `
            -or ($testResults.Count -ne 1) `
            -or ($outcome -ne 'Failed') `
            -or (-not $hasMarker)
        if ($mutationEscaped) {
            $escaped.Add(
                "${capability}: expected exactly one marked scenario failure; exit=$testStatus, " +
                "results=$($testResults.Count), outcome=$outcome, marker=$hasMarker")
            continue
        }

        Write-Host "::notice::S17.5 detected the $capability mutation."
    }
    finally {
        Remove-Item Env:PLATFORM_S17_MUTATION -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $results -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($escaped.Count -gt 0) {
    foreach ($failure in $escaped) {
        Write-Host "::error::$failure"
    }

    exit 1
}

Write-Host 'All nine S17.5 negative mutations failed at their capability assertions.'
