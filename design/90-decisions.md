# Decision log — G3 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g2/90-decisions.md`](g2/90-decisions.md), [`g1/90-decisions.md`](g1/90-decisions.md),
[`d5/90-decisions.md`](d5/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

## Open

The owner's questions, numbered as in [`10-design.md`](10-design.md#open-questions). None is settled
by any entry below.

1. Whether ownership on the MCP path is in G3's scope.
2. What account deletion removes beyond game-service's owned rows, and what it guarantees after.
3. Whether pre-G3 ownerless rows are deleted at migration or left unreachable.
4. Which Adventures routes move behind the edge.
5. Live cutover and topology: proxy position, deployment ownership, existing sign-ins, rollback,
   downtime.
6. Whether G2's fully-offline steady state still binds G3.
7. Whether G3 requires a live game-service deployment.
8. Abuse-limit values, key, enforcement topology, edge instance count and any capacity commitment.
9. Whether verifying one Platform-issued key counts as adopting Platform's Identity.

---

### 2026-11-01 — The process-lived lifecycle probe is wrapped per request, and the wrapped one is used

Context: G2 composes the probe once, process-lived (invariant 69), and in the per-request
composition discards the lifecycle member's result. Under ownership, an undecorated probe answers
`expired` for any caller's id — an existence oracle for foreign sessions.
Chosen: each request's composition passes the process-lived probe through the seam with the live
ports, and Dispatch uses the decorated result (invariant 116). The probe is not rebuilt. G2's
"discarded" prose is amended; invariants 69 and 74 hold.
Rejected: **rebuilding the probe per request** — breaks invariant 69 and its pool and schema
guarantees. **Leaving the probe undecorated and filtering its answer in Dispatch** — puts an owner
in Dispatch, which must never see a principal.
Reversibility: cheap

### 2026-11-01 — Invariant 41 is restated to admit key material and abuse-limit counters

Context: G1's invariant 41 forbids the edge any cache. Terminating credentials needs Identity's
cached key set, and abuse limits need counters.
Chosen: the edge's only process state is the key set and the limiter's counters, and neither changes
the bytes of any forwarded request or response. No per-session state, affinity or response cache.
Rejected: **keeping 41 verbatim and moving limits to the reverse proxy** — leaves the brief's abuse
limits outside anything CI can exercise. **Dropping 41** — loses the guarantee that forwarding never
mixes callers, which #340's cookie pass-through depends on.
Reversibility: cheap

### 2026-11-01 — The owner key is the principal id as established, and the game-service edge composes no account module

Context: with `IdentityAccountsModule` composed, a principal that later creates a Platform account
authenticates as `(platform.account, <id>)`, so every row keyed by its raw pair would become
unreachable.
Chosen: the owner key is `(issuer, subject)` exactly as the bearer chain establishes it; only kind
`Account` may own state; the game-service edge does not compose the account module in G3.
Rejected: **keying on the Platform account id** — requires explicit account creation before any
game operation, and pulls account lifecycle into G3 ahead of Open question 2. **Hashing or deriving
the key** — adds nothing the pair lacks and makes the store unreadable against the issuer.
Reversibility: expensive — changing the key later means re-keying every owned row.

### 2026-11-01 — G3's evidence composes the real edge, game-service, Postgres and a test issuer

Context: the brief's live proof is Adventures; the owner required that CI exercise the integration
rather than substitute mocked authentication.
Chosen: CI runs a test issuer serving a real discovery document and key set and signing real
tokens, behind the real edge, against the real game-service and Postgres (invariant 123).
Adventures' proofs run at a pinned revision pointed at the same issuer. A live game-service
deployment is Open question 7.
Rejected: **an injected principal or a stub authentication provider** — proves the decorator, not
the boundary. **A hosted identity provider in CI** — an external dependency and a secret in every
run, for no assurance the test issuer lacks.
Reversibility: cheap

### 2026-11-01 — game-service has no unauthenticated mode

Context: local runs and G2's harnesses would be simpler with the verifier off.
Chosen: no configuration disables the verifier, and game-service refuses to start without a
verification key (invariant 110). Harnesses and replay sign with a test key (invariant 124).
Rejected: **an environment switch** — a bypass with a configuration value in front of it, and the
first thing a misconfigured deployment would ship with. **Loopback-only exemption** — every process on the host
shares loopback, so it is a network position, not a credential.
Reversibility: cheap

### 2026-11-01 — Abuse limits are in-process at the edge, keyed by principal, else by address

Context: one VPS is the baseline; instance count and capacity are owner questions.
Chosen: an in-process limiter per edge instance, after authentication, keyed by principal or by
client address; `429` with `Retry-After`, never forwarded (invariant 121). Values are Open question
8. A second instance multiplies the effective limit; ownership is unaffected.
Rejected: **a shared limiter store** — a new stateful dependency for a capacity commitment nobody
has made. **Limits only at the reverse proxy** — untestable from a fresh clone and blind to the
principal.
Reversibility: cheap

### 2026-11-01 — Authentication failures keep Platform's mapping

Context: the brief asks that refusal be distinguished from infrastructure failure. Platform's
`AuthenticationError` maps every failure to unauthenticated, never a server error, and none is
retryable.
Chosen: keep `401` with distinct codes — `CredentialRejected` and the new `CredentialRequired` for
refusals, `KeyMaterialUnavailable` and `ProviderFailed` for infrastructure — with Identity's key-set
readiness check reporting degradation.
Rejected: **`503` for infrastructure failures** — overrides a Platform-wide contract for one
workload and changes every other host's behaviour. **One undifferentiated `401`** — fails the brief.
Reversibility: cheap

### 2026-11-01 — The account surface is two routes on game-service, outside the operation and tool tables

Context: the brief requires listing a principal's saves and deleting its account; §5 fixes the game
surface's width.
Chosen: `GET /account/saves` and `DELETE /account` on game-service behind the same verifier, reading
through the per-request decorated store, never through Dispatch or the engine (invariants 118–120).
The listing spans every profile and never carries engine state. Deletion's floor is one transaction
over the four owned tables; anything beyond is Open question 2.
Rejected: **new game operations** — widens the contract the engine owns. **A Platform .NET account
module reading the game store** — a second store consumer across the runtime boundary G2 keeps.
**Reusing `list-saves` per profile** — requires the caller to know every profile it ever named.
Reversibility: cheap

### 2026-11-01 — game-service's initial contract is bearer-only

Context: the owner asked why bearer is the right first contract without excluding browsers.
Chosen: game-service accepts only a bearer token at the edge. A bearer token is not ambient, so the
contract has no CSRF or CORS surface; terminate mode drops `Cookie` and `Origin` together and never
returns `Set-Cookie`.
Rejected: **a cookie session first** — no browser client exists, and it brings anti-forgery and CORS
into scope for no caller. Browsers remain addable later through the SignIn module's cookie; only the
edge's authentication step changes, not the handoff.
Reversibility: cheap

### 2026-11-01 — A foreign id is indistinguishable from an unknown one

Context: [`d5/90-decisions.md`](d5/90-decisions.md) 2026-08-24 tells a caller who may not see a
resource that it does not exist. The Adventures POC answers foreign ids `403`.
Chosen: identical responses on every operation, both surfaces and the lifecycle probe, including for
a foreign expired session (invariant 115). It holds by construction: the owner predicate finds no
row.
Rejected: **`404` unknown, `403` foreign** — an existence oracle on every id. **A new wire code for
cross-principal refusal** — the engine's contract has none, and adding one reveals existence.
Reversibility: cheap

### 2026-11-01 — Ownership is host-owned owner columns enforced in the store, applied through the seam decorator

Context: the ports carry no owner and must not (G2 invariants 48, 49); `profileId` is caller-chosen;
§6.3 requires the store's paths be decorated.
Chosen: `owner_issuer` and `owner_subject` on all four tables, in every predicate and stamped on
every insert (invariant 112). Profiles are namespaced by owner. The store executes under an owner
scope; the decorator is the only module that maps a principal to one (invariant 111). Pre-G3 rows
take a reserved empty owner pending Open question 3.
Rejected: **a separate ownership ledger** — claim and write become two statements, leaving unowned
rows and orphan claims. **Binding `profileId` to a principal-derived key** — changes caller input and
needs a refusal the engine lacks. **Owner in the blob** — breaks invariant 49 and byte identity.
**A contract-aware edge checking ownership** — reverses
[`d5/90-decisions.md`](d5/90-decisions.md) 2026-10-17, "the edge still reads no contract".
Reversibility: expensive — a schema migration and a key change on two tables.

### 2026-11-01 — The edge's credential handling is a mode per deployment: terminate or pass-through

Context: one edge deployment serves one workload. game-service needs Platform authentication;
Adventures authenticates with its own cookie session and needs guest sessions, sign-in and
cross-origin writes to keep working (#340).
Chosen: `Terminate` for game-service — consume the bearer, drop `Cookie` and `Origin`, mint the
handoff. `PassThrough` for Adventures — forward `Cookie` and `Origin` together, return every
`Set-Cookie` verbatim, forward CORS and preflight from the workload. Both modes strip hop-by-hop
headers, caller forwarding headers and any caller handoff header (invariants 97–104).
Rejected: **one policy for every workload** — either breaks Adventures' cookies or forwards cookies
to a workload that must never see them. **Edge-side cookie rewriting** — makes the edge hold a view
of each workload's session, contrary to invariant 41.
Reversibility: cheap

### 2026-11-01 — The edge hands the principal to game-service as a signed, request-bound assertion

Context: the owner chose this approach at the design gate. Shared proxy networks and alternate
ingress routes, including today's direct route, mean network position is not a credential.
Chosen: approach **B**. The edge mints a short-lived compact JWS (ES256) binding issuer, audience,
principal, kind, method, path and query, request id and expiry; game-service verifies it against
configured public keys by key id and refuses everything else with `401 assertion_refused`
(invariants 106–110). A compromised host or malicious operator is proposed out of scope.
Rejected: **A, an unsigned principal header** — the guarantee rests on network wiring CI cannot
prove, and one route defeats it silently. **C, pass-through with a new .NET resolver** — a new
process and a per-request cross-runtime dependency on every game operation. **game-service
validating bearer tokens itself** — a second authentication stack, which the brief rules out.
**Mutual TLS** — authenticates the edge, not the principal, and still needs a principal header.
Reversibility: expensive — every workload deployment and harness carries the verifier and its keys.
