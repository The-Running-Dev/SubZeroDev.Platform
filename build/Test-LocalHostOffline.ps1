<#
.SYNOPSIS
    Runs the local sample's executable scenarios while CI denies its process outbound networking.

.DESCRIPTION
    The caller owns the network guard and runs this script under the guarded operating-system user.
    This script migrates the built, dependency-free local sample's SQLite database in a clean
    writable directory, starts the sample in Production against it, and proves readiness, liveness,
    the local root endpoint through loopback, and the runtime setting the sample changes and reads
    back (S45.3). CI then
    inspects the guard's rejected-packet counter and fails if the sample attempted any non-loopback
    connection.

.PARAMETER Root
    Repository root. Defaults to the current directory.

.PARAMETER DataDirectory
    Clean writable directory for the isolated user's process logs and the sample's database.
#>
[CmdletBinding()]
param(
    [string] $Root = $PWD.Path,
    [string] $DataDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'platform-local-offline')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
public static class LocalHostSignal
{
    public const int SIGTERM = 15;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
}
'@

$sampleDirectory = Join-Path $Root 'samples' 'SubZeroDev.Platform.Sample.Local' 'bin' 'Release' 'net10.0'
$sampleExecutable = Join-Path $sampleDirectory 'SubZeroDev.Platform.Sample.Local'
$stdoutPath = Join-Path $DataDirectory 'sample-local.out.log'
$stderrPath = Join-Path $DataDirectory 'sample-local.err.log'
$migrateStdoutPath = Join-Path $DataDirectory 'sample-local-migrate.out.log'
$migrateStderrPath = Join-Path $DataDirectory 'sample-local-migrate.err.log'
$settingLine = 'Runtime setting Sample.Local.MaxConcurrentRuns: 8 (Global)'
$baseUri = 'http://127.0.0.1:5299'

if (-not (Test-Path -LiteralPath $sampleExecutable)) {
    throw "'$sampleExecutable' does not exist; build the Release solution before running S17.3."
}

[void](New-Item -ItemType Directory -Path $DataDirectory -Force)
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = $baseUri

# The sample's working directory is its build output, which the isolated user may not write; the
# database lives beside the logs instead.
$env:Platform__Persistence__ConnectionString = "Data Source=$(Join-Path $DataDirectory 'sample-local.db')"

function Show-Logs {
    foreach ($path in @($migrateStdoutPath, $migrateStderrPath, $stdoutPath, $stderrPath)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $content = Get-Content -LiteralPath $path -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { continue }
        Write-Host "----- $(Split-Path -Leaf $path)"
        Write-Host $content
    }
}

# Production applies no migration at startup, and a pending one fails readiness: migrate mode runs
# first, as a deployment's would.
$migrateProcess = Start-Process -FilePath $sampleExecutable -ArgumentList 'migrate' `
    -WorkingDirectory $sampleDirectory -NoNewWindow -PassThru -Wait `
    -RedirectStandardOutput $migrateStdoutPath -RedirectStandardError $migrateStderrPath
if ($migrateProcess.ExitCode -ne 0) {
    Show-Logs
    throw "The local sample's migrate mode exited $($migrateProcess.ExitCode)."
}

$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $sampleExecutable -WorkingDirectory $sampleDirectory `
        -NoNewWindow -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $ready = $false
    foreach ($attempt in 1..30) {
        if ($hostProcess.HasExited) {
            Show-Logs
            throw "The local sample exited before serving (status $($hostProcess.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri "$baseUri/health/ready" -TimeoutSec 2 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch { }

        Start-Sleep -Seconds 1
    }

    if (-not $ready) {
        Show-Logs
        throw 'The local sample did not become ready within 30 seconds.'
    }

    $liveness = Invoke-WebRequest -Uri "$baseUri/health/live" -TimeoutSec 5 -UseBasicParsing
    if ($liveness.StatusCode -ne 200) {
        throw "Local liveness returned HTTP $($liveness.StatusCode)."
    }

    $rootResponse = Invoke-WebRequest -Uri "$baseUri/" -TimeoutSec 5 -UseBasicParsing
    if ($rootResponse.StatusCode -ne 200) {
        throw "Local root returned HTTP $($rootResponse.StatusCode)."
    }

    # Printed once the host has started, which can be just after readiness first answers.
    $settingPrinted = $false
    foreach ($attempt in 1..10) {
        if ((Get-Content -LiteralPath $stdoutPath -Raw) -match [regex]::Escape($settingLine)) {
            $settingPrinted = $true
            break
        }

        Start-Sleep -Seconds 1
    }

    if (-not $settingPrinted) {
        Show-Logs
        throw "The local sample did not print '$settingLine'."
    }

    if ([LocalHostSignal]::kill($hostProcess.Id, [LocalHostSignal]::SIGTERM) -ne 0) {
        throw "SIGTERM failed for local sample process $($hostProcess.Id)."
    }

    if (-not $hostProcess.WaitForExit(15000)) {
        Show-Logs
        throw "The local sample did not exit within 15 seconds of SIGTERM."
    }

    if ($hostProcess.ExitCode -ne 0) {
        Show-Logs
        throw "The local sample exited $($hostProcess.ExitCode) after SIGTERM."
    }

    Write-Host 'The local sample served readiness, liveness and root, printed the runtime setting it changed, and shut down cleanly.'
}
finally {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        $hostProcess.Kill($true)
        $hostProcess.WaitForExit()
    }
}
