<#
.SYNOPSIS
    Runs both sample hosts against one shared store and proves the brief's first and third CI
    assertions.

.DESCRIPTION
    Starts the web and worker samples against a single SQLite store in a non-Development
    environment, and proves S1's health and readiness, S3's peer registration, and S5.3's
    kill-and-restart delivery. Shared by two callers: .github/workflows/build.yml (against project
    references) and .github/workflows/release.yml's verify-restore job (against the packages just
    published to GitHub Packages, per S9.2/S9.3) -- one script rather than two near-identical
    copies drifting apart.

    Assumes both samples are already built at samples/*/bin/Release/net10.0 (via `dotnet build -c
    Release`, with whichever MSBuild properties the caller needs) and that
    ASPNETCORE_ENVIRONMENT is set by the caller.

    Linux-only, and it always was: it signals processes and reads the store with sqlite3. Two
    things are worth knowing before editing it.

    SIGTERM has no PowerShell or .NET expression. `Stop-Process` and `Process.Kill()` both send
    SIGKILL on Unix, and the difference between the two signals is the whole point of this script
    -- a graceful worker exits zero on SIGTERM, and the web host is SIGKILLed precisely so that
    nothing in its memory can bridge the commit to the dispatch. So both signals go through a
    P/Invoke to libc's kill(2), which keeps them symmetrical and readable side by side instead of
    sending one through .NET and the other through a shell.

    sqlite3 is still required, and processed_at is still read from the table rather than grepped
    from a log, for the reason recorded at that step.

.PARAMETER Root
    Repository root. Defaults to the current directory.

.PARAMETER DatabaseFile
    Name of the shared SQLite file, created beneath Root. Defaults to shared-sample.db.

.EXAMPLE
    ./build/Test-SampleRoundTrip.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = $PWD.Path,
    [string] $DatabaseFile = 'shared-sample.db'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# The constants are SIGTERM and SIGKILL, not Term and Kill, and that is load-bearing. PowerShell
# resolves members case-insensitively, so a `Kill` constant shadows the `kill` method on the same
# type: every [LibcSignal]::kill(...) call then fails with "does not contain a method named 'kill'"
# even though reflection shows the method present. Keep the constant names distinct from the
# method name under case folding, or this script dies at its first signal.
Add-Type -TypeDefinition @'
public static class LibcSignal
{
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
}
'@

$webDirectory = Join-Path $Root 'samples' 'SubZeroDev.Platform.Sample.Web' 'bin' 'Release' 'net10.0'
$workerDirectory = Join-Path $Root 'samples' 'SubZeroDev.Platform.Sample.Worker' 'bin' 'Release' 'net10.0'
$webExecutable = Join-Path $webDirectory 'SubZeroDev.Platform.Sample.Web'
$workerExecutable = Join-Path $workerDirectory 'SubZeroDev.Platform.Sample.Worker'
$databasePath = Join-Path $Root $DatabaseFile

$webBaseUri = 'http://127.0.0.1:5199'
$workerBaseUri = 'http://127.0.0.1:5100'

# Every stream this script redirects, so a failure can dump all of them. Start-Process refuses to
# point stdout and stderr at one file, so each stream gets its own -- the shell script's `2>&1`
# has no Start-Process equivalent.
$logFiles = @(
    'web.out.log', 'web.err.log'
    'worker.out.log', 'worker.err.log'
    'worker-restart.out.log', 'worker-restart.err.log'
) | ForEach-Object { Join-Path $Root $_ }

$web = $null
$worker = $null

function Stop-Quietly {
    param([System.Diagnostics.Process] $Process)

    if ($null -eq $Process -or $Process.HasExited) { return }
    try { [void][LibcSignal]::kill($Process.Id, [LibcSignal]::SIGKILL) } catch { }
}

function Assert-Ok {
    param([string] $Message)

    Write-Host "::error::$Message"

    foreach ($logFile in $logFiles) {
        if (-not (Test-Path -LiteralPath $logFile)) { continue }
        $content = Get-Content -LiteralPath $logFile -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { continue }
        Write-Host "----- $(Split-Path -Leaf $logFile)"
        Write-Host $content
    }

    Stop-Quietly $web
    Stop-Quietly $worker
    exit 1
}

function Send-Signal {
    param(
        [System.Diagnostics.Process] $Process,
        [int] $Signal
    )

    if ([LibcSignal]::kill($Process.Id, $Signal) -ne 0) {
        Assert-Ok "kill($($Process.Id), $Signal) failed."
    }
}

function Wait-ForExitStatus {
    param([System.Diagnostics.Process] $Process)

    $Process.WaitForExit()
    return $Process.ExitCode
}

function Test-Probe {
    param([string] $Uri)

    try {
        $null = Invoke-WebRequest -Uri $Uri -TimeoutSec 5 -UseBasicParsing
        return $true
    }
    catch {
        # Readiness returns 200 for Healthy and Degraded and 503 only for Unhealthy, so a throw
        # here means not-ready or not-listening -- the same verdict `curl -fsS` gave.
        return $false
    }
}

# Both roles point at one store, outside either's own output directory -- the only way PeerHost and
# SettingsFingerprint see anything besides "no peer ever registered here".
$env:Platform__Persistence__ConnectionString = "Data Source=$databasePath"

# Migrate mode creates the shared store and puts its SQLite file in WAL -- a host that finds a file
# still in the default journal mode aborts startup, per the contract, and a store with no schema
# has nothing for the standard call's checks to be healthy against.
foreach ($migration in @(
        @{ Executable = $webExecutable; Directory = $webDirectory }
        @{ Executable = $workerExecutable; Directory = $workerDirectory }
    )) {
    $migrate = Start-Process -FilePath $migration.Executable -ArgumentList 'migrate' `
        -WorkingDirectory $migration.Directory -NoNewWindow -PassThru -Wait
    if ($migrate.ExitCode -ne 0) {
        Assert-Ok "migrate mode exited $($migrate.ExitCode) for '$($migration.Executable)'."
    }
}

# The built executables rather than `dotnet run`, so the process signalled at the end is the host
# itself and its exit status is the host's. Each runs from its own output directory, because
# appsettings.json sits beside the binary and the content root defaults to the working directory.
# ASPNETCORE_URLS is set for the web host alone and removed before the worker starts -- the worker
# binds its own probe port from Hosting:WorkerProbePort, and must not inherit the web host's.
$env:ASPNETCORE_URLS = $webBaseUri
$web = Start-Process -FilePath $webExecutable -WorkingDirectory $webDirectory -NoNewWindow -PassThru `
    -RedirectStandardOutput (Join-Path $Root 'web.out.log') `
    -RedirectStandardError (Join-Path $Root 'web.err.log')
Remove-Item Env:ASPNETCORE_URLS

$worker = Start-Process -FilePath $workerExecutable -WorkingDirectory $workerDirectory -NoNewWindow -PassThru `
    -RedirectStandardOutput (Join-Path $Root 'worker.out.log') `
    -RedirectStandardError (Join-Path $Root 'worker.err.log')

$ready = $false
foreach ($attempt in 1..30) {
    if ((Test-Probe "$webBaseUri/health/ready") -and (Test-Probe "$workerBaseUri/health/ready")) {
        $ready = $true
        break
    }

    if ($web.HasExited) { Assert-Ok 'the web host exited before serving' }
    if ($worker.HasExited) { Assert-Ok 'the worker host exited before serving' }
    Start-Sleep -Seconds 1
}

if (-not $ready) { Assert-Ok 'neither probe answered within 30 seconds' }

if (-not (Test-Probe "$webBaseUri/health/live")) { Assert-Ok 'web liveness did not answer' }

# The worker probe is loopback-only, and its readiness answered above. Its absence from the
# machine's routable address is asserted in the test suite rather than here.
Write-Host "Both roles served their probes in $env:ASPNETCORE_ENVIRONMENT."

# Stop dispatch before committing the order. The web process is then killed rather than shut down,
# proving the durable row -- not process memory -- bridges commit to delivery.
Send-Signal $worker ([LibcSignal]::SIGTERM)
$workerStatus = Wait-ForExitStatus $worker
if ($workerStatus -ne 0) { Assert-Ok "the first worker exited with status $workerStatus" }

try {
    $order = Invoke-RestMethod -Uri "$webBaseUri/orders" -Method Post -TimeoutSec 30 `
        -ContentType 'application/json' `
        -Body '{"name":"restart-survivor","quantity":1}'
}
catch {
    Assert-Ok "the sample order did not commit: $($_.Exception.Message)"
}

# Invoke-RestMethod parses the response, which is what retires the shell script's `python3 -c
# json.load` hop -- one fewer thing the runner has to have.
$orderId = $order.orderId
if ([string]::IsNullOrWhiteSpace($orderId)) { Assert-Ok 'the committed order response carried no id' }

Send-Signal $web ([LibcSignal]::SIGKILL)
$webStatus = Wait-ForExitStatus $web
if ($webStatus -eq 0) { Assert-Ok 'the web process was expected to be killed' }

$worker = Start-Process -FilePath $workerExecutable -WorkingDirectory $workerDirectory -NoNewWindow -PassThru `
    -RedirectStandardOutput (Join-Path $Root 'worker-restart.out.log') `
    -RedirectStandardError (Join-Path $Root 'worker-restart.err.log')

# Queried from the outbox table itself rather than grepped from the restart log: the log line is
# real evidence of dispatch, but it reaches that file through Serilog's async sink (WriteTo.Async,
# by design non-blocking per S8) and then through this process's redirected stdout, so its on-disk
# appearance can lag well behind the dispatch it reports. processed_at is written by the same
# transaction that runs the handler, so it is set the instant delivery actually commits.
$query = "SELECT processed_at FROM platform_outbox WHERE type = 'sample.order-placed' " +
    "AND json_extract(payload, '`$.orderId') = '$orderId';"

$delivered = $false
foreach ($attempt in 1..30) {
    $processedAt = & sqlite3 $databasePath $query
    if ($LASTEXITCODE -ne 0) { Assert-Ok 'sqlite3 could not read the outbox table' }

    if (-not [string]::IsNullOrWhiteSpace($processedAt)) {
        $delivered = $true
        break
    }

    if ($worker.HasExited) { Assert-Ok 'the restarted worker exited before dispatch' }
    Start-Sleep -Seconds 1
}

if (-not $delivered) { Assert-Ok 'the committed outbox row was not delivered after restart' }

Write-Host "The outbox row survived process death and dispatched order $orderId after restart."

# SIGTERM is the ordinary shutdown signal, and a graceful worker exits zero on it.
Send-Signal $worker ([LibcSignal]::SIGTERM)
$workerStatus = Wait-ForExitStatus $worker
if ($workerStatus -ne 0) { Assert-Ok "the restarted worker exited with status $workerStatus" }
