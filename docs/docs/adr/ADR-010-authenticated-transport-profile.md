---
sidebar_position: 10
sidebar_label: ADR-010 Authenticated Transport Profile
---

# ADR-010: A Host That Only Authenticates Declares `AuthenticatedTransport`

## Status

Accepted. Extends the composition profile of D5 (`design/d5/90-decisions.md`, 2026-08-24 and
2026-09-03). Neither `Local` nor `Operated` changes meaning.

## Context

`CompositionProfile` had two values. `Local` is identity-free and fails startup if an authentication
provider is registered (I-C3). `Operated` requires an authentication provider and a durable audit
sink (I-C1, I-C2).

G3's slice S3 puts a host between callers and a workload that does one job: it authenticates a
bearer token against an issuer's pinned keys and forwards the authenticated principal to the
workload in a signed assertion. The game edge is that host. It has to compose Platform Identity's
real bearer provider, so `Local` refuses it. It keeps nothing about the caller, authorizes nothing,
and has no database, so `Operated` refuses it for want of a durable audit sink.

The two ways to make the edge start under the existing profiles are both false statements:

- Register a dummy authentication provider under `Local`, or a sink that claims to be durable under
  `Operated`. Either defeats the check that exists to catch a host that grew a commercial dependency
  or lost its audit trail by accident.
- Turn validation off for the edge. Startup validation is the only place the profile is enforced
  (I-C9).

The authorization decision stays with the workload. The edge answers "who is calling", never "may
they". G3 invariants 41 and 125 bind the edge: it holds no per-session state and caches nothing but
the issuer's pinned key set, and it validates tokens only against keys pinned in its configuration
and opens no connection but the one to its workload.

## Decision

Add a third value, `AuthenticatedTransport`, to `CompositionProfile`. The existing values keep their
numbers (`Local` 0, `Operated` 1, `AuthenticatedTransport` 2), so a stored or serialized profile
means what it meant.

A host declaring it **must** have:

- at least one authentication provider (I-C14).

It **must not** have, and startup fails naming the registration if it does (I-C15):

- any module not marked as transport-compatible. The mark is internal to Platform, so a Platform
  package opts in after examination, and a module added later is refused until it is;
- durable Platform state (Persistence registers a marker that the profile refuses);
- an `IAuditSink` declaring `IsDurable`;
- an `ITenantResolver`;
- an entitlement contributor other than the community baseline;
- a permission provider other than the composition provider, which on this profile grants nothing;
- an endpoint that requires a permission.

The host-compose check runs before any module's `Register` and before any hosted service is built, so
a refused module is named as itself. It is repeated at `StartingAsync` for a registration made after
composition.

`AuditClass.Required` is refused at the writer under this profile with a non-retryable error naming
it (I-C16). The profile keeps no durable trail, and a Required record that landed in the log sink
would satisfy the class in name only. A caller that needs a durable trail declares `Operated`.

The profile name joins the settings fingerprint, as the other two do.

An authentication failure on this profile is answered `401` by the operation-scope middleware with
the code `CredentialRejected` and a correlation id. Nothing is written to audit and no route runs.
That is a limit, stated here: this profile's audit guarantee is that it makes none. The workload,
which owns authorization and any durable trail, audits what it decides.

### What it does not change

- **`Local`** stays identity-free. It still rejects an authentication provider.
- **`Operated`** still requires a durable audit sink. The edge declaring `Operated` still fails
  startup for want of one.
- **Authorization** stays at the workload. A principal the edge authenticates holds no Platform
  permission.
- **No workload-specific check** exists in generic Platform code. The profile names registrations
  and capabilities, not the game edge.

### How the game edge uses it

In `Terminate` mode the edge declares `AuthenticatedTransport`, composes Platform Identity's bearer
provider with `SigningKeys` pinned and no discovery address, and signs the handoff assertion. In
`PassThrough` mode it has no provider and keeps `Local`. `GameEdge:CredentialMode` has no default, so
a deployment names its mode. A `Terminate` edge configured `Local` or `Operated` fails startup under
the existing rules (I-C3, I-C2), which the S3 proof exercises.

## Consequences

- A fourth state a reader must know exists, and a third column in every composition table. The
  composition table in `docs/docs/platform-specification.md` and the hosting guide carry it.
- A new Platform module defaults to refused on this profile. Opting it in is a code change in
  Platform, reviewed as one.
- The profile is a statement about what a host holds, not a feature switch. Runtime behaviour branches
  on it in one place only, the `Required` audit refusal in I-C16, because that is the one rule that
  cannot be checked at startup.
- A consumer that wants Identity accounts, persistence or audit on its edge does not use this
  profile. It declares `Operated`.
- Auth failures on this profile leave no Platform audit record. An operator who needs one adds a
  durable sink and moves to `Operated`.

## Alternatives rejected

**Make `Local` accept an authentication provider.** Removes the check that a local host has not grown
a commercial dependency, and gives up the `Local` guarantee the brief states.

**Make `Operated` accept a non-durable sink when the host has no persistence.** Turns the audit
requirement into one that is satisfied by saying nothing, and I-C2 says the log sink is never an
`Operated` fallback.

**A dummy provider, a sink that reports itself durable, or a no-op audit implementation.** Each
registers a thing that does not do what its contract says, in exactly the place the validator looks.

**Disable validation for the edge, or for a named workload.** Removes I-C9 for the host that has the
widest reach, and puts a workload's name into generic code.

**A flag on `Operated` that waives the audit requirement.** The flag would be one line to add to a
host that should have an audit trail.

**Give the edge Platform persistence and an audit store.** Adds a database, migrations and a trail to
a host whose invariant is that it keeps nothing about the caller (G3 invariant 41).

## References

- `design/90-decisions.md`, 2026-11-03: the S3 entries.
- `design/20-contract.md`, invariants 130 to 132.
- `design/d5/20-contract.md`, I-C14 to I-C16.
- [ADR-009](ADR-009-identity-package.md): Identity, the module the edge composes.
