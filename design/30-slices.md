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
- **S16 — The administration shell** — shipped:
  [#184](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/184) via
  [#223](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/223).
- **S17 — The proof: two databases, two hosts, contention and offline** — shipped:
  [#185](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/185) via
  [#226](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/226), [#227](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/227), [#228](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/228), [#231](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/231).
- **S18 — Packages, documentation and the corrected plan** — shipped:
  [#186](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/186) via
  [#233](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/233).

---

## Outstanding

None. All D5 slices have landed; their closed issues retain the acceptance criteria.
