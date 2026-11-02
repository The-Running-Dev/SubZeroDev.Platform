# Decision log — G3 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g2/90-decisions.md`](g2/90-decisions.md), [`g1/90-decisions.md`](g1/90-decisions.md),
[`d5/90-decisions.md`](d5/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

## Open

The owner answered Open questions 1–9 on 2026-11-02 (entries below). Still open, and settled by no
entry below:

- The live game-service hostname.
- Which client registration at Adventures' OIDC issuer obtains tokens for game-service, and so the
  audience the edge accepts.

---

### 2026-11-02 — S1: The gate writes `assertion_refused` itself, outside the vendored status mapping

Context: The contract's error table gives `assertion_refused` a `401`, but the vendored
service-contract 0.8.0 package has no row for it, and its status mapping has no fallback.
Chosen: the gate writes the `{ code, correlation }` envelope and the `401` directly, in the same
canonical encoding every wire error uses.
Rejected: **adding the row to the vendored package** — a service-contract release for a code no
operation returns. **Routing it through `wireError`** — fails loudly on the unmapped code.
Reversibility: cheap

### 2026-11-02 — S1: Verification keys are a directory of `<key id>.pem` files

Context: The contract fixes `HandoffSettings` but not how an operator supplies it.
Chosen: `GAME_SERVICE_HANDOFF_KEYS_DIR` names a directory with one P-256 public key per
`<key id>.pem` file. `GAME_SERVICE_HANDOFF_ISSUER` and `GAME_SERVICE_HANDOFF_AUDIENCE` are required.
`GAME_SERVICE_HANDOFF_CLOCK_TOLERANCE_SECONDS` defaults to 30 and
`GAME_SERVICE_HANDOFF_MAX_LIFETIME_SECONDS` to 60. Any unreadable or non-P-256 file refuses the whole
set.
Rejected: **one key in one variable** — no way to configure several key ids for rotation. **Skipping
a bad file** — a rotation that silently did not happen.
Reversibility: cheap

### 2026-11-02 — S1: A test signing proxy stands in for the edge's signer until S3

Context: S1.8 requires G2's edge-fronted replay to pass with assertions, but the edge signs nothing
until S3.
Chosen: the edge-fronted harness puts a test-only proxy between the edge and the workload. The proxy
drops any caller assertion and adds a test-key assertion bound to each forwarded request line.
Rejected: **skipping the edge-fronted proofs until S3** — S1.8 names them. **Letting the edge pass a
caller-supplied assertion through** — a pass-through S3 would have to remove.
Reversibility: cheap

### 2026-11-02 — S1: A wrong `typ` and an expiry at or before issue are `malformed`

Context: `HandoffRefusal` has no value for a token of another type or for `exp <= iat`.
Chosen: both are `malformed`. A token of another type is not a handoff assertion at all, and an
expiry at or before issue is not a lifetime of any length.
Rejected: **`unsupported_kind` for `typ`** — that value is about `pkind`. **`lifetime_exceeded` for
`exp <= iat`** — no limit was exceeded.
Reversibility: cheap

### 2026-11-02 — S1: The documented start generates a throwaway verification key

Context: Invariant 110 refuses to start without a key, and the README's and CI's documented start
commands had none. Nothing signs for the workload until S3.
Chosen: the documented start generates a P-256 key pair with `openssl`, keeps the public key and
discards the private one. Until S3 the documented workload answers only its probes.
Rejected: **a key committed to the repository** — a published private key is a bypass. **A
development off switch** — invariant 110.
Reversibility: cheap

### 2026-11-02 — Verifying Platform's bearer chain counts as adopting Platform's Identity

Context: Open question 9. The edge composes Identity's bearer authentication; game-service verifies
only the edge's handoff.
Chosen: yes, as the owner ruled. G3 adopts Identity through the edge's bearer chain and composes no
other Identity module.
Rejected: **composing Identity's account module as well** — re-keys owners when an account is created
(2026-11-01, owner key). **game-service validating bearer tokens itself** — a second authentication
stack (2026-11-01, handoff).
Reversibility: cheap

### 2026-11-02 — Abuse limits: 60 requests per 60 seconds per principal, else per address, in process

Context: Open question 8. The limiter's placement was decided on 2026-11-01; its values, key order
and topology were not.
Chosen: one edge instance per workload, limited in process, keyed by principal and otherwise by
client address, never before authentication. Shipped default 60 requests per 60 seconds. No shared
enforcement and no capacity commitment.
Rejected: **limiting before authentication as well** — counts refused credentials against an address
for no ownership benefit. **A shared limiter for several instances** — a stateful dependency for an
instance count nobody plans. **A stated capacity commitment** — nothing measures it yet.
Reversibility: cheap

### 2026-11-02 — game-service is deployed live, on the same VPS under its own hostname

Context: Open question 7. G3's CI evidence was decided on 2026-11-01; whether a live game-service is
also required was not.
Chosen: yes. game-service runs behind its own edge on the existing VPS, under its own hostname, and
signs players in through Adventures' OIDC issuer. The hostname is not yet chosen, nor is the client
registration that obtains its tokens.
Rejected: **CI evidence only** — leaves the terminate path unproven outside CI, with only
Adventures' pass-through live. **A separate host** — a second machine for one workload.
Reversibility: cheap

### 2026-11-02 — G2's offline steady state binds: the edge pins the issuer's keys and fetches nothing

Context: Open question 6. Background key-set refresh from a discovery document is an outbound
dependency in steady state.
Chosen: it binds. In terminate mode the edge uses Identity's `BearerSettings.SigningKeys`, and startup
refuses a discovery address (invariant 125). Rotation is an operator step: pin the new key before the
issuer signs with it. This supersedes the key-set readiness reporting in 2026-11-01, "Authentication
failures keep Platform's mapping"; `KeyMaterialUnavailable` cannot occur with pinned keys.
Rejected: **discovery with background refresh** — an outbound dependency on every edge. **Discovery
once at startup** — still a startup dependency on the issuer, and a stale set after rotation
anyway.
Reversibility: cheap — Identity accepts either key source.

### 2026-11-02 — Cutover: the edge sits behind the existing reverse proxy, and Platform deploys it

Context: Open question 5.
Chosen: the existing reverse proxy keeps TLS and public routing and routes Adventures' API host to
the edge, and the edge lists the proxy in `TrustedProxies`. Platform owns the edge's deployment. Existing
Adventures sign-ins continue, with no downtime. Rollback restores the proxy's direct route to
Adventures.
Rejected: **the edge in front of the proxy, terminating TLS** — moves certificates into the edge for
no G3 requirement. **Adventures owning the edge's deployment** — splits one component's release
across repositories. **Forcing every player to sign in again** — pass-through keeps the cookie, so
nothing requires it.
Reversibility: cheap — rollback is one proxy route.

### 2026-11-02 — All of Adventures' API moves behind the edge

Context: Open question 4. #340 names the signed-in routes that bypass the edge today.
Chosen: every Adventures API route is served through the edge, so no direct route remains after
cutover.
Rejected: **only #340's routes** — leaves a direct route beside the edge, the bypass the brief closes.
Reversibility: cheap

### 2026-11-02 — Pre-G3 rows are deleted at migration

Context: Open question 3. The design recommended leaving them unreachable under the reserved empty
owner.
Chosen: the G3 migration deletes every row of the four tables present when it runs (invariant 128).
The reserved empty owner remains, for rows a still-running G2 instance writes afterwards.
Rejected: **leaving them unreachable** — keeps data no player can reach or delete.
Reversibility: one-way — deleted rows are not recoverable.

### 2026-11-02 — Account deletion removes game-service's owned rows only, irreversibly

Context: Open question 2. Invariant 120 fixed the floor; everything beyond it was open.
Chosen: deletion removes game-service's owned rows in one transaction and nothing else: no Platform
account mapping, sign-in, credential, issuer account or Adventures data (invariant 126). It keeps no
copy. A write committed before deletion is deleted with the rest; a later write by a still-valid
credential starts a new, empty account (invariant 127).
Rejected: **also revoking sign-ins or credentials** — G3 holds neither, and bearer tokens are not
introspected. **A recovery window** — a retained copy of what the player asked to remove.
**Refusing writes after deletion** — needs a tombstone keyed by owner, which is itself retained
data.
Reversibility: cheap to change the rule; each deletion is one-way.

### 2026-11-02 — MCP tool calls are in G3's ownership scope

Context: Open question 1. G1 named MCP as bypassing the edge.
Chosen: in scope. MCP reaches game-service only through the edge, and each id-bearing tool is
refused a foreign id exactly as the matching HTTP operation is (invariant 129).
Rejected: **documenting MCP as unavailable in G3** — leaves "every operation carrying an id" short of
its own wording.
Reversibility: cheap

### 2026-11-02 — The contract declares the lifetime maximum, the store's owner scope and a test minter

Context: the slice plan found S1 and S2 needed signatures the contract left implicit.
Chosen: `HandoffSettings.maxLifetimeSeconds`, `IncomingRequestLine`, the Store's owner-scope
interfaces with `ownershipDecorator(storage, owner)`, the amended `ConformanceTarget`, and a
test-support `mintTestAssertion`, all amended in one change.
Rejected: **deriving the maximum from the edge's lifetime** — couples two deployments' settings
silently. **Leaving the minter to S1** — every harness would grow its own. **Amending slice by
slice** — the contract would disagree with itself between slices.
Reversibility: cheap

### 2026-11-02 — The brief's ownership wording: authority comes from the edge

Context: the brief said ownership is "checked at the edge"; the design checks it in game-service,
against a principal the edge vouches for.
Chosen: the owner's wording, "Ownership is enforced on every operation carrying an id, against a
principal only the edge can vouch for, and ids are unguessable."
Rejected: **keeping "checked at the edge"** — describes approach A, which the design gate rejected.
Reversibility: cheap

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
