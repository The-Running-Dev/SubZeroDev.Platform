<#
.SYNOPSIS
    Runs the local sample's executable scenarios while CI denies its process outbound networking.

.DESCRIPTION
    The caller owns the network guard and runs this script under the guarded operating-system user.
    This script migrates a fresh SQLite store, starts the built local sample in Production, and
    proves readiness, liveness and the local root endpoint through loopback. CI then inspects the
    guard's rejected-packet counter and fails if the sample attempted any non-loopback connection.

.PARAMETER Root
    Repository root. Defaults to the current directory.

.PARAMETER DataDirectory
    Writable directory for the isolated user's SQLite database and process logs.
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
$databasePath = Join-Path $DataDirectory 'sample-local-offline.db'
$stdoutPath = Join-Path $DataDirectory 'sample-local.out.log'
$stderrPath = Join-Path $DataDirectory 'sample-local.err.log'
$baseUri = 'http://127.0.0.1:5299'

if (-not (Test-Path -LiteralPath $sampleExecutable)) {
    throw "'$sampleExecutable' does not exist; build the Release solution before running S17.3."
}

[void](New-Item -ItemType Directory -Path $DataDirectory -Force)
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = $baseUri
$env:Platform__Persistence__ConnectionString = "Data Source=$databasePath"

function Show-Logs {
    foreach ($path in @($stdoutPath, $stderrPath)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $content = Get-Content -LiteralPath $path -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { continue }
        Write-Host "----- $(Split-Path -Leaf $path)"
        Write-Host $content
    }
}

$migration = Start-Process -FilePath $sampleExecutable -ArgumentList 'migrate' `
    -WorkingDirectory $sampleDirectory -NoNewWindow -PassThru -Wait `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
if ($migration.ExitCode -ne 0) {
    Show-Logs
    throw "The local sample's migrate mode exited $($migration.ExitCode)."
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

    $root = Invoke-RestMethod -Uri "$baseUri/" -TimeoutSec 5
    if ([string]::IsNullOrWhiteSpace([string]$root.correlation)) {
        throw 'The local root response carried no correlation id.'
    }

    if ([string]::IsNullOrWhiteSpace([string]$root.tenant)) {
        throw 'The local root response carried no tenant id.'
    }

    if ([LocalHostSignal]::kill($hostProcess.Id, [LocalHostSignal]::SIGTERM) -ne 0) {
        throw "SIGTERM failed for local sample process $($hostProcess.Id)."
    }

    $hostProcess.WaitForExit()
    if ($hostProcess.ExitCode -ne 0) {
        Show-Logs
        throw "The local sample exited $($hostProcess.ExitCode) after SIGTERM."
    }

    Write-Host 'The local sample migrated, served readiness, liveness and root, and shut down cleanly.'
}
finally {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        $hostProcess.Kill($true)
        $hostProcess.WaitForExit()
    }
}
