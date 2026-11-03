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

### 2026-11-03 — S5: A bearer scheme without a token, or beside a second `Authorization` value, is a rejected credential

Context: S5.3 says a malformed `Authorization` value is `401 CredentialRejected`, never `Anonymous`.
The first run of the new test against the real edge showed two forms answering otherwise.
`Authorization: Bearer` (with or without the trailing space) and a request with
two `Authorization` values, the first a valid bearer token, were not rejected. The first was read as no
credential and answered `CredentialRequired`. The second authenticated as the first value and ignored
the other, and game-service saw the request. The code, not the contract, was wrong.
Chosen: `ConfiguredBearerAuthenticationProvider` reads the header as nothing, a token, or malformed.
The bearer scheme with no token, the scheme in any case or followed by other whitespace than one space
(`bearer <token>`), and a bearer among several `Authorization` values, are malformed and answer
`CredentialRejected`. `JwtBearerAuthenticationProvider`, the sample's test-grade provider, is unchanged. A value for another scheme (`Basic`) is still no bearer credential and
defers, so a deployment that adds a provider for it is unaffected; with none, the edge answers
`CredentialRequired` and forwards nothing.
Rejected: **treating every non-bearer scheme as malformed**, which would stop a second provider from
ever claiming its own scheme. **Accepting the first of several values**, which makes the answer depend
on header order. **Fixing it in the edge**, which would give the edge a second reading of a header the
provider already owns.
Reversibility: cheap.

### 2026-11-03 — S5: The provider failure is injected at the provider boundary, and the issuer outage is a listener nothing may connect to

Context: S5.6 needs a failure mid-validation, and a real edge process offers no seam to cause one.
S5.9 says "with the test issuer stopped", but the test issuer is a key pair and has no process. Each
needs a stand-in whose limits are stated.
Chosen: S5.6 is a `TerminateModeTests.cs` test that wraps the registered provider and, while armed,
answers `ProviderFailed` for a request with an `Authorization` header. It proves the edge turns that
answer into `401 ProviderFailed` with a correlation, forwards nothing, and recovers on the next request.
It returns the failure and does not throw inside the JWT handler, because the chain has no catch. The
mapping from a library fault to `ProviderFailed` is proved by `S20_7_A_library_fault_is_a_provider_failure`.
S5.9 points the edge's token issuer at a loopback listener that counts connections, shows valid tokens
succeed with a count of zero, then closes the listener and shows they still succeed. The claim that the
edge opens no connection except to game-service is carried by the `game-service-isolation` job, which
now runs `s5-credentials.test.ts` beside `s3-edge.test.ts` and fails on any non-loopback packet.
S5.7 runs an edge with `ClockTolerance` 00:00:00 and a ten-second token, so the `exp` boundary is
crossed in about twelve seconds, long enough that a loaded CI runner cannot land the earlier calls after it without moving a clock.
Rejected: **a sidecar issuer process**, which would add a server the edge is meant never to ask.
**Throwing inside the handler**, which no code path catches. **Moving the clock**, which would test the
harness and not the edge.
Reversibility: cheap.

### 2026-11-03 — S4: The cross-principal matrix is derived from the contract, and a profile row is seeded on the durable half only

Context: S4.1 requires the matrix to fail if a contract operation carrying `sessionId`, `saveId` or
`profileId` is absent from it, and S4.2 requires A's rows read back with SQL. Thirteen operations
carry an id field; `list-campaigns`, `list-saves`, `create-session` and `delete-save` carry
`profileId`, and a never-minted profile does not refuse (it answers as an empty one), so for those
"the response a never-minted id produces" is the comparison and not a 404. The in-memory store has
no SQL to read, and gameplay this short earns no achievement, so no public operation writes A a
profile row there.
Chosen: the test reads the request schemas out of the vendored contract (resolving the one `$ref`
`create-session` uses) and fails on any id-carrying operation without a matrix row; a second test
drops each row in turn and shows the check names it. B's request on A's ids and on never-minted ids
are compared whole, with the generated `sessionId` and `gameId` of a successful `create-session`
replaced. After each request, A's rows in all four owned tables are compared as the database
serialises them. A's profile row is written through A's own port on the durable half, as S2.5 does;
the in-memory half compares with A having no profile row, and S4.4's per-owner in-memory profile
store covers the profile port there. A mutation that drops the owner predicate from the save read
fails `load-game` and `delete-save` on both surfaces; one that drops the owner from the save lifecycle
probe fails the expired-row test (invariant 115). The matrix cannot see a leak in the profile read or
in the delete statement's own predicate: the profile operations answer the same for an empty and a
foreign profile, and the save read refuses before a delete runs. Those two are proved at the port by
S4.4, which fails when either is removed.
Rejected: **a hand-written list of operations**, which is the omission S4.1 exists to catch;
**mocking a profile row into the in-memory store through composition internals**, which would test
the mock; **comparing only status and code**, which would pass a body that echoes A's data.
Reversibility: cheap.

### 2026-11-03 — S4: The conformance suite runs under two owners, with a per-owner in-memory target and a leak probe after each step

Context: invariant 122 and the `ConformanceTarget` amendment in `20-contract.md` require every G2
assertion under owner A and owner B on both targets, and that each port method sees nothing of the
other owner's rows. The shipped target still had the G2 shape (`persistence`, `profiles`, an ownerless
`seedCorruptProfile`). S4.4 says "seven port methods"; the ports have eight, counting
`saves.listByProfile`.
Chosen: `ConformanceTarget` takes the amended shape (`storage: OwnerScopedStorage`, seeds take an
owner). `runPortConformance` runs each existing step once per owner against a view bound to that
owner, so the G2 assertions are unchanged, then probes from the other owner: reads of the ids the
step touched see nothing, a foreign `saves.delete` (matching and mismatched `savedAt`) removes
nothing, and a foreign `profiles.save` changes nothing the owner loads. The in-memory target keeps
one state per owner. The durable write-failure seam keys on the owner columns. Eight leaking
in-memory mutants, one per method including `listByProfile`, each fail the suite with
`MethodDiverged`. The probe covers all eight methods; the slice's "seven" is read as "every".
Rejected: **running the suite under one owner and a separate isolation test**, which leaves the G2
assertions unproven under B; **a mutant for only the methods S4.4 names**, which would leave
`listByProfile` unproven; **editing the slice text to say eight**, which is the design's to correct
and changes no behaviour.
Reversibility: cheap.

### 2026-11-03 — S3 review: Terminate mode's pinned-keys rule is the edge's own startup guard

Context: invariant 125 says the edge validates bearer tokens only against pinned keys and startup
refuses a discovery address. The edge composes Platform Identity, which accepts `Discovery` for hosts
that want it and fetches the issuer's key set while the host starts. So the rule was documented and not
enforced: a Terminate edge configured with a discovery provider and no `SigningKeys` connected to the
issuer, then started. The independent review of PR #370 found it.
Chosen: the edge checks the raw `Platform:Identity:Bearer` section in Terminate mode before it registers
Identity, so the refusal precedes any discovery or issuer connection. It refuses a provider that sets
`Discovery` (even beside `SigningKeys`) or sets no `SigningKeys`, and names the full key. Identity keeps
discovery for every other host. A host with no provider stays the composition profile's to refuse.
Proved against the real edge process with a listener standing in for the issuer: the four refused
configurations make zero connections to it, and a control shows the listener counts a connection that is
made. The in-process tests cannot show this, because a test host flattens settings before startup.
Rejected: **removing discovery from Identity**, which other hosts use; **checking after registration**,
which comes after the connection the check exists to prevent; **trusting Identity's key validation**,
which runs on keys that are present, not on whether they were meant to be fetched.
Reversibility: cheap.

### 2026-11-03 — S3 review: S3.8 is measured in a job of its own, and a startup test that raced was moved

Context: S3.8 claimed the composition ran with no network beyond its own processes, and CI rejected two
outbound TCP ports for a job that also ran other suites; the test accepted a token of an unresolvable
issuer. Nothing counted a connection attempt, so a swallowed one was invisible.
Chosen: the `game-service-isolation` job runs `s3-edge.test.ts` as a dedicated user whose every
non-loopback packet, over IPv4 and IPv6, and every DNS query is rejected and counted, with dependencies
installed first. A negative control shows each path rejects and counts; the run then fails on any counted
rejection, on a guard that saw no loopback traffic from the suite, and on any skipped test. The guard is
removed on every exit path. The Local-host guard's mechanism is reused; its proof stays the Local sample's.
Also: `A_mode_under_a_profile_that_cannot_hold_it_fails_startup` failed intermittently. Platform refuses a
profile in a hosted service's start, and when that start fails `WebApplicationFactory` can report the
host's disposal (`ObjectDisposedException: 'IServiceProvider'`) instead of the refusal: 126 of 300 starts
under CPU saturation, none idle. The refusals are asserted instead against the real edge process in S3.6,
by error code. No retry, weaker assertion or serial execution was added.
Rejected: **retrying the theory**, which hides a defect in what it reports; **asserting only that startup
throws**, which would pass on the disposal error; **keeping the in-process theory**, which can fail without
a defect in the edge.
Reversibility: cheap.

### 2026-11-03 — S3: A host that only authenticates declares `AuthenticatedTransport` ([ADR-010](../docs/docs/adr/ADR-010-authenticated-transport-profile.md))

ADR-010 records the profile, what it admits and refuses, and why `Local` and `Operated` keep their meanings.

### 2026-11-03 — S3: Consumer modules are refused under the transport profile, and an auth failure is not audited

Context: the edge's Terminate mode needs Platform Identity's bearer provider, which `Local` refuses
(I-C3), and has no durable audit sink, which `Operated` requires (I-C2). Extends the profile of
[`d5/90-decisions.md`](d5/90-decisions.md), 2026-08-24, and the 2026-09-03 entry that makes a consumer
declare the registrations its profile requires; it supersedes neither.
Chosen: the profile admits modules by an internal mark rather than refusing them by a list, so a module
added later is refused until it is examined. The module and durable-state check runs before any module
registers, so the refusal names the module, not a service that failed to activate. The edge's
authentication failure is `401 CredentialRejected` from the operation-scope middleware with a
correlation id and no audit write, because the profile has no trail to write it to. `Required` audit is
refused at the writer with a non-retryable error. The shipped `appsettings.json` stays `Local`, and
`GameEdge:CredentialMode` has no default.
Rejected: **a dummy provider or a sink claiming to be durable**, which register what does not do the
job; **disabling validation for the edge**; **letting `Operated` waive its audit requirement**;
**refusing modules by a list**, which admits every module nobody thought of; **a Required audit write
that falls through to the log sink**, which satisfies the class in name only.
Reversibility: cheap for the module mark and the check order. Expensive for the enum value, which is
fingerprinted and stored.

### 2026-11-03 — S2: Dispatch and both surfaces are built per request; the startup check and replay use fixed owners

Context: `forRequest(owner)` returns ports bound to one owner, but G2 built Dispatch and both
surfaces once at startup over a provider. The startup route and schema check and the in-process
replay run with no verified request.
Chosen: `serve` builds Dispatch and both surfaces for each verified request, from that request's
ports. The startup check builds them once for a reserved check owner and discards them. Replay runs
every step as one fixed replay owner.
Rejected: **one Dispatch over a provider argument** — the owner would have to travel beside the
arguments, which invariant 111 rules out. **Skipping the startup check** — it is what refuses a
route clash before binding.
Reversibility: cheap

### 2026-11-03 — S2: The sweep, the snapshot and the readiness check stay ownerless

Context: Invariant 112 puts an owner predicate on every statement a request runs. The retention
sweep, the replay snapshot and the readiness check run for the process, not for a request.
Chosen: those three keep their G2 statements, with no owner predicate. Each reads or deletes by
expiry or by schema and never answers a request.
Rejected: **sweeping per owner** — no list of owners exists to sweep by, and the sweep's
correctness does not depend on one.
Reversibility: cheap

### 2026-11-03 — S2: A write to an id another owner holds fails as an id collision

Context: Ids are minted by the engine and unique per table. An owner's write to an id another owner
holds cannot update that row, and the contract does not say what it answers.
Chosen: the write matches no row and fails with `IdCollision`. The other owner's row is unchanged.
Rejected: **silently inserting a second row** — the id is the primary key. **Answering as a
conflict** — that would tell the writer the id exists.
Reversibility: cheap

### 2026-11-03 — S2: The in-memory target keeps one map per table and one session layer per owner

Context: The in-memory configuration must be owner-scoped the same way as the durable one, and G1
kept one long-lived session layer for every caller.
Chosen: each entry is held beside its owner, never inside its record. Reads filter by owner. Each
owner gets one long-lived session layer and profile store, cached for the process with no bound.
Rejected: **a separate map per owner** — the snapshot would have to merge them back into one
ordering. **A fresh layer per request** — loses G1's in-memory session cache.
Reversibility: cheap

### 2026-11-03 — S2: The probe is one process-lived owner-taking object, bound per request by the decorator

Context: Invariant 69 keeps one lifecycle probe for the process, and invariant 116 has Dispatch
use a probe decorated for the request.
Chosen: composition holds one probe whose methods take the owner first. `ownershipDecorator` binds
it to the request's owner on each call, without copying or rebuilding it.
Rejected: **a probe built per request** — breaks invariant 69.
Reversibility: cheap

### 2026-11-03 — S2: Six tables are owner-scoped, not four

Context: The contract names `session`, `save`, `profile` and `profile_achievement`. G2 also keeps
`profile_terminal` and `profile_kind_data`, each keyed by profile.
Chosen: the migration adds owner columns to all six and re-keys all four profile tables by owner.
Rejected: **scoping only the four named** — a terminal or kind-data row would collide across owners
sharing a profile id.
Reversibility: costly

### 2026-11-03 — S2: A G2 instance's profile writes fail after the G3 migration

Context: The contract says rows a still-running G2 instance writes after the migration take the
reserved empty owner. G2 writes profiles with `on conflict (tenant_id, profile_id)`, and that is
no longer a unique key once profiles are keyed by owner.
Chosen: keep the owner-first profile key. A G2 profile write fails, and the G2 adapter reports
`profile_write_failed`, changing nothing. G2 session and save writes still take the reserved owner.
The mixed-version tests pin this.
Rejected: **keeping a `(tenant_id, profile_id)` unique index for G2** — two owners could then never
hold the same profile id.
Reversibility: costly

### 2026-11-03 — S2: The conformance target is bound to one fixed owner

Context: The conformance seam exposes one `SessionPersistence`, and the durable store now hands out
persistence per owner.
Chosen: the conformance target is the store's persistence for a fixed conformance owner.
Rejected: **an ownerless persistence for conformance** — that would be the one unscoped store path.
Reversibility: cheap

### 2026-11-03 — S2: `DurableStore` exposes its ports as one `storage` member

Context: G2's `DurableStore` exposed `persistenceForRequest()` and its profile store separately.
The contract's `OwnerScopedStorage` groups persistence, profiles and the probe.
Chosen: `DurableStore.storage` is that `OwnerScopedStorage`, replacing the separate members.
Rejected: **keeping the G2 members beside it** — two ways to reach the same rows, one ownerless.
Reversibility: cheap

### 2026-11-03 — S2: The account surface is left to S8

Context: The contract amendment's store members include account deletion and export, and S2 touches
the store.
Chosen: S2 adds no account member. S8 owns it.
Rejected: **adding the members now, unused** — S8's acceptance would then test code it did not ship.
Reversibility: cheap

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

### 2026-11-03 — S6: The edge copies no caller header wholesale; one allow-list decides both directions, on both paths

Context: S6 asks each header row of the credential table to follow its mode. Before this slice the
forwarder sent the method, path, body, `Content-Type` and trace headers and nothing else, so the
table's pass-through rows (`Cookie`, `Origin`, `Set-Cookie`, `Access-Control-*`) did not exist, and a
list of headers to strip would have let any name not on it through.
Chosen: `HeaderPolicy` is the single owner. Inbound it is an allow-list: `Cookie`, `Origin`,
`Access-Control-Request-Method` and `-Headers` in pass-through only; `X-Forwarded-For`, `-Proto` and
`-Host` always, set by the edge. Outbound it keeps `Location`, `Cache-Control` and `Vary` in both modes
and `Set-Cookie` and `Access-Control-*` in pass-through only, every value, read unvalidated so a comma
in `Expires` cannot split or merge one. The buffered and streamed paths call the same two methods, so
invariant 104 holds by construction. A header the table does not name (`Authorization`, `Forwarded`,
hop-by-hop headers, `Proxy-Authorization`) is dropped without a rule of its own.
Rejected: **a deny-list of known-dangerous headers**, which fails open for the next header anyone
invents. **Copying the caller's headers and removing some**, which is the shape of the bug the table
exists to prevent. **Two policies, one per relay path**, which is how the paths would drift apart.
Reversibility: cheap.

### 2026-11-03 — S6: `TrustedProxies` is text validated at startup; a listed proxy's `X-Forwarded-*` replace the connection's per header; a terminate-mode preflight is anonymous

Context: S6.5 needs the edge to take a trusted proxy's `X-Forwarded-For` and to ignore everyone
else's. Three points the slice does not settle.
Chosen: `GameEdge:TrustedProxies` is a list of strings, because the configuration binder has no
`IPAddress` converter and would skip a malformed entry silently. Program parses each at startup and
names the full key (`'GameEdge:TrustedProxies:0' must be an IP address.`) before the host listens.
An IPv4-mapped IPv6 connection address matches its IPv4 entry. The default, empty, trusts nobody. When
the connecting address is listed, each of `X-Forwarded-For`, `-Proto` and `-Host` the proxy sent
replaces the edge's own value for that header; one it did not send falls back to the connection's.
`Forwarded` never passes, listed or not. Pass-through also forwards `Access-Control-Request-Method`
and `-Headers`: they carry no credential and the workload cannot answer a preflight without them.
Terminate mode drops the workload's `Access-Control-*` and answers no preflight itself, so an
anonymous preflight is `401 CredentialRequired` like any anonymous request. A browser client of
game-service is therefore a same-origin or native one until a decision says otherwise.
Rejected: **binding `TrustedProxies` as `IPAddress`**, which hides a typo. **Trusting the loopback
by default**, which makes a co-located attacker a proxy. **Parsing `Forwarded`**, a second header
format for one fact. **Having the edge answer the terminate-mode preflight**, which is the CORS
policy the design assigns to nobody.
Reversibility: cheap.

### 2026-11-03 — S7: Adventures is proved from a test-only project, at a pinned commit, against an HTTPS test issuer, with a recording proxy between the edge and the workload

Context: S7 runs a revision of another repository behind the real edge. Four points the slice leaves open.
Chosen: the proof is its own project, `workloads/adventures-proof`, not a test inside `game-service`,
because it shares nothing with that workload and its CI job must reach the network to clone and install
Adventures, which `game-service` and `game-service-isolation` must not. Adventures is pinned by commit
in `adventures-pin.json`; `npm run prepare-adventures` clones that commit and installs it, and the tests
refuse to start without the matching marker. Adventures runs from its own source with `tsx`, on a database
created and dropped per run in the PostgreSQL the `game-service` compose file provides. The test issuer
serves HTTPS with a per-run certificate that Adventures' process trusts through `NODE_EXTRA_CA_CERTS`,
because `openid-client` refuses an HTTP issuer; no HTTP exemption is introduced. A recording proxy sits
between edge and Adventures so that S7.3 can show the refusal was Adventures' own answer rather than infer
it from a status code. The edge's port is chosen before Adventures starts, because Adventures builds its
OIDC `redirect_uri` from the address a browser reaches it at.
Rejected: **a shared package for the process helpers**, which has one consumer today. **A plain-HTTP
issuer with the check disabled**, which proves a configuration Adventures does not run in. **Following the
tip of Adventures' default branch**, where a change there would fail this repository's build for a reason
nothing here changed. **Running the proof in `game-service-isolation`**, whose point is that nothing it
runs can reach out.
Reversibility: cheap.

### 2026-11-03 — S8: account deletion removes six tables, the routes sit outside the tables, and the in-memory store drops its cached layer

Context: S8 adds `GET /account/saves` and `DELETE /account`. Four points the slice and the contract leave open.
Chosen: deletion covers every table the owner migration puts an owner column on, which is six, not the
four the contract and S8.3 name: `profile_terminal` and `profile_kind_data` hold per-owner profile data
too, and leaving them would keep a deleted player's data. One transaction deletes children first
(`profile_achievement`, `profile_terminal`, `profile_kind_data`, `profile`, `save`, `session`) and rolls
back on any error. The two routes are answered in `lifecycle.ts` after the handoff is verified and before
the surfaces, so neither enters the operation table or the MCP table; any other verb on those paths is
`404 unknown_operation`. `204` is not an `HttpStatus`, so the account response has its own file-local
type rather than widening the type the game surface uses. The in-memory store keeps one engine layer per
owner with a live-session cache, so a successful `deleteAll` also drops that layer; without it a deleted
session stays reachable. An in-memory save has no stored expiry, so its listed `expiresAt` is its creation
time plus the save TTL, a nominal value; the durable store reports the column.
Rejected: **deleting only the four named tables**, which leaves personal data behind. **Registering the
routes as operations**, which would put them on the MCP surface and break the parity S8.5 asserts.
**Adding 204 to `HttpStatus`**, which every game-surface handler would then have to consider.
Reversibility: cheap, except the six-table list, which a new owned table must extend.

### 2026-11-03 — S10: the limiter keys on the principal or the proxy's address, counts only admitted requests, and "disabled" means a limit that cannot fire

Context: S10 adds the abuse limiter of invariant 121. The contract fixes the key, the response and the
default; six points it leaves open.
Chosen: a fixed window that opens at the first request after the last one closed, in memory per instance.
A refused request is not counted, so a caller that keeps asking is admitted again when its window closes
rather than being held out by its own retries. `Retry-After` is the window's remaining time rounded up to
whole seconds, at least one. The limiter runs after the Terminate-mode `401`, before the assertion is
signed and the body read, so a refused credential is never counted. An unauthenticated caller is counted
under the connection's address, or, when the connection is a configured trusted proxy that sent
`X-Forwarded-For`, under the last entry, the one that proxy appended; earlier entries are the caller's
claim and a caller must not choose its own key. The contract has no off switch, so S10.4's "limiting
disabled by configuration" is a `PermitCount` of `int.MaxValue`. When `AbuseLimits` is present both fields
are required, `PermitCount` at least 1 and `Window` at least one second, because `Retry-After` is whole
seconds; a breach stops startup naming the key. Expired counters are swept at most once per window. No
telemetry counter is added, since none is specified.
Rejected: **a sliding window**, which costs a list of timestamps per caller for a bound no acceptance row
asks for. **Counting refused requests**, which makes a polite client retrying on `Retry-After` pay for its
earlier flood. **The first `X-Forwarded-For` entry**, which the caller writes. **A boolean to disable
limiting**, a signature the contract does not carry.
Reversibility: cheap.

### 2026-11-04 — S11: the composition job is a gate over the four fresh-clone jobs, and the rollback is rehearsed against a route table

Context: S11.1 asks for one CI job that, from a fresh clone, runs every proof and fails if any fails. The
proofs already run as four jobs (`build`, `game-service`, `game-service-isolation`, `adventures-proof`),
each from a fresh clone and each needing its own toolchain, network rule or privilege; one job that
repeated them would need all four setups at once and prove nothing the four do not. S11.3 asks for a
rollback rehearsed once, and the reverse proxy is not in this repository.
Chosen: `g3-composition` is a job that needs all four, runs even when one fails, and fails unless every
one succeeded; its result is the single answer to "do all of G3's proofs hold on a fresh clone". It
carries a comment saying what each job covers and that it is not the live smoke check. The rollback is
rehearsed by `workloads/adventures-proof/tests/cutover-rollback.test.ts` through a test-only route table
(`support/route-table.ts`) that holds one public address and switches between the direct route and the
edge, with a tap behind the edge only so a request is known to be on the cutover route exactly when the
tap saw it. The documented procedure is the same one change: point the host's route back at Adventures.
Rejected: **a fifth job that re-runs everything**, which doubles CI time and maintains two copies of each
job's setup. **Rehearsing against a real proxy in CI**, which would test the proxy's configuration, not
this repository's code, and which S12 does on the live host.
Reversibility: cheap. Nothing checks that the gate's `needs` list stays complete: a new proof job added
outside it would not be covered by `g3-composition`.
