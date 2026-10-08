# Slices — commercial (D5)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a declaration the contract
does not carry — a module's own API surface is the likeliest place, since `20-contract.md`
§ *Public surface* 10 names what each module exposes without declaring it — the slice **stops and asks
for a contract amendment rather than inventing one.**

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code that
satisfies them. One repository is in scope, this one, plus the two sample hosts and the shell that S1
and S16 create inside it.

**The ordering is the design's risk ordering.** The riskiest thing in this design is not any one
capability — it is the single idea every capability is generated from:
[ADR-006](../docs/docs/adr/ADR-006-application-modules.md) rules 1 and 2 held **structurally**, so that
the framework can own nine questions while six modules own the answers and no module can reach another.
If that cannot be enforced by the build, the shape is wrong and everything downstream rests on a
preference. S1 therefore builds the enforcement
before there is anything to enforce it against, and proves it bites by failing it against a
deliberately broken graph — the standard
[`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 sets. S2 takes the one
change that is cheap now and expensive after a consumer ships. S3 to S8 are the framework seams, each
placed where it first becomes checkable, ending with the fixed request order every later slice plugs
into rather than retrofits. S6 sits as early as its prerequisites allow because the deliberately shared
resource is the part of tenancy no library provides and the part
[`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §3 names as how isolation
quietly stops holding. S9 to S16 are the six modules and the shell. S17 is the proof the brief's
definition of done actually asks for, and S18 is public delivery.

## Decisions that must be taken before the slice that needs them starts

Neither the design nor the contract settles these, and none is a slice's to settle silently.

| Question | Needed before |
|---|---|
| Contract Unresolved 1 — whether `IAuditable.CreatedBy` becomes non-null under a total principal | **S2**. Both readings are defensible and produce different declarations. Determined either way: the stored value is `PrincipalId.ToString()` and is never split, and `ModifiedBy`/`DeletedBy` stay nullable |
| Contract Unresolved 2 — how the frozen entitlement-contributor set reaches the settings-fingerprint input | **S8**, which is where I-C4 lands. The two shapes change different public declarations — a second parameter on `Compute`, or a projected `[Fingerprinted]` property. Determined either way: the names are inside the hashed input and `SettingsFingerprint`'s format version changes |
| Design Open question 2 — how the administration shell is delivered | **S16**. The recommendation is a separate front-end build with no .NET package; the alternative is a .NET-hosted UI. Neither introduces a .NET declaration, so nothing before S16 changes shape either way |
| Design Open question 4 — whether the D5 packages version in lockstep with the framework or independently | **S18**. The recommendation is lockstep for the whole of D5 |
| Design Open question 3 — whether ADR-006's vocabulary is corrected now that five infrastructure packages land in its module tier | **Not blocking any slice.** It is an ADR amendment and therefore the repository owner's; the placement in *Module boundaries* does not wait on the answer. Recorded here so `/slice` does not read the silence as settled |
| Issue #94's disposition once S23 lands. By the 2026-09-26 decision, D5 does not deliver #94's criteria 1, 3 and 4 (the sign-in hook, the sign-out proof and the hook documentation), and criterion 2 holds in validation terms only | **Not blocking any slice.** It is the repository owner's call whether #94 closes with those criteria recorded as accepted risk, or is split so they stay tracked. Recorded here so `/track` does not close #94 as fully delivered |

## How this document is kept

A slice's body is its specification while it is outstanding. **A re-run of `/plan` appends new slices
under `## Outstanding` only** — it never rewrites `## Landed`, and it never renumbers or reuses a
retired id, including one that never got an issue. Removing a criterion leaves a gap; the next
criterion takes the next unused number.

When a slice ships and its issue closes, its body is retired to a one-line entry under `## Landed`. The
index carries no criteria, because the issue is then the record of what was accepted — `/track` does
not sync a landed slice and must not re-derive its criteria from the index.

Every slice carries a `**Status:**` line reading `shipped`, `in progress` or `queued`, immediately
under its heading. [`build/Test-SliceStatusMarkers.ps1`](../build/Test-SliceStatusMarkers.ps1) fails the
documentation gate when the markers are inconsistent: at most one slice in progress, at least one in
progress while any is queued, and no shipped slice ordered after a queued one.

Each outstanding slice also carries a plain `Status:` line, `todo` or `done`, directly under the first.
That one is what `/agentkit:next` reads to pick the next slice. The two move together in the slice's
own pull request: `Status: done` and `**Status:** shipped` on the slice that ships, and
`**Status:** in progress` on the next queued one.

---

## Outstanding

S24 to S30 take every open issue that can be built without a decision from the repository owner. That
is the D5 work the issues surfaced after S18 landed. The riskiest of them goes first. S24 is a real
defect: during an outage, a caller is told it lacks permission when nothing was decided. It also changes
a public answer on two surfaces, HTTP and Mcp. S25 removes the CI flake that has failed two unrelated
pull requests, so that every later slice's CI result means something. S26 to S30 close issue
[#260](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/260). Each one tests the part of an
invariant that today is held only by instruction, then moves its record to `code`. They are grouped by
the package that owns the invariant, so that each pull request reads as one area.

A test in S26 to S30 may show that the code does not hold an invariant. That is then a defect, not a
test gap. If the fix stays inside the owning package and needs no contract change, the slice fixes it
and its pull request says so. Otherwise the record stays `instruction`, the slice opens an issue naming
the failing case, and the remaining criteria still land.

## S24 — A permission check during an outage answers "try again", not "forbidden"
**Status:** in progress
Status: todo

Delivers: someone calling a Platform-hosted service while a permission provider is unreachable is told
the service could not check their permission and that they may retry. Today they are told they lack
permission. An operator reading the answer can tell an outage from a real refusal.

Touches:
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs)** (Abstractions):
  `AuthorizationDecision.ProviderFailure`, as `20-contract.md` *Types* § 2 scaffolds it
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Core/Authorization.cs)** (Core): the evaluator
- **[`EndpointRequirement.cs`](../src/SubZeroDev.Platform.Hosting/EndpointRequirement.cs)** (Hosting)
  and **[`McpToolAdapter.cs`](../src/SubZeroDev.Platform.Mcp/McpToolAdapter.cs)** (Mcp): where a decision
  is answered
- **[`20-contract.md`](20-contract.md)**: the *Types* § 2 scaffold, and the I-A11 and I-A12 records
- **`tests/`**

Depends on: none

Acceptance:
- **S24.1** `AuthorizationDecision.ProviderFailure` exists exactly as the contract declares it. The
  *Types* § 2 scaffold block is replaced by the pointer to the tree, in the same change.
- **S24.2** With two registered providers, where the first returns any error and the second grants
  nothing, the decision is `Denied` and `ProviderFailure` is `ProviderUnavailable` naming the first
  provider. The same holds when the error the provider returned is not `ProviderUnavailable`.
- **S24.3** With three registered providers, where the second and third return errors, `ProviderFailure`
  names the second.
- **S24.4** With one provider that returns an error and another that grants the permission, the decision
  is `Allowed` and `ProviderFailure` is null. A denial in which every provider answered carries a null
  `ProviderFailure`.
- **S24.5** Over HTTP, an endpoint requiring a permission, with the Organizations permission provider's
  store unreachable, answers 503 with the `ProviderUnavailable` code. It does not answer 403 or 404, and
  the endpoint's handler never runs.
- **S24.6** Over Mcp, the same condition answers a tool result saying the permission could not be checked
  and may be retried. The tool's invoker never runs.
- **S24.7** A decision carrying both `AuditFailure` and `ProviderFailure` is answered with the audit
  failure's code, on both surfaces.
- **S24.8** The I-A11 and I-A12 records move from `instruction` to `code`, citing the tests above, in
  the same change. Issue #260's I-A11 box is ticked by this slice.

Out of scope: retrying the provider inside the evaluator, any change to the order providers are
consulted in, and caching a provider's last answer. None is in the contract.

## S25 — Cross-process trace evidence passes or fails on the code, not on a sampling draw
**Status:** queued
Status: todo

Delivers: a contributor whose pull request touches nothing near tracing no longer has CI fail on it at
random. When CI does fail on the tracing test, the failure means fresh-root tracing really broke.

Touches:
- **[`trace-evidence.test.ts`](../workloads/game-service/tests/trace-evidence.test.ts)** and its
  `tests/support/` helpers

Depends on: none

Acceptance:
- **S25.1** The malformed-`traceparent` case no longer needs any particular sampling outcome to occur
  within a fixed number of attempts. Either the test's pass condition is derived from the sampler's
  deterministic per-trace-id decision, or the attempt count is derived and stated so that a miss is
  below one in a million runs. The derivation is written in the test.
- **S25.2** With the edge's span pairing deliberately broken, for example the workload span's parent
  not propagated, the test fails on every run.
- **S25.3** The well-formed-`traceparent` assertions (S8.1, S8.2) are unchanged.
- **S25.4** The `game-service` CI job passes on this slice's pull request, and on the reruns needed to
  show S25.1 holds in practice: three consecutive runs.

Out of scope: changing `PlatformSampler`'s ratio, or making it configurable. Either one is a public
surface the contract does not declare. Also out of scope: the other `game-service` tests.

## S26 — The authorization and request-scope invariants are proven, not assumed
**Status:** queued
Status: todo

Delivers: a maintainer changing the evaluator, the composition provider or the operation scope finds out
from a failing test, not from production, if they break a promise the contract makes about who may do
what.

Touches: `tests/SubZeroDev.Platform.Tests/` (`AuthorizationTests.cs`, `OperationScopeTests.cs`,
`PrincipalTests.cs`, `TenancyTests.cs`), and the I-A3, I-A7, I-I1 and I-R3 records in
[`20-contract.md`](20-contract.md) *Invariants*

Depends on: 24

Acceptance:
- **S26.1** I-A3: the evaluator, called directly with a `PermissionName` that no registered
  `IPermissionCatalog` declares, refuses it. The test does not go through a path that would have caught
  the name earlier.
- **S26.2** I-A7: in `Operated`, the composition provider grants nothing to an `Account` or a `Delegated`
  principal, not only to System and Anonymous.
- **S26.3** I-I1: setting the ambient principal to null inside an open operation scope is refused, and
  reading it never yields null.
- **S26.4** I-R3: an attempt to change the scope's tenant or principal after the scope opens is refused,
  and a read at the end of the request returns the values set at its start.
- **S26.5** Each of I-A3, I-A7, I-I1 and I-R3 moves to `code` with its new test as Evidence, and #260's
  matching box is ticked.

Out of scope: any change to the evaluator's behaviour beyond what a failing test proves is a defect
(see *Outstanding*).

## S27 — Tenant isolation is proven for writes and for every entity type
**Status:** queued
Status: todo

Delivers: a product team that stores customer data on Platform can rely on one tenant never writing
another tenant's rows, and on isolation that does not depend on whether a type is shareable, because a
test fails the build if either stops being true.

Touches: `tests/SubZeroDev.Platform.Tests/` (`SharedReadTests.cs`, `TenancyTests.cs`,
`PackageGraphTests.cs`), and the I-T1, I-T2, I-T7 and I-O8 records

Depends on: 26

Acceptance:
- **S27.1** I-T1: inside tenant A's scope, an update, a delete and an insert carrying tenant B's id each
  fail or affect zero of tenant B's rows. This holds inside a shared-read scope too.
- **S27.2** I-T2: outside a shared-read scope, a query over a shareable type and a query over a
  non-shareable type each return only the current tenant's rows.
- **S27.3** I-T7: a test fails if any migration file present at D5's baseline commit is modified, or if
  the tenant identifier's column type, the primary keys or the implicit-tenant representation differ
  from that baseline.
- **S27.4** I-O8: a reflection test over every framework assembly finds no type or member that refers to
  a tenant's owner. It does not rely on I-C6's package-graph test.
- **S27.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: any new escape hatch for writes, and any change to the shared-read audit.

## S28 — Audit's guarantees are proven across every surface that writes it
**Status:** queued
Status: todo

Delivers: a compliance reviewer can point at a test for each audit promise: that a record cannot be
changed after it is written, that secrets never reach it, and that an allowed action is recorded as
reliably as a refused one.

Touches: `tests/SubZeroDev.Platform.Tests/` (`AuditTests.cs`, `AuditStoreTests.cs`, `LicensingTests.cs`),
and the I-U4, I-U6, I-U7 and I-U9 records

Depends on: 26

Acceptance:
- **S28.1** I-U4: an allowed action whose work completes writes its audit row with the outcome known, in
  its own transaction. The denial case is already covered.
- **S28.2** I-U6: one test pushes a representative secret and payload through each audited input
  surface the tree has, and asserts neither appears in the stored record or in any captured log line.
  The surfaces are enumerated from the tree, not hand-listed, so a surface added later without coverage
  fails the test.
- **S28.3** I-U7: at the schema level, the audit table rejects an `UPDATE` and a `DELETE` issued directly
  against the database, not only through the store's API. If the schema does not hold this today, see
  *Outstanding*.
- **S28.4** I-U9: the Core owner's detection site audits a repeated error condition once per detection,
  however many checks observe it.
- **S28.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: audit retention and `Prune` scheduling, which is
[#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187)'s owner decision.

## S29 — Host startup, telemetry, the shell and billing keep their promises under test
**Status:** queued
Status: todo

Delivers: an operator deploying a Platform host can trust that a bad configuration stops the host with a
named cause, that missing telemetry settings start no exporter, and that billing never calls out on a
request, because each of these is tested and not just written down.

Touches: `tests/SubZeroDev.Platform.Tests/` (`CompositionProfileTests.cs`, `TelemetryOptionsTests.cs`,
`TelemetryExportTests.cs`, `AdministrationShellTests.cs`, `PackageGraphTests.cs`, and a billing test),
and the I-C9, I-OB1, I-W1 and I-B6 records

Depends on: 26

Acceptance:
- **S29.1** I-C9: a test discovers every `HostStartupError` variant by reflection, and for each one
  provokes it and asserts the host fails to start, naming the registration. A variant added later
  without a provocation fails the test.
- **S29.2** I-OB1: with no OTLP endpoint configured, the host starts and no exporter instance exists in
  the built service provider. The test observes this directly; it does not infer it from the absence of
  traffic.
- **S29.3** I-W1: the shell's build output contains no server-side state store and no endpoint of its
  own, and every request it issues targets a route in the public HTTP API's route table.
- **S29.4** I-B6: with an outbound handler that fails the test on any call to a billing provider's host,
  a request, a host startup and a readiness probe each complete without calling it.
- **S29.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: adding new startup checks or new telemetry options.

## S30 — Mcp, Organizations and Licensing keep their promises under test
**Status:** queued
Status: todo

Delivers: a product exposing tools over Mcp, inviting members, or selling paid features can rely on
credentials being checked once per connection, invitation tokens never being stored readable, and
licensed work already under way not being cut off when a licence lapses.

Touches: `tests/SubZeroDev.Platform.Tests/` (`McpTests.cs`, `McpInvocationTests.cs`,
`OrganizationsTests.cs`, `LicensingTests.cs`), and the I-M5, I-M7, I-O4 and I-L8 records

Depends on: 26

Acceptance:
- **S30.1** I-M5: a reflection test over the Mcp tool and invoker surface finds no parameter that
  carries a credential, and a call presenting a credential in its arguments is authenticated as the
  connection's principal, not as the credential's.
- **S30.2** I-M7: a manifest-projected producer and a product-owned fixed-table producer register through
  the same public surface in one host, and both tools are listed and invocable with identical
  authorization behaviour.
- **S30.3** I-O4: after an invitation is minted, the stored row holds a hash that is not the token, and
  no API returns the token again. A second read attempt returns nothing token-shaped.
- **S30.4** I-L8: after the grace period ends, work accepted before it ended runs to completion,
  scheduled work runs at its time, and existing data stays readable and exportable. New paid-feature
  work is denied.
- **S30.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked. With S24
  to S29, that closes #260.

Out of scope: the cancelled-call test that
[#224](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/224) keeps skipped until
`ModelContextProtocol` ships its fix.

## Not yet sliced

These open issues are not in this plan. Each one waits on something a slice cannot settle.

| Waiting on | Issues |
|---|---|
| An owner decision. Each is small once decided, and becomes a slice here as soon as it is | [#68](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/68) development-time automatic migration: build it, or record it as retracted · [#61](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/61) seeding: a contract beside `IMigrationRunner`, or remove the references to a seeder · [#60](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/60) optimistic concurrency as an opt-in column · [#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187) audit retention and who schedules `Prune` |
| A design pass (`/agentkit:spec` or `/agentkit:align`) before any slice | [#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264) revocation of mirrored issuer roles · [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58) runtime settings · [#62](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/62) consumer idempotency · [#63](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/63) module lifecycle hooks · [#110](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/110) streaming at the edge · [#80](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/80) which slice set the roadmap renders |
| Platform Identity's design cycle | [#98](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/98), [#99](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/99), [#100](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/100), and [#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93) boxes 3–4 (they presuppose an owned user store) |
| Another repository or an outside party | [#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97) Adventures consuming the packages · [#224](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/224) `ModelContextProtocol` fix (csharp-sdk PR 1377 still unmerged; 2.2.0 is the latest release) · [#24](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/24), [#25](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/25), [#81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81), [#85](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/85), [#86](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/86) |

---

## Landed

| Slice | Name | Issue | Criteria | Body complete at |
|---|---|---|---|---|
| **S19** | An untrusted credential is refused, not ignored | [#270](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/270), closed | S19.1–S19.8 | `c00c2af` |
| **S20** | An issuer with fixed keys is trusted from configuration alone | [#271](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/271), closed | S20.1–S20.12 | `bca2535` |
| **S21** | Keys are found through discovery and kept fresh | [#272](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/272), closed | S21.1–S21.10 | `60d4167` |
| **S22** | One issuer serves many customer tenants | [#273](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/273), closed | S22.1–S22.6 | `c00c2af` |
| **S23** | A vendor's settings arrive as a configuration source | [#274](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/274), closed | S23.1–S23.6 | `c00c2af` |

- **S1 — The two hosts and the enforced package boundary** — shipped:
  [#169](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/169) via
  [#195](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/195).
- **S2 — The total principal** — shipped:
  [#170](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/170) via
  [#196](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/196).
- **S3 — Audit: the contract, the writer and the log sink** — shipped:
  [#171](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/171) via
  [#197](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/197).
- **S4 — Authorization: names, providers and the evaluator** — shipped:
  [#172](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/172) via
  [#199](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/199).
- **S5 — Tenant resolution at the request boundary** — shipped:
  [#173](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/173) via
  [#200](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/200).
- **S6 — The shareable type and the audited cross-tenant read** — shipped:
  [#174](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/174) via
  [#201](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/201).
- **S7 — The entitlement seam and the Community baseline** — shipped:
  [#175](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/175) via
  [#203](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/203).
- **S8 — The fixed request order and the composition profile's startup rules** — shipped:
  [#176](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/176) via
  [#204](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/204),
  [#210](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/210),
  [#211](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/211),
  [#213](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/213) and
  [#215](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/215).
- **S9 — Identity: authenticating a principal at the transport** — shipped:
  [#177](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/177) via
  [#214](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/214).
- **S10 — Organizations** — shipped:
  [#178](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/178) via
  [#216](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/216).
- **S11 — Billing** — shipped:
  [#179](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/179) via
  [#217](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/217).
- **S12 — Licensing** — shipped:
  [#180](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/180) via
  [#218](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/218).
- **S13 — The durable audit store** — shipped:
  [#181](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/181) via
  [#219](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/219).
- **S14 — Mcp: the frozen catalogue and its startup checks** — shipped:
  [#182](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/182) via
  [#220](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/220).
- **S15 — Mcp: the transport, the connection principal and invocation** — shipped:
  [#183](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/183) via
  [#221](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/221).
- **S16 — The administration shell** — shipped:
  [#184](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/184) via
  [#223](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/223).
- **S17 — The proof: two databases, two hosts, contention and offline** — shipped:
  [#185](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/185) via
  [#226](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/226), [#227](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/227), [#228](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/228), [#231](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/231).
- **S18 — Packages, documentation and the corrected plan** — shipped:
  [#186](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/186) via
  [#233](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/233).
