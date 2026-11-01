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
as **OQ1–OQ9**. Nothing below settles one. Where a declaration depends on an answer, it says so
under [*Unresolved*](#unresolved).

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
| 123 | G3's CI evidence uses the real edge, the real game-service, Postgres and a test issuer serving a real discovery document and key set. No mocked authentication replaces the chain | Proof harness |
| 124 | G2's harnesses and replay present assertions minted with a test key. Their assertions about game state are unchanged | Proof harness |

## Amended invariants

| # | Was | Now |
|---|---|---|
| 41 (G1) | The edge holds no per-session state, no connection affinity and no cache, and never reorders, batches or coalesces | The edge holds no per-session state, no connection affinity and no response cache, and never reorders, batches or coalesces. Its only process state is Identity's key set and the abuse limiter's counters, and neither changes the bytes of any forwarded request or response |

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
//   AbuseLimitOptions AbuseLimits
//   IReadOnlyList<IPAddress> TrustedProxies
```

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
}

export type HandoffRefusal =
  | "missing" | "malformed" | "unknown_key" | "bad_signature" | "wrong_algorithm"
  | "wrong_issuer" | "wrong_audience" | "expired" | "not_yet_valid" | "lifetime_exceeded"
  | "request_mismatch" | "unsupported_kind" | "empty_owner";

export function verifyHandoff(
  settings: HandoffSettings, request: IncomingRequestLine, now: Date,
): Outcome<OwnerKey, HandoffRefusal>;

export function ownershipDecorator(owner: OwnerKey): StorageDecorator;

// StoreProvider gains the owner; G2's no-argument form is removed:
//   forRequest(owner: OwnerKey): StorageSeam
```

`IncomingRequestLine` carries the method, the path and query, and the header value. The verifier
reads nothing else.

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
- **Existing rows.** They take the reserved empty owner, `''` and `''`. Whether the migration
  instead deletes them is OQ3.
- **Platform §7 obligations.** The tenant column stays a primary-key member on every table. The
  owner is host-owned and never reaches the engine (invariant 113).

## Error semantics

| Condition | Answered by | Status | Code | Retryable | Readiness |
|---|---|---|---|---|---|
| No credential, terminate mode | Edge | 401 | `CredentialRequired` | No | — |
| Token rejected, expired or malformed | Edge, Platform pipeline | 401 | `CredentialRejected` | No | — |
| No usable key material | Edge, Platform pipeline | 401 | `KeyMaterialUnavailable` | No | Unhealthy |
| Provider failure | Edge, Platform pipeline | 401 | `ProviderFailed` | No | — |
| Last key fetch failed, previous set in use | — (requests keep validating) | — | — | — | Degraded |
| Abuse limit reached | Edge | 429 | `RateLimited`, with `Retry-After` | After the delay | — |
| Assertion missing or invalid | game-service | 401 | `assertion_refused` | No | — |
| Foreign or unknown session or save | game-service | 404 | `unknown_session` / `unknown_save` | No | — |
| Store unreachable | game-service | 503 | `storage_failure` | Unchanged from G2 | Unchanged from G2 |

**Refusal and infrastructure failure are distinguished by code, not status.** Platform's
`AuthenticationError` contract maps every authentication failure to unauthenticated, "never a server
error", and marks none retryable. G3 keeps that contract instead of overriding it. The brief's
"distinguish refusal from an infrastructure failure" is met by the code, with Identity's key-set
readiness check reporting the key set's state:
`KeyMaterialUnavailable` and `ProviderFailed` are infrastructure, and `CredentialRejected` and
`CredentialRequired` are refusals.

**`assertion_refused` carries no reason.** Only a bypass attempt or an edge defect can produce it.
The reason (`HandoffRefusal`) goes to the log only.

**Sign-out under bearer.** A game-service bearer token stays valid until it expires. The edge has no
sign-out, and G3 does not introspect tokens. The sign-out test asserts exactly that outcome.
Adventures' sign-out is its own, and it passes through.

## Unresolved

- **OQ1, MCP.** Whether the MCP transport's paths are reached only through the edge in G3's
  deployments. Invariant 109 gates them either way. What is unresolved is whether that is the
  intended coverage or a documented unavailability.
- **OQ2, account deletion.** Anything beyond invariant 120's floor: workload data, Platform account
  mappings, credentials, the issuer, reversibility, in-flight writes and post-deletion creations.
  The deletion tests' expected outcomes wait on it.
- **OQ3, pre-G3 rows.** Whether the migration keeps them under the reserved empty owner or deletes
  them.
- **OQ5, cutover.** The trusted-proxy list, whether pass-through serves the workload's public host
  (the precondition of invariant 103's "unrewritten"), and rollback.
- **OQ6, offline.** Whether background key-set refresh is acceptable.
- **OQ8, abuse limits.** The values in `AbuseLimitOptions`, the instance count, and whether limiting
  before authentication is required.
- **OQ9, Identity.** Whether verifying a Platform-issued key satisfies "adopts Platform's Identity".
- **OQ4 and OQ7** change no declaration above. They change which routes and deployments the
  evidence covers.
