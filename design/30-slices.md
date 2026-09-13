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

## How this document is kept

A slice's body is its specification while it is outstanding. **A re-run of `/slices` appends new slices
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

---

## Landed

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

---

## Outstanding

## S16 — The administration shell
**Status:** in progress

Delivers: an operator can see who they are signed in as, move between the organizations they belong to,
read what the deployment is entitled to and what its licence says, and look through the record of what
has happened — in a screen a consumer can throw away and replace with their own.

Touches:
- **The shell** — a front-end build in the delivery shape settled for design Open question 2, with no
  .NET package a backend could reference
- **`tests/SubZeroDev.Platform.Tests`** — the I-W1 assertions over the package graph and the shell's call
  list

Depends on: S13.

Acceptance:
- **S16.1** The shell displays the current principal's kind and display name, and shows an anonymous state
  distinctly from an authenticated account.
- **S16.2** The shell switches the active organization through the same HTTP endpoint an ordinary caller
  uses; a test performs the identical switch with an HTTP client and no UI, and both succeed.
- **S16.3** Every endpoint the shell calls is callable without it: enumerating the shell's calls against
  the public API surface finds no admin-only or UI-only endpoint.
- **S16.4** The shell displays the resolved entitlement, the licence tier and the grace state, reading all
  three through the public API.
- **S16.5** The shell reads the audit record with tenant and instant-range filters and offers no update
  and no delete.
- **S16.6** No backend package references the shell, and the backend builds and runs with the shell's
  assets absent.
- **S16.7** The shell holds no server-side state: it renders an error when the backend is unavailable, and
  nothing is lost when it does.

Out of scope: settings, notifications, API-key administration, payment UI, plugin administration and
feature-flag management — a complete shared administration product is a brief non-goal.

## S17 — The proof: two databases, two hosts, contention and offline
**Status:** queued

Delivers: anyone deciding whether to adopt Platform can look at one CI run and see each of the nine
capabilities pass or fail on its own — on both databases, with the offline deployment and the
several-instances deployment actually exercised rather than assumed.

Touches:
- **The sample solution's scenarios** — one focused set per capability, positive and negative
- **The CI workflow** — the database matrix, the offline run, the multi-instance run, and the
  per-capability reporting

Depends on: S16.

Acceptance:
- **S17.1** CI runs the operated scenarios against SQLite and against PostgreSQL, and reports each of the
  nine capabilities as a separately named result. Starting successfully is not one of them.
- **S17.2** The contention scenarios — concurrent licence verification, concurrent organization creation,
  one invitation redeemed twice, and concurrent audit appends — run against PostgreSQL with at least two
  host instances.
- **S17.3** The local host's scenarios run with outbound network unavailable, and the run fails if any
  outbound connection is attempted.
- **S17.4** The operated proof uses at least two principals, two organizations and two tenants.
- **S17.5** Nine mutation fixtures, one per capability, each violating a negative criterion from the
  brief's capability table, and CI fails against every one of them.
- **S17.6** The local host's scenarios assert it has no package or project reference to Identity,
  Organizations, Billing or Licensing, requires no configuration or storage for them, and uses the
  `system:local` audit actor.

Out of scope: deploying or operating the sample — it is a CI proof, not an environment. Performance,
throughput, latency and capacity measurement, and any comparison between the two database providers.

## S18 — Packages, documentation and the corrected plan
**Status:** queued

Delivers: someone outside this repository can take Platform's commercial surface as published packages,
follow written instructions to build either deployment shape, and find the effort's own plan describing
what was actually built rather than what was once intended.

Touches:
- **The CI packaging steps** and the sample solution's package references
- **[`Fakes.cs`](../src/SubZeroDev.Platform.Testing/Fakes.cs)** — the D5 fakes and the audit inspector
- **`docs/`** — the human-facing guide
- **`implementation-plan.md`** and [`90-decisions.md`](90-decisions.md)

Depends on: S17.

Acceptance:
- **S18.1** Every D5 package is packed in CI as a versioned 0.x artifact, and the sample solution restores
  them from those artifacts rather than through project references.
- **S18.2** `SubZeroDev.Platform.Testing` exposes a fake principal of each of the four kinds, a fake tenant
  resolver, a fake entitlement contributor, a fake permission provider, an audit inspector and the
  composition profile on the test host — and no fake organization, subscription or licence.
- **S18.3** The audit inspector reads records and exposes no write and no clear.
- **S18.4** The human-facing documentation covers registration, optional composition, both deployment
  shapes, security defaults, failure semantics and the sample's asserted scenarios, and the documentation
  build passes with no broken link.
- **S18.5** `implementation-plan.md`'s D5 capability list includes `Platform.Mcp`, and its done-when
  matches [`00-brief.md`](00-brief.md).
- **S18.6** The retained consumer-evidence objection for Authorization, Licensing, Audit and the shared web
  UI is recorded in [`90-decisions.md`](90-decisions.md) and is not written as resolved.

Out of scope: publishing to a public registry, and onboarding any named consumer — external adoption is
separate work.
