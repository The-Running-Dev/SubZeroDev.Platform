# Design — principals (G3)

**Document status:** Design. Derived from [`00-brief.md`](00-brief.md) and the approach the owner
chose on 2026-11-01, a **signed principal handoff**. If the brief changes, this is re-derived, not
patched.

What this document does **not** decide is already owned elsewhere.
[`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) owns the game surface's width
(§5), the ownership obligation (§6.2), the decorator's shape (§6.3) and the port obligations (§7).
[`g2/20-contract.md`](g2/20-contract.md) owns the store schema this design extends.
Platform's Identity owns authentication: providers, key discovery and the principal type.
**Adventures owns its own persistence and authorization.** It embeds the engine and keeps its own
database, and G3 does not migrate it onto game-service or replace its store.

**Three labels mark every claim below that is not inherited.**

- **Fact** — read from the tree, the brief or a recorded decision, and cited.
- **Recommendation** — this design's choice. Each is logged in [`90-decisions.md`](90-decisions.md)
  with what it rejected, and the owner can overturn it there.
- **Owner question** — open, and listed under [*Open questions*](#open-questions). Nothing in this
  document settles one. Where a mechanism depends on the answer, the design names the dependency
  and stops.

Five facts shape almost everything below.

1. **The edge serves exactly one workload per deployment.** *(Fact: `GameEdgeOptions` carries one
   `WorkloadBaseAddress`.)* A per-workload credential policy is therefore a per-deployment
   configuration value, not a routing table.
2. **Platform authenticates every request before anything else runs.** *(Fact:
   `OperationScopeMiddleware` in `src/SubZeroDev.Platform.Hosting/Pipeline.cs`.)* A rejected or
   unverifiable credential is answered `401` with the error's own code, before a scope opens. No
   credential means `Anonymous`, which proceeds.
3. **The edge's catch-all route is exempt from Platform's per-endpoint authorization.** *(Fact:
   `workloads/game-edge/SubZeroDev.Platform.GameEdge/Endpoints.cs`.)* Its recorded reason is that "the workload's own boundary
   authorizes what it carries." This design keeps that reading: the edge authenticates, and the
   workload's boundary authorizes.
4. **The store's ports carry no owner, and must not.** *(Fact: G2 invariants 48 and 49.)* No host
   column reaches a stored record, and the session blob carries nothing but the engine's
   serialization. Ownership therefore cannot be a field the engine sees. It has to be a host column
   and a predicate.
5. **The caller chooses `profileId`.** *(Fact: the vendored service contract 0.8.0.)* It is a field
   on `list-campaigns`, `list-saves` and `delete-save`, and optional on `create-session`. A design
   that scoped sessions and saves but left profiles global would leave a cross-principal read
   through the profile key.

## Data model

### The principal

**Fact.** A Platform principal is an id, issuer plus subject, and a kind. Its kinds are `Anonymous`,
`Account`, `Delegated` and `System`. Identity's bearer providers, the configured path and the JWT
path, establish kind `Account`.

**Recommendation: the owner key is the principal id exactly as the edge's chain establishes it.**
It is `(issuer, subject)`, both non-empty, with nothing derived and nothing hashed. Only kind
`Account` may own state.

**Recommendation: the game-service edge does not compose `IdentityAccountsModule` in G3.** With
that module composed, a principal that later creates a Platform account authenticates as
`(platform.account, <id>)` instead of its raw pair. Every row it owned would then become
unreachable through a change of key, not of owner. Leaving the module out keeps the owner key
stable for G3's lifetime. Whether account deletion involves a Platform account record is part of
Owner question 2.

### The handoff assertion

The edge mints one per forwarded request, and only in *terminate* mode. It is a compact signed token
with an asymmetric signature. game-service holds only the public half of the key.

| Claim | Meaning | Why it is bound |
|---|---|---|
| issuer | The edge deployment's configured handoff issuer | Separates the handoff from any issuer token |
| audience | The workload's configured audience | An assertion for one workload is not one for another |
| principal issuer, subject | The owner key | The thing being handed off |
| principal kind | `Account` | A kind the workload does not accept is refused, not defaulted |
| method, path and query | Exactly as forwarded | A captured assertion cannot authorize a different operation |
| request id | The request's correlation | Ties the assertion to one logged request |
| issued at, expiry | Seconds apart | Bounds what a captured assertion is worth |

**Recommendation: the body is not bound.** Requests are forwarded with their body, and streamed
routes would make a body digest a second read of the body. The method and path already fix which
operation runs. Within the expiry window, replaying a captured assertion with a different body
first requires capturing traffic between the edge and the workload. That needs host-level access,
which is the compromised-host case the threat model proposes to exclude (*Distribution*).

**Consequence of the approach:** game-service never sees the caller's token. It is not a brief
requirement. It follows from terminating at the edge and handing off an assertion instead.

### The store: owner columns

**Recommendation: ownership is a host-owned column pair on every owned row, in every predicate.**
This follows the pattern G2 already uses for `tenant_id`, which the store supplies and never
computes.

| Table | Added | Key change |
|---|---|---|
| `session` | `owner_issuer`, `owner_subject` | None. Ids are engine-minted and unguessable, and the key stays `(tenant_id, session_id)` |
| `save` | `owner_issuer`, `owner_subject` | None, for the same reason |
| `profile` | `owner_issuer`, `owner_subject` | **The key becomes `(tenant_id, owner_issuer, owner_subject, profile_id)`** |
| `profile_achievement` | `owner_issuer`, `owner_subject` | **The key gains the owner** ahead of `profile_id` |

**Profiles are namespaced by owner, not bound to a derived key.** Two principals may both call a
profile `default` and hold two different rows. The engine sees the `profileId` the caller chose,
unchanged. A foreign profile id is simply a profile this principal does not have, so it loads as
the engine's empty profile. No new refusal path is introduced.

**The engine never sees an owner.** G2 invariants 48 and 49 hold as written. Byte-identical
`serialize()` output under the decorator follows from that, and the proof asserts it rather than
assuming it.

**Pre-G3 rows** have no owner. Their treatment is Owner question 3. The mechanism is the same
whichever answer is chosen. The migration gives every existing row a **reserved empty owner**, `''`
for both columns. No principal can ever hold that owner, because the handoff refuses an empty
issuer or subject. If the answer is "unreachable until swept", nothing more is needed for sessions
and saves, because G2's sweep already bounds them. Profiles are never swept, so a reserved-owner
profile would stay unreachable and unremoved until the answer says otherwise. If the answer is
deletion, the same migration deletes those rows instead.

### The account surface's projection

The account surface lists the principal's saves across all of its profiles. Each entry carries the
save id, campaign id, profile id, `saved_at_seq`, `row_created_at` and `expires_at`. It never
carries a blob or a scene, so *no hosted endpoint returns engine state* holds.

### Configuration

| Where | Value | Notes |
|---|---|---|
| Edge | **Credential mode**: `Terminate` or `PassThrough` | Per deployment. Startup refuses a mode its required settings do not support |
| Edge, terminate | Platform Identity's bearer settings | Unchanged from D5: issuer, audiences, algorithms and key discovery |
| Edge, terminate | Handoff signing key, key id, issuer, audience and lifetime | The private key is mounted as a secret, never set inline |
| Edge | Abuse-limit policy | Key, window and permit count. **The values are Owner question 8** |
| Edge | Trusted forwarding proxies | Empty by default. Depends on Owner question 5 |
| game-service | Handoff issuer, audience, public keys by key id, clock tolerance | No private material. More than one key id is accepted so rotation needs no simultaneous restart |

### Not modelled

There is no session in the edge and no cookie jar. There is no token cache beyond Identity's key
set. There is no ownership-transfer record, no sharing, and no guest ownership; the last is a
brief non-goal. There is no tenant in the assertion: game-service stays on the implicit tenant G2
supplies.

## Module boundaries

### The .NET edge

| Module | Owns | Depends on |
|---|---|---|
| **Authentication** | Platform Identity's bearer chain, composed in terminate mode only | Platform Hosting and Identity |
| **Credential policy** | The per-mode header rules in both directions (*Control flow* §6) | Mode configuration |
| **Handoff signer** | Minting the assertion from the ambient principal and the outbound request line | Credential policy's outbound request, the signing key |
| **Abuse limiter** | Admitting or refusing a forwarded request | The ambient principal, or else the client address |
| **Forwarder** | Unchanged from G1 and G2, except that it now applies the credential policy's outbound headers | Credential policy, Handoff signer |

**The edge reads no operation contract** ([`d5/90-decisions.md`](d5/90-decisions.md), 2026-10-17). It authenticates the caller and
asserts who they are. It never decides whether that caller may touch a given id.

### The Node workload

| Module | Owns | Depends on |
|---|---|---|
| **Handoff verifier** (new) | Refusing every non-probe request whose assertion is missing or invalid, and producing the verified principal | Configuration only |
| **Composition** | Passing the verified principal into `forRequest` | Handoff verifier's output type |
| **Ownership decorator** (new) | The `StorageDecorator` that turns a principal into an owner scope on all three ports | Store's owner-scope parameter |
| **Store** | Executing every statement under the owner scope it is given. It has no notion of a principal | — |
| **Dispatch** | Unchanged. It never sees a principal | Composed ports |
| **HTTP surface, MCP surface** | Unchanged in width | Composition |
| **Account surface** (new) | Two routes outside the operation table and the MCP tool table | Composition's per-request decorated store, never Dispatch, never the engine |

**The dependency rule.** Edges run one way: verifier → composition → decorator → store. Dispatch and
the account surface consume the composed ports and know nothing about principals. The Store knows
an owner scope and nothing about how it was derived. The decorator is the only module that maps a
principal to an owner. No cycle exists, and none can form without the Store importing the verifier,
which a module-graph test forbids. That test mirrors G2 invariant 77.

**The lifecycle probe is decorated per request.** *(Fact: G2's contract makes the probe process-lived
under invariant 69, and composes it once with placeholder persistence and profile ports.)*
**Recommendation:** each request's composition passes the process-lived probe into the seam with the
live ports, and Dispatch uses the decorated result. The probe itself is not rebuilt, so invariant 69
holds. What changes is G2's prose that "the lifecycle member's result is discarded" in the
per-request call. Without this change, the undecorated probe would classify any id as live, expired
or absent for any caller — the existence oracle G2 warned about.

## Control flow

### 1. One game operation, end to end

1. The caller sends `Authorization: Bearer <token>` to the edge.
2. Platform's pipeline authenticates. A valid token establishes an `Account` principal.
3. The abuse limiter admits the request, keyed by that principal.
4. The credential policy builds the outbound request. In terminate mode it consumes `Authorization`
   and drops `Cookie` and `Origin`. It strips any caller-supplied handoff header. It sets
   `X-Forwarded-*` from the connection.
5. The handoff signer mints the assertion over the outbound method, path and query, and adds it.
6. game-service's verifier checks the signature against a configured key id, then the algorithm,
   issuer, audience, expiry, issued-at, method, path and query, kind and non-empty owner. Any
   failure ends the request with `401` (*Error semantics* in the contract).
7. Composition calls `forRequest(owner)`. The decorator wraps all three ports under that owner.
8. Dispatch runs the engine operation unchanged. Every store statement carries the owner predicate.
9. The response returns through the edge under terminate mode's response rules.

### 2. A foreign or unknown id

At step 8 the owner predicate finds no row. The store answers exactly as it answers for an id that
was never minted: `unknown_session` or `unknown_save`, `404`. The decorated lifecycle probe answers
`absent` for a foreign id, so a foreign *expired* session cannot surface as `session_expired`.
**Recommendation:** unknown and foreign look identical, following
[`d5/90-decisions.md`](d5/90-decisions.md) 2026-08-24, "a caller who may not see a resource is told
it does not exist". The Adventures POC's `404`/`403` split is
rejected (*Alternatives considered*).

### 3. No credential, or a refused one

- **No credential, terminate mode:** Platform yields `Anonymous`. The forwarder refuses before
  forwarding: `401`, `CredentialRequired`. Nothing reaches game-service.
- **A rejected, expired or malformed token:** Platform answers `401` with `CredentialRejected` before
  the route runs.
- **No usable key material, or a provider failure:** Platform answers `401` with
  `KeyMaterialUnavailable` or `ProviderFailed`. Identity's key-set check reports the edge degraded
  or unhealthy on readiness (*Failure modes*).

### 4. Direct access to game-service

A caller that reaches game-service's listener without passing through the edge has no assertion, or
one it cannot have signed. The verifier refuses it at step 6. **There is no unauthenticated mode and
no switch that disables the verifier.** *(Recommendation.)* G2's harnesses and the replay mint
assertions with a test key pair instead.

### 5. The account surface

- **List saves:** steps 1–7, then the account surface reads the owner's saves across every profile
  through the decorated store, and returns the projection.
- **Delete account:** steps 1–7, then one transaction deletes every `session`, `save`, `profile` and
  `profile_achievement` row the owner holds in the tenant. **That set is the floor every answer to
  Owner question 2 shares.** Anything beyond it waits on the answer: the workload's own data,
  Platform account mappings, credentials, the external issuer, reversibility and in-flight writes.

### 6. The credential policy, header by header

[#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340) is this table's outside
evidence. **Recommendation:** game-service runs in **terminate** mode and Adventures in
**pass-through**. The workload-specific policy is allowed because every shared requirement holds in
both modes.

| Direction | Header | Terminate (game-service) | Pass-through (Adventures) |
|---|---|---|---|
| in | `Authorization` | Consumed by the edge, never forwarded | Not forwarded. Adventures reads none from callers (#340) |
| in | `Cookie` | Dropped | Forwarded from this request only, together with `Origin` |
| in | `Origin` | Dropped, together with `Cookie` | Forwarded, together with `Cookie` |
| in | Handoff header | Stripped, then set by the edge | Stripped, never set |
| in | `X-Forwarded-For`, `-Proto`, `-Host`; `Forwarded` | Caller values dropped; the edge sets `X-Forwarded-*` from the connection | Same |
| in | Hop-by-hop headers, including any `Connection` names | Stripped | Stripped |
| out | `Set-Cookie`, every value | Dropped | Forwarded verbatim, every value, unrewritten |
| out | `Location` on `3xx` | Forwarded | Forwarded |
| out | `Access-Control-*`, preflight answer | Not produced. A preflight reaches the workload like any request | Forwarded from the workload. The edge answers neither CORS nor preflight |
| out | `Cache-Control`, `Vary` | Forwarded | Forwarded |

**Cookies are not rewritten.** *(Recommendation.)* The precondition is that the edge answers at the
workload's public host, so a cookie set without `Domain` binds to the right host. The tests assert
`Set-Cookie` bytes unchanged. If cutover (Owner question 5) puts the edge on a different host, that
precondition fails visibly instead of silently.

### 7. Probes

The edge's own probes are answered by Platform's probe middleware. game-service's `/livez` and
`/readyz` are exempt from the verifier: they answer liveness and readiness and touch no owned row.
G2 invariant 92 holds.

## Distribution

### The baseline

**Facts.** There is one VPS, which is the stated baseline. An existing reverse proxy terminates TLS
and routes publicly. Adventures' compose file joins an external proxy network. game-service binds
loopback unless `GAME_SERVICE_HOST` is set.

### The simplest initial topology

**Recommendation:** reverse proxy → one edge per workload → that workload, with one edge instance
each. The edge's position relative to the proxy (behind it, or taking over TLS) is Owner question 5.
This design works either way. Behind the proxy, the edge trusts `X-Forwarded-For` only from the
configured proxy addresses, and per-address limits key on that.

### Who can reach the listener, and who can assert a principal

| Path to game-service | Reaches the listener? | Can assert a principal? |
|---|---|---|
| Caller → reverse proxy → edge → game-service | Yes | **Only the edge**, by signing |
| A container on a shared proxy network | Yes, if game-service is attached to that network | No: it holds no private key |
| An alternate ingress route that sends the reverse proxy straight to game-service | Yes, if configured | No: the request carries no assertion, or a caller-supplied one the signature refuses |
| A caller who obtained an assertion from a log or error | Yes | No: assertions are never logged, and each one binds a method, path and query and expires in seconds |
| A compromised host, or a malicious operator | Yes | **Yes**, by reading the key. **Proposed out of scope.** |

**Recommendation: signing is needed.** Isolating the network and stripping headers would make
game-service's guarantee depend on how every future compose file, proxy route and shared network is
wired. CI cannot prove that wiring, and one misplaced route defeats it silently. With signing, the
trust boundary is the key, so the guarantee holds whatever the topology. **Adventures needs no
handoff.** It authorizes from its own cookie session, so reaching its listener directly bypasses
nothing Platform enforces. Whether its direct route stays public after cutover is Owner question 5.

**"Implementing Identity locally."** game-service verifies one Platform-issued key and holds no
issuer, token, discovery or account logic. This design reads that as adopting Platform's Identity,
not implementing it. **The reading is the owner's to confirm** (Owner question 9).

### A second edge instance

Ownership is unaffected. The edge is stateless; both instances sign with keys game-service trusts,
and no affinity is needed. **Abuse limits multiply by instance count**, because each instance counts
alone. The instance count and any shared enforcement are Owner question 8. No supported-capacity
commitment is made here.

### Steady-state dependencies

The edge depends on the issuer's key set. It refreshes the set in the background, and cached keys
keep validating while the issuer is unreachable. game-service depends on nothing external for
authentication. **Whether G2's fully-offline steady state still binds G3 is Owner question 6.**
Under termination, the issuer's key endpoint is the one new outbound dependency, and only at
refresh.

### Evidence

**Recommendation:** CI, from a fresh clone, composes the real edge, the real game-service, Postgres
and a **test issuer serving a real discovery document and key set, signing real tokens**. No mocked
authentication stands in for the chain. It proves:

- two principals, and a cross-principal refusal on every operation carrying a session, save or
  profile id
- account save listing and account deletion
- byte-identical `serialize()` output, decorated against undecorated
- missing, expired and invalid credentials, and issuer failure
- direct workload access with an absent, forged, expired or mis-bound assertion refused
- every header rule in the table above, through buffered and streamed paths

Adventures' end-to-end proofs of guest session, OIDC sign-in and cross-origin write run against a
pinned Adventures revision, pointed at the same test issuer. **Fact:** Adventures' OIDC client is
generic, so a test issuer is a configuration change (`server/src/identity/oidc.ts`). Whether
game-service also needs a **live** deployment in G3 is Owner question 7.

## Failure modes

| Failure | Observable outcome | Recovery |
|---|---|---|
| Issuer unreachable at refresh, keys cached | Requests keep validating. Readiness reports degraded | Background refresh retries |
| No key material at all | `401 KeyMaterialUnavailable`. Readiness reports unhealthy | Issuer reachable again |
| Provider failure mid-validation | `401 ProviderFailed` | Per request |
| Handoff signing key missing or unreadable at edge startup | The edge refuses to start, naming the setting | Operator |
| game-service handoff keys missing at startup | game-service refuses to start, naming the setting. It never starts open | Operator |
| Assertion refused by game-service | `401`, logged with the reason; the assertion itself is never logged | None needed: either a bypass or an edge defect |
| Edge and game-service clocks differ beyond tolerance | Every forwarded request is refused `401` | Fix the clock. The tolerance is configured, and one host shares one clock |
| Key rotation half-done | Requests signed with a key id game-service does not hold are refused | Rotation order: add the public key, then switch the signer, then retire the old key |
| Abuse limit reached | `429` with `Retry-After` and code `RateLimited` | Wait out the window |
| Flood of refused credentials | Each costs one validation. The limiter runs after authentication, so refused credentials are **not** counted against a limit | Owner question 8 decides whether pre-authentication limiting belongs at the reverse proxy |
| Store unreachable | Unchanged from G2: `503 storage_failure` | Unchanged |
| A foreign id | Identical to an unknown one | — |
| Account deletion fails partway | Nothing is deleted. It is one transaction | Retry |

## Concurrency and ordering

- **An owner never changes after a row is created.** No operation transfers ownership. So the owner
  predicate cannot race the row's lifetime, and checking ownership needs no separate
  read-then-write.
- **G2's optimistic lock is unchanged.** The guarded update gains the owner in its predicate. A
  foreign writer matches no row and cannot even produce a conflict.
- **The verifier is stateless.** It keeps no replay cache, so two game-service instances need no
  shared state for authentication.
- **Account deletion and in-flight writes.** A write that commits before the deletion transaction is
  deleted with the rest. An update that arrives after it finds no row and fails as G2 already
  defines. **A new session created after deletion with a still-valid credential succeeds today.**
  Whether it must not is Owner question 2. The mechanism that would stop it, an owner tombstone
  checked on insert, is designed only once the answer exists.
- **Abuse-limit counters** are per edge instance and in memory, so a restart resets them.

## Alternatives considered

### The handoff — signed assertion, not an unsigned header (approach A)

The edge would set a principal header and game-service would trust it. It is the cheapest option.
**Rejected** because the guarantee would rest entirely on network wiring: shared proxy networks,
alternate ingress routes and every future compose file. CI cannot prove that wiring, and one route
defeats it with no failing test.

### The handoff — signed assertion, not pass-through with a resolver (approach C)

The edge would never terminate. game-service would send the token to a new Platform .NET resolver.
There would be nothing to spoof. **Rejected** because it adds a process and a per-request network
dependency to every game operation. It also puts a .NET runtime dependency on the Node workload's
serving path, which G2's isolation rule keeps apart.

### game-service validates bearer tokens itself

game-service would run its own JWT validation against the issuer's key set. **Rejected** because it
is a second authentication stack: discovery, key refresh and algorithm policy reimplemented outside
Platform. That is exactly what "adopts Platform's Identity rather than implementing it locally"
rules out.

### A contract-aware edge enforcing ownership

The edge would read the operation contract, extract the id and look up its owner. **Rejected**
because it reverses decision 2026-10-17, "edge reads no contract". It would make the edge a second
store consumer, and it would leave the store's own paths undecorated, which §6.3 requires to be
decorated.

### Mutual TLS between the edge and the workload

**Rejected** because it authenticates the edge, not the principal. A principal header would still be
needed, and certificate issuance and rotation would cost more than one key pair.

### An ownership ledger table checked by a wrapper

A separate table would map each id to its owner, and the wrapper would check it before delegating.
**Rejected** because claiming and writing become two statements, so a row can exist unowned
between them, and a failed write leaves an orphan claim. Owner columns on the row make ownership and
existence one fact.

### Binding `profileId` to a key derived from the principal

**Rejected** in favour of namespacing profiles by owner. A derived key would change what a caller
must send, and refusing a mismatched one would add a refusal the engine has no code for. Namespacing
leaves the engine's input unchanged.

### Unknown `404`, foreign `403`

This is the Adventures POC's split. **Rejected** because it turns every id into an existence oracle,
contrary to decision 2026-08-24.

### A cookie session for game-service first

This would use the SignIn module's cookie and its anti-forgery endpoint. **Rejected for now, not for
good.** game-service has no browser client today. A bearer token is not ambient, so the bearer
contract has no CSRF or CORS surface. Browser support can be added later through that same module
without changing the handoff, because only the edge's authentication step changes.

### A switch that runs game-service without the verifier

This would make local runs and G2's harnesses convenient. **Rejected** because a switch is a bypass
with a configuration value in front of it. The harnesses sign with a test key instead.

## Open questions

These are the brief's open items that design cannot settle, plus one interpretation this design
depends on. Each is the owner's to answer. Where this document recommends an answer, the
recommendation is labelled and nothing depends on it being accepted.

1. **MCP's path.** Is ownership on the MCP path in G3's scope? *What depends on it:* the verifier
   gates every non-probe path, MCP's included. With enforcement in the store, an MCP tool call
   without an assertion can neither read nor write owned state. If MCP is in scope, MCP clients
   authenticate at the edge like HTTP callers, and coverage comes from the same gate. If it is not,
   MCP has no working path to owned state in a deployment with the verifier on, and G3 must say so
   rather than leave a bypass. Listing MCP among the edge's callers does not resolve this.
2. **Account deletion.** What it removes beyond game-service's owned rows. Its effect on Platform
   account mappings, active sign-ins, credentials and the external issuer. Whether it is reversible.
   How writes already in flight, and creations by a credential still valid afterwards, are treated.
3. **Pre-G3 ownerless rows.** Deleted at migration, or unreachable until swept (and, for profiles,
   kept indefinitely). *Recommendation:* unreachable under the reserved empty owner, with sessions
   and saves left to G2's sweep.
4. **Which Adventures routes move behind the edge.** All of its API, or only those #340 names.
5. **Live cutover and topology.** Whether the edge sits behind the existing reverse proxy or takes
   over TLS. Which repository owns deployment. How existing Adventures sign-ins are treated. The
   rollback procedure and acceptable downtime.
6. **G2's fully-offline steady state.** Whether it still binds G3, given the edge's background
   key-set refresh.
7. **A live game-service deployment.** Whether G3 requires one. The brief's live proof is
   Adventures.
8. **Abuse limits.** Their values, key and enforcement topology, the edge instance count, whether
   enforcement must be shared, whether refused credentials are limited before authentication, and
   any capacity commitment.
9. **"Not implementing Identity locally."** Whether verifying one Platform-issued key counts as
   adopting Platform's Identity. This design reads it as yes.
