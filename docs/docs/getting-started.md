---
title: Getting Started
sidebar_label: Getting Started (D3)
sidebar_position: 0.5
---

# Getting Started

Platform is a framework of packages you build on, not a service you deploy: your product is an
ordinary ASP.NET Core application that references them, and Platform contributes the hosting shape,
configuration validation, observability, persistence and test fakes it would otherwise re-derive.
What the repository is, and what it is not, is settled in
[Platform Identity](platform-identity.md); this page is the shortest path from an empty project to a
running host.

## Packages and the feed

All .NET packages share one explicitly unstable 0.x version (`0.1.0` today); breaking changes are
permitted between releases, and the version in `src/Directory.Build.props` is the single source. The
six framework packages are `SubZeroDev.Platform.` followed by Abstractions, Core, Hosting,
Observability, Persistence and Testing. Their boundaries and done-criteria are owned by
[The Minimal Package Set](minimal-platform-packages.md); the eight optional module packages are
covered in the [Commercial Platform guide](commercial-guide.md). Hosting references Observability,
Core and Abstractions, so one `PackageReference` to Hosting brings the host and its telemetry.
Persistence is separate and optional.

The packages publish to a **private GitHub Packages feed**, not to nuget.org:

```text
https://nuget.pkg.github.com/The-Running-Dev/index.json
```

Restoring from it needs a GitHub token with `read:packages`. Keep the token out of the file and
expand it from the environment, as GameEngine's host does in its
[`nuget.config`](https://github.com/The-Running-Dev/SubZeroDev.GameEngine/blob/main/src/host/nuget.config):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
    <add key="platform" value="https://nuget.pkg.github.com/The-Running-Dev/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <platform>
      <add key="Username" value="%NUGET_GITHUB_ACTOR%" />
      <add key="ClearTextPassword" value="%NUGET_GITHUB_TOKEN%" />
    </platform>
  </packageSourceCredentials>
</configuration>
```

```powershell
$env:NUGET_GITHUB_ACTOR = '<your-github-username>'
$env:NUGET_GITHUB_TOKEN = '<a token with read:packages>'
dotnet restore
```

Never commit a token. A file beside the project scopes it to that host, because NuGet uses the
nearest `nuget.config`. In a workflow, the default `GITHUB_TOKEN` cannot read packages published
from another repository; GameEngine supplies a read-only secret.

## A minimal web host

Reference Hosting and Persistence at the release version, then register the host:

```powershell
dotnet add MyProduct package SubZeroDev.Platform.Hosting --version 0.1.0
dotnet add MyProduct package SubZeroDev.Platform.Persistence --version 0.1.0
```

```csharp
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;

var builder = WebApplication.CreateBuilder(args);

if (args is ["migrate"])
{
    return await builder.RunPlatformMigrateModeAsync(CancellationToken.None);
}

builder.AddPlatformWebHost();
builder.Services.AddPlatformPersistence();

var app = builder.Build();

app.MapGet("/", () => "hello")
    .ExemptFromPlatformAuthorization("A public landing page that reads nothing tenant-owned.");

await app.RunAsync();
return 0;
```

Every endpoint must declare either `RequiresPlatformAuthorization(permission, feature)` or a named
`ExemptFromPlatformAuthorization(reason)`; a host with an undeclared endpoint refuses to start with
`UndeclaredEndpointRequirement`. The Local profile below also expects an `IPermissionCatalog` and
`IPermissionProvider` when you use the first form; the
[Local sample](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/main/samples/SubZeroDev.Platform.Sample.Local)
shows the smallest honest pair. Register any `IPlatformModule` on `builder.Services` **before** the
`AddPlatformWebHost()` call, and before the `migrate` branch if the module owns a migration.

`AddPlatformWebHost()` binds and validates the options, composes the modules and adds the
following, with no further wiring:

- **Probes.** `GET /health/live` reports liveness and `GET /health/ready` reports readiness, both
  as JSON `{status, checks[{name, status, detail?, data?}]}`. They answer 503 only when the aggregate
  is Unhealthy; Healthy and Degraded answer 200. The detail is full in Development or from loopback
  and minimal otherwise. Persistence adds the readiness checks `platform.database`,
  `platform.pending-migrations`, `platform.peer-host`, `platform.settings-fingerprint` and three
  `platform.outbox.*` checks.
- **Correlation.** A well-formed inbound `traceparent` (and `tracestate`) is adopted; otherwise a
  correlation is minted. It is available as `ICurrentCorrelation` and is the `correlation` in the
  `{code, correlation}` error envelope an unhandled failure produces.
- **Telemetry.** Structured JSON logs to the console and to a rolling file, and OpenTelemetry
  instrumentation for ASP.NET Core, outbound HTTP and the runtime.
- **Module lifecycle.** The module graph is validated at startup, and each module's
  `InitializeAsync` and `ShutdownAsync` run with the host.

The worker equivalent is `builder.AddPlatformWorkerHost()`. A worker owns all background work
(outbox dispatch, scheduled work) and serves only the same two probes, on
`Platform:Hosting:WorkerProbePort` (default `5100`), bound to loopback unless
`Platform:Hosting:WorkerProbeLoopbackOnly` is `false`. The web role runs none of it. The
[worker sample](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/main/samples/SubZeroDev.Platform.Sample.Worker)
is the reference.

## Configuration

Platform reads the `Platform` section. A missing required key aborts startup with an error that
names the key, which is deliberate. These five are required, even for a host that never calls
`AddPlatformPersistence()`:

```json
{
  "Platform": {
    "CompositionProfile": "Local",
    "Persistence": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=my-product.db"
    },
    "Outbox": {
      "ProcessedRetention": "1.00:00:00",
      "PoisonedRetention": "30.00:00:00"
    }
  }
}
```

`Persistence:Provider` is `Sqlite` or `PostgreSql`, and `PoisonedRetention` must be strictly greater
than `ProcessedRetention`. `CompositionProfile` is `Local` or `Operated`. Local is the
identity-free shape for a developer machine or a single-file install: it rejects authentication
providers, tenant resolvers and non-baseline entitlement contributors. Operated requires an
`IAuthenticationProvider` and a durable `IAuditSink`, which the optional modules supply; see the
[Commercial Platform guide](commercial-guide.md) before choosing it.

Everything else has a default. The keys most hosts touch are, beneath `Platform:`:

| Key | Default | Meaning |
|---|---|---|
| `Telemetry:LogDirectory` | `logs` | Where the rolling log file is written, beneath the content root. |
| `Telemetry:OtlpEndpoint` | absent | Opt-in OTLP export. Absent means no exporter and no outbound connection. |
| `Hosting:WorkerProbePort` | `5100` | The worker's probe port. |
| `Hosting:GracefulShutdownDrainWindow` | `30s` | Must be less than `Outbox:ClaimWindow` (`5m`). |
| `Outbox:DispatchInterval` | `5s` | How often the worker looks for undelivered messages. |

Log levels follow `Logging:LogLevel`. Every option, default and validation rule is in
[the contract](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/d3/20-contract.md).

## Persistence and migrations

`AddPlatformPersistence()` is optional and idempotent. It registers the unit of work
(`IUnitOfWork`), the transactional outbox with its dispatcher, event-handler registration
(`AddPlatformEventHandler<TEvent, THandler>(name)`), the migration runner, leases and the host
heartbeat. Both providers, SQLite and PostgreSQL, are production paths. Every table carries a
non-null tenant column from the first schema, defaulted to the single implicit tenant.

A module owns its schema by registering an `IModuleMigrationSource`: a module name and an ordered
list of `IModuleMigration` (each a `Name` and `ApplyAsync(DbConnection, DbTransaction,
CancellationToken)`). Migrations apply per module in ordinal name order, up only, under a
provider-native lock. Seed data goes in an `ISeeder`, which must be idempotent.

Run migrations with the one-shot `migrate` command shown above, as
`dotnet run --project MyProduct -- migrate`. It is a command, not a host role, and never serves
HTTP. It exits 0 on success, 2 when another process holds the migration lock, and 1 for any other
failure. A host started in the `Development` environment also applies pending migrations itself,
but never seeds. In any other environment it never migrates, so `migrate` is the only way a schema
changes.

## Testing your product

`SubZeroDev.Platform.Testing` starts a real host in-process, with no network port to manage:

```csharp
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

await using var host = await PlatformTestHost.CreateBuilder()
    .WithRole(HostRole.Web)
    .WithProvider(PersistenceProvider.Sqlite)
    .StartAsync(CancellationToken.None);

var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
host.Clock.Advance(TimeSpan.FromMinutes(10));
```

The builder also offers `WithSetting(key, value)` (the `Platform:` prefix is added) and
`WithServices(...)`. A test host defaults to the Operated profile with a fake authentication
provider and a fake durable audit sink, a temporary SQLite file, a free probe port and suppressed
background timers; persistence is wired only when `WithProvider` is called. The host exposes
`Clock` (a `FakeClock` you advance instead of waiting), `Events` (what was enqueued and dispatched),
`ProbeAsync` and `RunBackgroundWorkOnceAsync`, so a timing test needs no wall-clock wait. The package
also ships fakes for the clock, tenant, principal, culture, tenant resolver, entitlement contributor
and permission provider, plus an `AuditInspector` and a permission-provider harness. The
[Commercial Platform guide](commercial-guide.md) shows them in use.

## Building from source and releasing

You need the .NET 10 SDK and PowerShell 7. The complete test suite also needs Docker for its
PostgreSQL fixtures.

```powershell
dotnet build SubZeroDev.Platform.slnx -c Release
./build/Test-PackageManifests.ps1 -Version 0.1.0-local -OutputDirectory artifacts/packages
./build/Test-PackageRestore.ps1 -PackageDirectory artifacts/packages -Version 0.1.0-local
```

The last two pack every package and check each manifest, then restore, build and test the sample
solution against the packed artifacts rather than project references. The
[samples](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/main/samples) switch to
package references with `-p:UsePlatformPackages=true -p:PlatformPackageVersion=<version>`, and
`dotnet run --project samples/SubZeroDev.Platform.Sample.Local` starts one.

A release is a tag. Pushing `v*.*.*` runs
[`release.yml`](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/.github/workflows/release.yml),
which refuses any tag that is not `v0.<minor>.<patch>`, builds the API reference, packs, publishes
to the GitHub Packages feed, restores what it published to rerun the tests and sample against it,
and creates a GitHub release carrying the API reference as `api-reference.zip`.

## Where to go next

- [Commercial Platform guide](commercial-guide.md): the optional modules (Identity, Organizations,
  Billing, Licensing, Audit, Mcp, RuntimeSettings), the Operated profile and the consumer-test fakes.
- API reference: generated from the public types' doc comments and attached to each
  [GitHub release](https://github.com/The-Running-Dev/SubZeroDev.Platform/releases) as
  `api-reference.zip`.
- [Samples](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/main/samples): Local,
  Web and Worker hosts that CI builds and runs.
- [The Minimal Package Set](minimal-platform-packages.md): why the framework is six packages and
  where each boundary falls.
