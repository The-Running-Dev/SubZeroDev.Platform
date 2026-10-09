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
Abstractions, Core, Hosting, Observability, Persistence and Testing. The eight module packages are
Identity, SignIn, Organizations, Billing, Licensing, Audit, Mcp and RuntimeSettings. Each package name starts with
`SubZeroDev.Platform.`. Authorization and entitlement are framework seams, not additional packages.
The web shell is a separate frontend and is not a backend package dependency.

The **Package Consumers** CI workflow uploads the fourteen checked `.nupkg` files as the
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
builder.AddPlatformWebHost();
builder.Services.AddPlatformPersistence();
```

Identity registers a provider for each issuer the operator lists in configuration, so no
authentication provider is registered by hand; see [Trusting an issuer](#trusting-an-issuer-from-configuration).
A deployment whose issuer the schema below cannot describe still registers its own
`IAuthenticationProvider`. Import the Abstractions, Identity, Audit, Hosting and Persistence namespaces and Microsoft DI
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

`migrate` applies every registered module's migrations, then runs each module's seeders: the
installation-wide data a module needs, written so that re-running `migrate` changes nothing. When the
host's environment is `Development` (`DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`), the web and
worker hosts also apply pending migrations themselves at start; they do not seed, so run `migrate` once
for seed data. In any other environment a host never migrates, and `migrate` is the only way a schema
changes.

The operated sample uses a deterministic test issuer and deliberately permissive permissions
for its diagnostic surface. Its licence document is absent and it starts at Community. Those
fixtures demonstrate composition; they are not production authentication keys or permission policy.

## Trusting an issuer from configuration

An operator trusts a token issuer by writing settings under `Platform:Identity:Bearer:<name>`,
where `<name>` is a name the operator chooses for that provider. Each child section becomes one
authentication provider, validated before anything is fetched. A settings file, environment
variables and a vendor's configuration package all write the same keys, and Identity cannot tell
which wrote them; where two write one key, the host's configuration precedence decides.

| Key under `<name>` | Required | Default | Constraint |
|---|---|---|---|
| `Issuer` | exactly one of `Issuer` and `IssuerPattern` | none | The expected `iss`, compared ordinally. An absolute URI. |
| `IssuerPattern` | exactly one of `Issuer` and `IssuerPattern` | none | An issuer string containing `{tenantid}` exactly once. The placeholder matches one or more characters, none of them `/`; every other character matches ordinally. |
| `Discovery` | exactly one of `Discovery` and `SigningKeys` | none | The issuer's OpenID Connect discovery address: an absolute `https` URI. |
| `SigningKeys` | exactly one of `Discovery` and `SigningKeys` | none | A fixed JSON Web Key Set, inline as one string. Public keys only. |
| `Audiences` | yes | none | A non-empty array of non-empty strings; a token must name one in `aud`. |
| `Algorithms` | no | `[RS256]` | Drawn from `RS256`, `RS384`, `RS512`, `PS256`, `PS384`, `PS512`, `ES256`, `ES384`, `ES512`. Never `HS*` or `none`. |
| `ClockTolerance` | no | `00:01:00` | A non-negative duration of at most `00:05:00`. |
| `SubjectClaim` | no | `sub` | The claim that becomes the principal's subject. |
| `DisplayNameClaim` | no | none | The claim that becomes the display name. |
| `KeyRefreshInterval` | no | `00:05:00` | Only with `Discovery`. From `00:00:30` to `1.00:00:00`. |

A defect fails startup naming the full key, `Platform:Identity:Bearer:<name>:<setting>`, before any
key is fetched. A missing required key, or neither of an either-or pair, is `MissingRequiredSetting`.
Two keys that exclude each other, or `KeyRefreshInterval` beside `SigningKeys`, are
`InconsistentSettings`. A value that breaks its constraint, and any key not in the table, are
`InvalidSetting`. A failed key fetch never fails startup; it degrades readiness and the provider
refuses tokens until keys arrive.

**An issuer with fixed keys.**

```json
{ "Platform": { "Identity": { "Bearer": { "corp": {
  "Issuer": "https://login.example.com",
  "SigningKeys": "{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"key-1\",\"n\":\"…\",\"e\":\"AQAB\"}]}",
  "Audiences": [ "platform-api" ]
} } } } }
```

**An issuer found through discovery.** The keys are fetched in the background and refreshed on
`KeyRefreshInterval`, never on the request path.

```json
{ "Platform": { "Identity": { "Bearer": { "corp": {
  "Issuer": "https://login.example.com",
  "Discovery": "https://login.example.com/.well-known/openid-configuration",
  "Audiences": [ "platform-api" ]
} } } } }
```

**A multi-tenant issuer.** One entry trusts every customer tenant's issuer string. The principal's
issuer is the concrete `iss` the token carries, so two tenants presenting the same subject are two
principals. With `Discovery`, the discovery document's `issuer` must equal the pattern string itself.

```json
{ "Platform": { "Identity": { "Bearer": { "saas": {
  "IssuerPattern": "https://{tenantid}.login.example.com",
  "Discovery": "https://login.example.com/.well-known/openid-configuration",
  "Audiences": [ "platform-api" ]
} } } } }
```

**Where a vendor's quirk belongs.** A vendor ships its settings as a configuration source: a small
package with one extension method on `IConfigurationBuilder` per protocol surface, taking the provider
name and writing the keys above. It depends on the configuration abstraction and on no Platform
package, so it cannot implement a parallel provider. A quirk the keys cannot express is not handled in
Platform and not worked around in the vendor's package: it becomes a new generic-path key first, and a
key the table does not name fails startup exactly as it would from a settings file.

## Signing a person in (optional)

Platform is a resource server: it checks the bearer token a caller already holds. A browser client
with no sign-in of its own takes the optional `SubZeroDev.Platform.SignIn` package, which is an
OpenID Connect authorization-code client with a proof key (S256). It is a public client: there is no
client secret and no refresh token. A host that does not call `AddPlatformSignIn()` takes none of it.

```csharp
builder.Services.AddPlatformSignIn();          // before AddPlatformWebHost
// ...
app.MapPlatformSignIn();                        // on the host that takes Identity
```

Each method is one section, `SubZeroDev:SignIn:<Name>`. `Issuer` must equal the `Issuer` of a provider
under `Platform:Identity:Bearer`, so the token the module obtains is one Identity will accept.

| Key | Meaning |
|---|---|
| `Issuer`, `ClientId`, `RedirectPath` | Required. `RedirectPath` is where the callback is mapped; two methods may not share one. |
| `Scopes` | Space-separated; must include `openid`. Default `openid`. |
| `PostSignOutPath` | Where sign-out returns to. Default `/`. |
| `EndSessionTemplate` | The provider's own sign-out address, with `{client_id}`, `{return_to}` and `{id_token_hint}` placeholders. Use it when the provider's discovery document lists no `end_session_endpoint`. |
| `ExtraAuthorizeParameters:<name>` | Extra authorize-request parameters, such as `audience`. The names the module owns (`client_id`, `redirect_uri`, `response_type`, `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method`) fail startup. |

A key the table does not name, a client-secret key included, fails startup naming the full key.

Four endpoints are mapped per method: `GET /signin/<Name>/begin`, the callback at `RedirectPath`,
`POST /signin/<Name>/token` and `POST /signin/<Name>/signout`. The session is an encrypted cookie
holding the issuer, the subject, the access token and its expiry, and nothing else. The page reads its
access token from the token endpoint with an `X-Platform-SignIn` header; the cookie itself is
`HttpOnly`.

**Reaching for the hook is expected for some providers, not a sign of doing it wrong.** When the two
settings above cannot express a provider's dialect, register code for that one method and leave the
rest to configuration:

```csharp
builder.Services.ConfigurePlatformSignIn("auth0", hooks =>
{
    hooks.AdjustAuthorizeRequest = context => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
        new Dictionary<string, string>(context.Parameters) { ["audience"] = "https://api.example.com" });
    hooks.ReplaceEndSessionAddress = context => ValueTask.FromResult(new Uri("https://tenant.example.com/v2/logout"));
});
```

Configuration binds first and the hook adjusts after. A hook cannot change what Platform trusts: the
module re-sets its own parameters (state, nonce, proof key, client and redirect) after the hook runs,
and the token it holds is still checked by Identity like any other. Rewriting the scheme behind a
proxy is a deployment concern for `ForwardedHeaders`, not a hook.

What an operator must know:

- **Sign-out clears the module's session. It does not revoke a token already issued**; that token
  stays valid until it expires.
- **Instances that serve one site must share Data Protection keys.** A cookie one instance wrote and
  another cannot read is treated as no session.
- **The cookie carries the access token**, so a provider that issues very large tokens may exceed the
  browser's cookie limit.
- The issuer's discovery document is read on first use, not at startup, and cached for an hour; an
  unreachable issuer does not stop the host from starting.

## Runtime settings (optional)

Startup configuration is read once and fails the host on a bad value. A runtime setting is changed by a
running system and persisted. Compose `RuntimeSettingsModule`, declare your settings in an
`ISettingCatalog` (name, type, default, the layers it may be set at, an optional rule), and read them
with `ISettingReader`. A read returns the user's value, else the tenant's, else the global one, else
your default. Only an account has a user value, and only a real tenant has a tenant value.

Writing global or tenant values needs `Platform.RuntimeSettings.WriteGlobal` or `WriteTenant`. In
Local, the system principal holds both. In Operated, your own permission provider grants them, and
nothing in Platform does. Every change is audited without its value. The module maps no endpoints, so
map your own. Never put a secret in a setting: a name that looks like one fails startup.

The module owns a migration, so register it before the `migrate` branch, as the Local sample does.
There is no cache: every read goes to the database, so a change made on the web host is what the
worker reads next, with no restart. Concurrent writes to one setting leave one value, the last to
commit, and one audit record each.

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

A permission provider you register must take its grants from a source that can be revoked while the
caller's credential is still valid, and never from the token. `PermissionProviderHarness` checks both
against your own provider. `AssertRevokedGrantDeniesNextRequestAsync` takes your provider, a principal,
the permission, and two callbacks that grant and revoke in the provider's own source; it fails if the
provider still answers with the permission on the next request. `AssertTokenClaimsGrantNothingAsync`
fails a provider that grants from a token's role or permission claims. `Principal.Claims` is public as
the raw authentication result, so Platform cannot stop a provider reading it; these two checks are how
the rule is held.

A provider over roles the identity provider manages — a mirror your sync copies into your own tables —
implements `IMirroredPermissionProvider`. Its sync records, with the rows it writes and in the same
transaction, the instant it began reading the issuer, and records it only when that read succeeded; a
failed or partial read leaves both the rows and the instant as they were. Platform then stops asking a
mirror whose instant is older than `Platform:Authorization:MirrorMaximumAge`: it grants nothing and the
check answers "try again". The maximum defaults to 15 minutes and must lie between 30 seconds and one
day; set it above your sync interval plus the time one sync takes, or a healthy mirror goes stale
between syncs. `AssertIssuerRevocationDeniesWithinBoundAsync` takes the started test host, your
registered provider, a principal, tenant, resource and permission, and three callbacks: grant at a fake
issuer, revoke there, and run your real sync against it, stamping from the host's clock. It fails if your
provider does not grant after the grant and a sync, if another provider also grants the permission, if
the grant survives past the maximum with no sync, or if the next sync does not carry the revocation or
advance the instant. A mirrored provider runs both this and `AssertRevokedGrantDeniesNextRequestAsync`.

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
