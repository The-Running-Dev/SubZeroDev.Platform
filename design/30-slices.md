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

---

## Outstanding

S23 delivers the rest of the generic bearer path that issue
[#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94) asks for (`20-contract.md`
*Types* § 12, *Modules* § 10 Identity), after S19 landed the chain-end rule decided on 2026-10-01
(I-I15), and S20, S21 and S22 landed fixed-key, discovered-key and tenant-shaped providers from configuration. The riskiest assumption here is that a provider can be produced from configuration alone. That
means validated before anything is fetched, registered without a per-issuer call, and built on
IdentityModel without the library leaking into Platform's surface. S20 exercised that assumption. Key
discovery (S21, landed), tenant-shaped issuers (S22) and the vendor configuration source (S23) each add keys to
a schema S20 has proved. Each slice before them treats those keys as unrecognised, so every
interim state fails closed and no slice ever removes a key.

## S23 — A vendor's settings arrive as a configuration source
**Status:** in progress

Delivers: a vendor ships its issuer's settings as a small package that the operator adds to the host's
configuration. The host treats those settings exactly as if the operator had typed them, the package
cannot depend on Platform, and an operator has written instructions for configuring any issuer.

Touches:
- **`tests/` fixtures**: a vendor-shaped configuration-source project that references no Platform
  package
- **[`PackageGraphTests.cs`](../tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs)**: the fifth
  build check
- **[`commercial-guide.md`](../docs/docs/commercial-guide.md)**: the operator's guide to the schema
- **[`20-contract.md`](20-contract.md)**: the I-I13 and I-I14 records

Depends on: S22.

Acceptance:
- **S23.1** A fixture project, built the way a vendor configuration package would be, contributes
  `Platform:Identity:Bearer:<name>:*` keys through a standard configuration source and references no
  Platform package.
- **S23.2** The fifth build check fails the build when a vendor configuration package references any
  Platform package. It is shown failing against a deliberately broken fixture before it is shown passing
  (I-I13).
- **S23.3** The same keys and values, supplied once through the fixture's source and once through an
  in-memory settings file, give equal validated settings. The same tokens give identical accept and
  reject results under both (I-I14).
- **S23.4** A key the fixture writes that Identity does not recognise fails startup as
  `InvalidSetting`, naming the full key, exactly as it would from a settings file.
- **S23.5** The commercial guide documents the schema: every key, its default, its constraint, and the
  error a defect produces. It shows a fixed-key, a discovery and a pattern example. It explains where a
  vendor quirk belongs, in that vendor's configuration source and never in Platform. It replaces the
  hand-registered provider example as the recommended path. The documentation build passes with no
  broken link.
- **S23.6** The I-I13 and I-I14 records move to `code` citing their tests, in the same change.

Out of scope: building or publishing any real vendor package, since D5 builds none. Also out of scope
are #94's sign-in hook, its sign-out proof and the hook documentation, which D5 does not deliver
([`90-decisions.md`](90-decisions.md), 2026-09-26).

---

## Landed

| Slice | Name | Issue | Criteria | Body complete at |
|---|---|---|---|---|
| **S19** | An untrusted credential is refused, not ignored | [#270](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/270), closed | S19.1–S19.8 | `c00c2af` |
| **S20** | An issuer with fixed keys is trusted from configuration alone | [#271](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/271), closed | S20.1–S20.12 | `bca2535` |
| **S21** | Keys are found through discovery and kept fresh | [#272](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/272), closed | S21.1–S21.10 | `60d4167` |
| **S22** | One issuer serves many customer tenants | [#273](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/273), closed | S22.1–S22.6 | `c00c2af` |

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
