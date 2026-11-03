# Contract — principals (G3)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). It is authoritative for
the artifacts and modules it describes. [`00-brief.md`](00-brief.md) stays authoritative for scope
and non-goals.

Two languages are used, unchanged from G1 and G2: **TypeScript** with `strict` for the Node
workload, and **C#** with nullable reference types for the .NET edge.

**[`g1/20-contract.md`](g1/20-contract.md) and [`g2/20-contract.md`](g2/20-contract.md) stay
authoritative for everything they declare.** This document declares only what principals add. It
names every earlier declaration it amends and never restates one.

**Invariant numbering continues G2's, from 97.** Earlier invariants that G3 amends are listed under
[*Amended invariants*](#amended-invariants), not rewritten.

Owner questions from the design's [*Open questions*](10-design.md#open-questions) are referred to
as **OQ1–OQ9**. The owner answered all nine on 2026-11-02 ([`90-decisions.md`](90-decisions.md)),
and the declarations below carry those answers. What the answers left open is under
[*Unresolved*](#unresolved).

## Invariants

| # | Invariant | Owner |
|---|---|---|
| 97 | The edge's credential mode is exactly one of `Terminate` or `PassThrough` per deployment, and startup refuses a mode whose required settings are absent | Edge |
| 98 | In both modes, a caller-supplied handoff header never reaches the workload | Credential policy |
| 99 | In both modes, `X-Forwarded-For`, `-Proto` and `-Host` are set by the edge from the connection, or from a configured trusted proxy, and caller values never pass. `Forwarded` never passes | Credential policy |
| 100 | `Cookie` and `Origin` are both forwarded or both dropped, on every request, in both modes | Credential policy |
| 101 | Every forwarded `Cookie` comes from that request alone. The edge holds no cookie state, and `A_cookie_the_workload_sets_for_one_caller_is_never_sent_on_anothers_request` passes | Forwarder |
| 102 | In terminate mode, `Authorization` is never forwarded, and `Set-Cookie` never returns to the caller | Credential policy |
| 103 | In pass-through mode, every `Set-Cookie` value returns verbatim and unrewritten, `Location` returns verbatim, and the edge produces no CORS header and answers no preflight | Credential policy |
| 104 | Each header rule holds identically through the buffered and the streamed forwarding paths | Forwarder |
| 105 | In terminate mode, an `Anonymous` principal is refused `401 CredentialRequired` before anything is forwarded | Forwarder |
| 106 | The edge mints an assertion only in terminate mode and only for a principal of kind `Account` with a non-empty issuer and subject | Handoff signer |
| 107 | An assertion binds the forwarded method and the path and query, byte for byte as forwarded, and expires no more than the configured lifetime after it is issued | Handoff signer |
| 108 | No assertion, and no bearer token, is ever written to a log, a span attribute, an error body or a metric label | Edge, Handoff verifier |
| 109 | game-service refuses every non-probe request unless its assertion verifies against a configured key id with the fixed algorithm, issuer, audience, expiry, issued-at, method, path, query, kind and non-empty owner | Handoff verifier |
| 110 | game-service has no configuration under which the verifier is off, and it refuses to start without at least one verification key | Handoff verifier, Composition |
| 111 | Every request's composition passes exactly the verifier's principal to the ownership decorator. No other module derives an owner | Composition |
| 112 | Every statement against `session`, `save`, `profile` or `profile_achievement` carries an owner predicate, and every insert stamps the owner it was given | Store |
| 113 | An owner column never appears on a `StoredSessionRecord`, a `StoredSaveRecord` or a `Profile`, and never enters a blob | Store |
| 114 | The decorated store's `serialize()` output is byte-identical to the undecorated store's for the same operation sequence | Ownership decorator, Proof harness |
| 115 | A foreign id and an unknown id produce identical responses on every operation, through both surfaces and the lifecycle probe, including when the foreign row has expired | Ownership decorator, Store |
| 116 | The lifecycle probe Dispatch uses on a request is the per-request decorated one. The process-lived probe is wrapped, not rebuilt | Composition |
| 117 | No principal can hold the reserved empty owner | Handoff verifier |
| 118 | The account surface's routes are outside the operation table and the MCP tool table, and neither surface's width changes | Account surface |
| 119 | No account-surface response carries a blob, a scene or any engine state | Account surface |
| 120 | Account deletion removes every owned row of the four tables in one transaction, or none of them | Store, Account surface |
| 121 | The abuse limiter keys on the principal when one is established, and on the client address otherwise. A limited request is answered `429` with `Retry-After` and is never forwarded | Abuse limiter |
| 122 | The port-conformance suite (G2 invariant 89) runs every assertion under two owners on both targets, and asserts that one owner's rows are invisible to the other | Proof harness |
| 123 | G3's CI evidence uses the real edge, the real game-service, Postgres and a test issuer signing real tokens. The edge pins the test issuer's public keys, and Adventures' proofs use the same issuer's discovery document. No mocked authentication replaces the chain | Proof harness |
| 124 | G2's harnesses and replay present assertions minted with a test key. Their assertions about game state are unchanged | Proof harness |
| 125 | In terminate mode, the edge validates bearer tokens only against issuer keys pinned in its configuration, and startup refuses a discovery address, and a provider with no pinned keys, before any issuer is contacted. In steady state the edge opens no outbound connection except to its workload, and game-service none except to its store | Edge, Composition |
| 126 | Account deletion changes nothing outside game-service's owned rows: no Platform account mapping, sign-in, credential, issuer account or workload data. It keeps no copy of what it deleted | Account surface, Store |
| 127 | A write that commits after the deletion transaction belongs to a new, empty account under the same owner. Nothing refuses it | Store |
| 128 | The G3 migration deletes every `session`, `save`, `profile` and `profile_achievement` row present when it runs, in the same run that adds the owner columns | Migrations |
| 129 | An MCP tool call reaches game-service only through the edge, and every tool carrying a session, save or profile id is refused another principal's id exactly as the matching HTTP operation is | Handoff verifier, Ownership decorator |
| 130 | A host declaring `AuthenticatedTransport` requires an authentication provider, and refuses at startup, naming the registration, any module not marked transport-compatible, durable Platform state, a durable audit sink, a tenant resolver, a non-baseline entitlement contributor, a non-composition permission provider and an endpoint requiring a permission. The module and durable-state refusals run before any module registers | Composition |
| 131 | Under `AuthenticatedTransport`, a write of `AuditClass.Required` is refused with a non-retryable error before any sink sees it, and an authentication failure is answered `401 CredentialRejected` with no audit write and no route run. The edge makes no audit claim | Composition, Edge |
| 132 | `Local` still refuses an authentication provider and `Operated` still refuses a missing durable sink. A terminate-mode edge declaring either fails startup, and nothing is registered or disabled to make it start | Composition, Edge |

## Amended invariants

| # | Was | Now |
|---|---|---|
| 41 (G1) | The edge holds no per-session state, no connection affinity and no cache, and never reorders, batches or coalesces | The edge holds no per-session state, no connection affinity and no response cache, and never reorders, batches or coalesces. Its only process state is Identity's key set and the abuse limiter's counters, and neither changes the bytes of any forwarded request or response |

Invariant 41's process state is structural on the edge: the edge declares `AuthenticatedTransport`
(invariant 130, [ADR-010](../docs/docs/adr/ADR-010-authenticated-transport-profile.md)), so it composes
no Platform persistence, no account store and no durable trail. Invariant 125 is unchanged: the pinned
keys are `BearerSettings.SigningKeys`, read from configuration, and the profile adds no connection.

**G2 prose amended, not an invariant:** [`g2/20-contract.md`](g2/20-contract.md) says that in the
per-request composition, "the lifecycle member's result is discarded". Under invariant 116 it is
used instead. Invariants 69 and 74 hold as written.

## Public surface

Signatures only. Shapes already in the tree are not restated.

### Edge configuration (C#)

```csharp
public enum CredentialMode { Terminate, PassThrough }

public sealed record HandoffOptions(
    string Issuer, string Audience, string KeyId, string SigningKeyPath, TimeSpan Lifetime);

public sealed record AbuseLimitOptions(int PermitCount, TimeSpan Window);

// Added to the existing GameEdgeOptions:
//   CredentialMode CredentialMode
//   HandoffOptions? Handoff          — required in Terminate, refused in PassThrough
//   AbuseLimitOptions AbuseLimits    — default PermitCount 60, Window 60 seconds
//   IReadOnlyList<IPAddress> TrustedProxies
```

In terminate mode, the edge's bearer settings are Identity's existing `BearerSettings` with
`SigningKeys` set and `Discovery` absent (invariant 125). Identity itself accepts `Discovery`, and
fetches the issuer's keys while the host starts, so the edge enforces the rule itself: before it registers
Identity, it refuses a provider that sets `Discovery`, even beside `SigningKeys`, or sets no `SigningKeys`.
The refusal names the full key (`Platform:Identity:Bearer:<name>:Discovery` or `:SigningKeys`).
`CredentialMode` has no default: a deployment names it, and startup names the setting when it is
missing. In `Terminate` the edge declares `Platform:CompositionProfile` as `AuthenticatedTransport`
(invariant 130). `PassThrough` composes no provider and keeps `Local`.
`HandoffOptions.Lifetime` must not exceed game-service's `maxLifetimeSeconds`.

### The handoff (wire)

```text
Request header:  SubZeroDev-Principal-Assertion: <compact JWS>
JWS header:      { "alg": "ES256", "kid": <key id>, "typ": "szd-principal+jwt" }
JWS payload:     { "iss", "aud", "pis", "psub", "pkind", "htm", "htu", "rid", "iat", "exp" }
```

`pis` and `psub` are the principal's issuer and subject. `pkind` is `"Account"`. `htm` is the
method. `htu` is the path and query exactly as forwarded. `rid` is the request's correlation id.
`iat` and `exp` are seconds since the epoch.

### game-service (TypeScript)

```ts
export interface OwnerKey { readonly issuer: string; readonly subject: string; }

export interface HandoffSettings {
  readonly issuer: string;
  readonly audience: string;
  readonly keys: ReadonlyMap<string, KeyObject>;   // by key id
  readonly clockToleranceSeconds: number;
  readonly maxLifetimeSeconds: number;             // exp − iat above this is lifetime_exceeded
}

export interface IncomingRequestLine {
  readonly method: string;
  readonly pathAndQuery: string;                   // exactly as received
  readonly assertion: string | undefined;          // the SubZeroDev-Principal-Assertion value
}

export type HandoffRefusal =
  | "missing" | "malformed" | "unknown_key" | "bad_signature" | "wrong_algorithm"
  | "wrong_issuer" | "wrong_audience" | "expired" | "not_yet_valid" | "lifetime_exceeded"
  | "request_mismatch" | "unsupported_kind" | "empty_owner";

export function verifyHandoff(
  settings: HandoffSettings, request: IncomingRequestLine, now: Date,
): Outcome<OwnerKey, HandoffRefusal>;
```

The verifier reads nothing but `IncomingRequestLine`.

**The Store's owner scope.** The Store takes the owner as a parameter and puts it in every statement
(invariant 112). It never sees a principal. The ownership decorator is the only caller that supplies
an owner (invariant 111).

```ts
export interface OwnedLifecycleProbe {             // process-lived (G2 invariant 69)
  session(owner: OwnerKey, sessionId: string): Promise<Outcome<LifecycleState, StoreError>>;
  save(owner: OwnerKey, saveId: string): Promise<Outcome<LifecycleState, StoreError>>;
}

export interface AccountStore {                    // bound to one owner
  listSaves(): Promise<Outcome<readonly AccountSaveEntry[], StoreError>>;   // across every profile
  deleteAll(): Promise<Outcome<void, StoreError>>;  // one transaction (invariant 120)
}

export interface OwnerScopedStorage {              // implemented by the durable and in-memory stores
  persistence(owner: OwnerKey): SessionPersistence;
  profiles(owner: OwnerKey): ProfileStore;
  readonly lifecycle: OwnedLifecycleProbe;
  account(owner: OwnerKey): AccountStore;
}

// Replaces the owner-only form above. The returned decorator replaces each port of the seam it
// is given with that port bound to the owner. The lifecycle port is a view over the process-lived
// OwnedLifecycleProbe, never a new probe (invariant 116).
export function ownershipDecorator(storage: OwnerScopedStorage, owner: OwnerKey): StorageDecorator;

export function ownedAccount(storage: OwnerScopedStorage, owner: OwnerKey): AccountStore;

// StoreProvider gains the owner; G2's no-argument form is removed:
//   forRequest(owner: OwnerKey): StorageSeam
//   forAccount(owner: OwnerKey): AccountStore

// DurableStore (G2) is amended:
//   persistenceForRequest(): SessionPersistence   removed
//   readonly profiles: ProfileStore               removed
//   readonly lifecycle: LifecycleProbe            becomes  readonly storage: OwnerScopedStorage
```

**Port conformance under two owners** (invariant 122). G2's `ConformanceTarget` is amended:

```ts
export interface ConformanceTarget {
  readonly label: "in-memory" | "durable";
  readonly storage: OwnerScopedStorage;            // replaces persistence and profiles
  seedCorruptProfile(owner: OwnerKey, profileId: string): Promise<void>;
  seedProfileWriteFailure(owner: OwnerKey, profileId: string): Promise<void>;
}

export const CONFORMANCE_OWNERS: readonly [OwnerKey, OwnerKey];
```

`runPortConformance(target)` keeps its signature. It runs every assertion under each of
`CONFORMANCE_OWNERS`, and after each one asserts that the other owner's ports see none of its rows.

### Proof harness (TypeScript, test support only)

Never imported by `src/`. It mints the assertions G2's harnesses and replay present
(invariant 124), and the malformed ones S1's refusal tests need.

```ts
export interface TestSigningKey { readonly keyId: string; readonly privateKey: KeyObject; }

export interface AssertionClaims {
  readonly iss: string; readonly aud: string;
  readonly pis: string; readonly psub: string; readonly pkind: string;
  readonly htm: string; readonly htu: string; readonly rid: string;
  readonly iat: number; readonly exp: number;
}

export function mintTestAssertion(
  key: TestSigningKey, owner: OwnerKey, request: IncomingRequestLine, now: Date,
  overrides?: Partial<AssertionClaims>,
): string;
```

### Account surface (HTTP, on game-service, reached through the edge)

```text
GET    /account/saves   → 200 { "saves": AccountSaveEntry[] }
DELETE /account         → 204
```

```ts
export interface AccountSaveEntry {
  readonly saveId: string;
  readonly campaignId: string;
  readonly profileId: string | null;
  readonly savedAtSeq: number;
  readonly savedAt: string;    // row_created_at, ISO 8601
  readonly expiresAt: string;  // ISO 8601
}
```

## Persisted schemas

One migration, after G2's last. It follows G2's rule for every migration after the first.

| Table | Column | Type | Null | Owner |
|---|---|---|---|---|
| `session` | `owner_issuer`, `owner_subject` | `text` | not null | Host |
| `save` | `owner_issuer`, `owner_subject` | `text` | not null | Host |
| `profile` | `owner_issuer`, `owner_subject` | `text` | not null | Host |
| `profile_achievement` | `owner_issuer`, `owner_subject` | `text` | not null | Host |

- **Keys.** `session` and `save` keep their primary keys. `profile` becomes
  `(tenant_id, owner_issuer, owner_subject, profile_id)`. `profile_achievement` becomes
  `(tenant_id, owner_issuer, owner_subject, profile_id, campaign_id, achievement_id)`.
- **Indexes.** `(tenant_id, owner_issuer, owner_subject)` on `session` and on `save`, for the account
  surface and for deletion.
- **Existing rows.** The migration adds the owner columns with default `''`, then deletes every row
  present (invariant 128). A row a still-running G2 instance writes afterwards takes the reserved
  empty owner, which no principal can hold (invariant 117).
- **Platform §7 obligations.** The tenant column stays a primary-key member on every table. The
  owner is host-owned and never reaches the engine (invariant 113).

## Error semantics

| Condition | Answered by | Status | Code | Retryable | Readiness |
|---|---|---|---|---|---|
| No credential, terminate mode | Edge | 401 | `CredentialRequired` | No | — |
| Token rejected, expired or malformed | Edge, Platform pipeline | 401 | `CredentialRejected` | No | — |
| Token signed by a key not pinned, including after the issuer rotates | Edge, Platform pipeline | 401 | `CredentialRejected` | No | — |
| Provider failure | Edge, Platform pipeline | 401 | `ProviderFailed` | No | — |
| Pinned keys absent, or a discovery address set, terminate mode | Edge startup | — | — | — | The edge does not start, naming the setting, before any connection to the issuer |
| Abuse limit reached | Edge | 429 | `RateLimited`, with `Retry-After` | After the delay | — |
| Assertion missing or invalid | game-service | 401 | `assertion_refused` | No | — |
| Foreign or unknown session or save | game-service | 404 | `unknown_session` / `unknown_save` | No | — |
| Store unreachable | game-service | 503 | `storage_failure` | Unchanged from G2 | Unchanged from G2 |

**Refusal and infrastructure failure are distinguished by code, not status.** Platform's
`AuthenticationError` contract maps every authentication failure to unauthenticated, "never a server
error", and marks none retryable. G3 keeps that contract instead of overriding it. The brief's
"distinguish refusal from an infrastructure failure" is met by the code: `ProviderFailed` is
infrastructure, and `CredentialRejected` and `CredentialRequired` are refusals.
`KeyMaterialUnavailable` cannot occur, because the keys are pinned (invariant 125).

**`assertion_refused` carries no reason.** Only a bypass attempt or an edge defect can produce it.
The reason (`HandoffRefusal`) goes to the log only.

**Sign-out under bearer.** A game-service bearer token stays valid until it expires. The edge has no
sign-out, and G3 does not introspect tokens. The sign-out test asserts exactly that outcome.
Adventures' sign-out is its own, and it passes through.

## Unresolved

- **The live game-service hostname.** Not yet chosen. It is a deployment value and the smoke
  check's target, not a declaration above.
- **Owner question: the game-service client at Adventures' issuer.** Which client registration
  obtains tokens for game-service, and so the audience the edge accepts. Adventures' own client is
  confidential and keeps its tokens server-side, so it does not serve a game-service caller.

Each deployment's `TrustedProxies` holds the existing reverse proxy's address. Rotating the issuer's keys is an operator step: the new key is pinned before the
issuer signs with it.
