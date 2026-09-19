---
title: Commercial Platform Guide
sidebar_label: Commercial Platform (D5)
sidebar_position: 15
---

# Commercial Platform

D5 adds Identity, Authorization, Organizations, Tenancy, Billing, Licensing, Audit, a shared
web shell and MCP. The framework owns decision seams; optional modules contribute policy and
storage. A module does not reference another module, and the framework does not reference modules.
The [D5 brief](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/00-brief.md)
defines the bounded capability set.

## Packages and the sample

All .NET packages share one explicitly unstable 0.x version. The six framework packages are
Abstractions, Core, Hosting, Observability, Persistence and Testing. The six module packages are
Identity, Organizations, Billing, Licensing, Audit and Mcp. Each package name starts with
`SubZeroDev.Platform.`. Authorization and entitlement are framework seams, not additional packages.
The web shell is a separate frontend and is not a backend package dependency.

The **Package Consumers** CI workflow uploads the twelve checked `.nupkg` files as the
`d5-packages` artifact. A separate job downloads them into a fresh checkout, restores the
sample solution using an isolated package cache, checks that every Platform reference resolved
as a package at that version, then builds and runs the assertions and web/worker round trip.
Platform package names are mapped exclusively to the artifact directory; other dependencies
come from NuGet. This does not publish to a public registry.

From the repository root, reproduce that path with .NET 10 and PowerShell 7:

```powershell
./build/Test-PackageManifests.ps1 -Version 0.1.0-local -OutputDirectory artifacts/packages
./build/Test-PackageRestore.ps1 -PackageDirectory artifacts/packages -Version 0.1.0-local
```

The complete test suite needs Docker for its PostgreSQL fixtures. Use a fresh output directory
when changing the version; the manifest check rejects duplicate artifacts. For an ordinary
source build use `dotnet build SubZeroDev.Platform.slnx -c Release`. The artifact script uses
`samples/PackageConsumers.slnx` with `UsePlatformPackages=true` and the supplied version.

## Registration and optional composition

Register modules as `IPlatformModule` services **before** the standard host call. A web entry
point calls `builder.AddPlatformWebHost()`; a worker calls `builder.AddPlatformWorkerHost()`.
These compose the registries, correlation, probes and telemetry. Persistence is optional and
is installed with `builder.Services.AddPlatformPersistence()` when the host needs a store.

For example, an operated host with Identity and durable audit registers:

```csharp
builder.Services.AddSingleton<IPlatformModule, IdentityModule>();
builder.Services.AddSingleton<IPlatformModule, AuditModule>();
builder.Services.AddSingleton<IAuthenticationProvider>(authenticationProvider);
builder.AddPlatformWebHost();
builder.Services.AddPlatformPersistence();
```

Here `authenticationProvider` is the deployment's implementation of `IAuthenticationProvider`.
Import the Abstractions, Identity, Audit, Hosting and Persistence namespaces and Microsoft DI
extensions. Supply the required Platform settings before composing the host; the runnable
[operated sample](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/main/samples/SubZeroDev.Platform.Sample.Web)
shows the complete entry point and configuration, including migrations.

Add `OrganizationsModule`, `BillingModule` or `LicensingModule` only when those capabilities are
needed. Licensing also needs `LicensingOptions` containing the document path and accepted public
keys supplied by the consumer. Billing contributes entitlements through the provider-neutral
seam; D5 does not perform checkout or money movement. Product code asks `IEntitlementEvaluator`
about a `FeatureName`, never subscription state or a licence tier.

For MCP, register `McpModule`, the consumer's `IToolProducer` and `IToolInvoker`, and a
`McpOptions` instance naming explicitly exposed tools. Map the transport with `app.MapPlatformMcp()`.
Installing a producer exposes nothing by itself. Both manifest projections and fixed tables
use the same producer interface, and the catalogue freezes at startup.

## The two deployment shapes

**Local** declares `Platform:CompositionProfile=Local`. The local sample references Hosting
alone and has no Identity, Organizations, Billing or Licensing dependency. No account or
commercial database setup is needed. With no tenant resolver, the implicit tenant remains in
use. Explicit local operations use `Principal.LocalSystem` (`system:local`) as their actor;
anonymous HTTP requests do not inherit that trust.

```powershell
dotnet run --project samples/SubZeroDev.Platform.Sample.Local
```

The local sample's root endpoint deliberately grants its own diagnostic permission to any
principal. The framework's composition provider grants nothing to Anonymous. CI separately
runs the local host with outbound traffic blocked and checks that it attempted no outbound
connection; building and restoring dependencies happen before that restriction is installed.

**Operated** declares `Platform:CompositionProfile=Operated`. Startup requires an authentication
provider and an audit sink declaring durability. The sample uses SQLite by default; the
operated correctness scenarios also exercise PostgreSQL, and multi-instance assertions use
PostgreSQL. The sample's web and worker roles share the store.

```powershell
dotnet run --project samples/SubZeroDev.Platform.Sample.Web -- migrate
dotnet run --project samples/SubZeroDev.Platform.Sample.Web
```

Run the worker in another terminal with
`dotnet run --project samples/SubZeroDev.Platform.Sample.Worker`. Keep both processes configured
for the same database. The Linux CI round-trip script supplies matching settings and checks
restart delivery. These are local sample commands, not deployment instructions.

The operated sample uses a deterministic test issuer and deliberately permissive permissions
for its diagnostic surface. Its licence document is absent and it starts at Community. Those
fixtures demonstrate composition; they are not production authentication keys or permission policy.

## Security defaults and failures

The request order is authenticate, resolve tenant, open the operation scope, authorize, check
entitlement when admitting paid-feature work, execute, and audit. Every endpoint declares its
permission with `RequiresPlatformAuthorization(permission, feature)` or carries an explicit
reasoned exemption. Use `feature: null` for reads and exports that must remain available after
expiry. A resource-specific check is an additional handler check; the pipeline check is coarse.

| Condition | Behaviour |
|---|---|
| Bad credential or missing cached verification key | Authentication fails; no downgrade to Anonymous and no request-time key fetch |
| No permission grant, or a provider cannot answer | Denied; provider unavailability remains retryable |
| Resource belongs to another tenant, or organization membership is absent | Not found, so existence is not disclosed |
| Entitlement contributor cannot answer | Contributes no grant; other contributors may still grant |
| Licence absent, invalid or unreadable | Previously verified claims stand, otherwise Community; stored expiry and grace are not extended |
| Licence grace ends | New paid-feature work is denied; admitted work continues and existing data stays readable and exportable |
| Required audit write fails | Retryable failure and degraded readiness; a write enlisted in a transaction rolls back with it |
| Recorded audit write fails | Logged and readiness degrades; the response is unaffected |
| Tool unregistered or registered but unexposed | The same unknown-tool response; no existence disclosure |
| Invalid composition, duplicate registration name or undeclared endpoint permission | Startup fails with a named error |

Local composition rejects authentication providers, tenant resolvers and non-baseline entitlement
contributors. Tenant writes cannot cross boundaries. Shared reads require an explicitly declared
shareable type and an audited read-only scope; writing inside it is a contract violation.
Audit records contain no payload or changed-field bag. The writer redacts caller-controlled action
and resource strings before dispatch. MCP credentials belong to the connection, never tool arguments;
secret-shaped schema parameters fail startup.

## Consumer tests

`PlatformTestHost.CreateBuilder()` defaults to Operated with test authentication and an in-memory
test audit sink. Set `WithSetting("CompositionProfile", "Local")` to test the local composition.
The started host's `CompositionProfile` reports the validated value and has no setter.

`FakePrincipals` supplies Anonymous, System, Account and Delegated principals. `FakeTenantResolver`
initially defers; `FakePermissionProvider` and `FakeEntitlementContributor` initially grant nothing.
Configure their responses before registering them through the existing seams. Multiple instances
need distinct names. Entitlement contributors use the keyed DI slot
`EntitlementContributorRegistration.ServiceKey`; ordinary callers use the evaluator.

Construct `AuditInspector` with the host's `FakeDurableAuditSink`. Its `Records` property returns
read-only snapshots in arrival order. It cannot write or clear records and is not a durable-store
query. The sink's in-memory records are scoped to the test's process. Testing has no fake organization,
subscription or licence; module-specific knowledge remains with its module.

## What the scenarios assert

The [operated scenarios](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs)
report Identity, Authorization, Organizations, Tenancy, Billing, Licensing, Audit, SharedWebUi and
Mcp separately. They cover transport authentication; allowed and denied actions; invitation and
membership boundaries; tenant isolation and audited shared reads; entitlement changes; signed and
tampered licences; durable audit and secret exclusion; shell access through the public API; and
explicitly exposed tools with authorization before invocation.

CI also runs the local offline proof, PostgreSQL multi-instance assertions, restart and grace
scenarios, and all nine negative mutation fixtures. Each mutation must trigger its expected
assertion failure; a build failure or unrelated assertion does not count as a caught mutation.
The package-consumer run executes the same Platform test project against package artifacts.

These proofs establish sample correctness, not external adoption. The consumer-evidence objection
for Authorization, Licensing, Audit and shared web UI remains unresolved in the
[decision log](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/90-decisions.md).
Performance, throughput, database comparison, public registry publishing and named-consumer
onboarding are outside this effort.
