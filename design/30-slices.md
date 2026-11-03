# Slices — principals (G3)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a signature the contract
does not carry, it stops and asks for a contract amendment rather than inventing one.

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code
that satisfies them. Everything is in this repository except [S7](#s7--adventures-through-the-edge),
which runs a pinned Adventures revision without changing it, and
[S12](#s12--adventures-api-live-through-the-edge) and
[S14](#s14--game-service-live-behind-its-own-edge), which change the deployment.

**The ordering is the design's risk ordering.** The design's biggest bet is that ownership can be
enforced entirely behind the storage seam: one decorator, one owner predicate per statement, the
process-lived lifecycle probe wrapped rather than rebuilt, and engine bytes untouched.
[S2](#s2--the-store-answers-only-to-its-owner) proves that bet. S1 is the smallest path to it,
because the decorator needs a verified principal to scope by. S3 adds the real edge. **S1 to S3
together are the brief's narrowest useful version**: a signed-in player creates a session through
the edge and plays it, another principal is refused it, and serialization stays byte-identical.
Everything after S3 widens coverage to every operation, every credential outcome, every header,
Adventures, the account surface and the deployment.

## Decisions that must be taken before the slice that needs them starts

The contract amendment, the brief's ownership wording and Owner questions 1–9 were settled on
2026-11-02 ([`90-decisions.md`](90-decisions.md)), and the slices below carry the answers. Two
remain, and no slice may settle one silently.

| Question | Needed before |
|---|---|
| The live game-service hostname | **S14**'s criteria S14.1 and S14.2 |
| **Owner question:** which client registration at Adventures' OIDC issuer obtains tokens for game-service, and so the audience the edge accepts | **S14**'s criteria S14.1 and S14.2 |

---

## S1 — game-service refuses requests the edge did not sign
**Status:** shipped

Delivers: whoever operates game-service can expose it knowing that a request which bypasses the
edge, or forges the edge's signature, is refused before it touches any game.

Touches:
- **Handoff verifier**: `verifyHandoff`, `HandoffSettings`, `HandoffRefusal`, `OwnerKey`
- **Composition**: reading the verification keys at startup, and refusing to start without one
- **HTTP surface, MCP surface**: the gate ahead of Dispatch on every non-probe path
- **Proof harness**: the test-key assertion minter, used by G2's harnesses and replay

Depends on: the contract amendment's maximum lifetime and test minter.

Acceptance:
- **S1.1** game-service started with no handoff verification key exits non-zero before listening,
  and its error names the missing setting.
- **S1.2** `POST` to any operation route with no `SubZeroDev-Principal-Assertion` header returns
  `401` with code `assertion_refused`, and no store statement runs (asserted by statement count on
  the durable target).
- **S1.3** For each `HandoffRefusal` value, one request constructed to produce it returns
  `401 assertion_refused` with no reason in the body, and the log records that value.
  `request_mismatch` is exercised once for `htm`, once for the path and once for the query, and
  `empty_owner` once for `pis` and once for `psub`.
- **S1.4** A valid assertion for the request's exact method, path and query passes, and the
  operation's response is byte-identical to G2's for the same input.
- **S1.5** `/livez` and `/readyz` answer without an assertion, exactly as in G2.
- **S1.6** An MCP transport request with no assertion returns `401 assertion_refused`.
- **S1.7** No log line, span attribute or error body produced in S1.2–S1.6 contains the assertion
  string, asserted by searching captured telemetry for it.
- **S1.8** G2's replay, two-instance contention and byte-identity proofs pass unchanged, with
  harness requests carrying test-key assertions.

Out of scope: any owner on any row (S2). The edge signing anything (S3). Key rotation beyond
accepting several configured key ids.

## S2 — The store answers only to its owner
**Status:** shipped

Delivers: a signed-in player's sessions, saves and profiles belong to them. Another player who
sends the same ids sees only what a stranger would: nothing there.

Touches:
- **Migrations**: the one G3 migration (owner columns defaulting to the reserved owner, profile
  keys, indexes, and deletion of every existing row)
- **Store**: an owner predicate on every statement, and the owner stamped on every insert
- **Ownership decorator**: `ownershipDecorator`
- **Composition**: `forRequest(owner)`, with the process-lived lifecycle probe wrapped per request
  and used
- **In-memory target**: owner-scoped the same way

Depends on: S1, and the contract amendment's store members.

Acceptance:
- **S2.2** Principal A creates a session. Principal B then calls `get-session` and `act` with that
  session id, and both responses are byte-identical to the same calls with a never-minted id
  (`404 unknown_session`).
- **S2.3** A session of A's expires. B's `get-session` on its id returns `unknown_session`, never
  `session_expired`, and A's returns `session_expired`.
- **S2.4** A and B each call `list-saves` with `profileId: "default"` after A saves once. A sees one
  save and B sees none. A `load-save` by B on A's save id returns `404 unknown_save`.
- **S2.5** Every inserted `session`, `save`, `profile` and `profile_achievement` row carries the
  inserting principal's issuer and subject, read back with SQL.
- **S2.6** No `StoredSessionRecord`, `StoredSaveRecord`, `Profile` or blob produced in S2.2–S2.4
  contains either owner column's value.
- **S2.7** For G2's replay fixture, the decorated store's `serialize()` output is byte-identical to
  the undecorated store's.
- **S2.8** The lifecycle probe object is the same instance across 100 requests (invariant 69), and
  Dispatch's probe on each request is the decorated one (invariant 116).
- **S2.10** After migrating a G2 schema, `owner_issuer` and `owner_subject` are not null on all
  four tables, and `profile`'s primary key is `(tenant_id, owner_issuer, owner_subject, profile_id)`.
- **S2.11** An assertion whose `pis` and `psub` are both empty is refused before composition runs.

Out of scope: every remaining id-bearing operation and both surfaces (S4). The edge (S3). The proof
that pre-G3 rows are deleted (S9). The account surface (S8).

## S3 — A signed-in player plays through the edge
**Status:** shipped

Delivers: a player holding a token from the game's issuer can create and play a session through
the edge, and the edge alone vouches for who they are.

Touches:
- **Edge, Authentication**: Platform Identity's bearer chain composed in `Terminate` mode, with the
  issuer's keys pinned in `BearerSettings.SigningKeys`
- **Edge configuration**: `CredentialMode`, `HandoffOptions`
- **Handoff signer**
- **Forwarder**: refusing `Anonymous` in terminate mode, consuming `Authorization`, stripping a
  caller's handoff header
- **Proof harness**: a test issuer signing real tokens, whose public keys the edge pins, composed
  with the real edge, game-service and Postgres

Depends on: S2.

Acceptance:
- **S3.1** Through the edge, principal A with a valid test-issuer token creates a session and
  performs `act` on it, and both succeed.
- **S3.2** Principal B's valid token on A's session id returns `404 unknown_session` through the
  edge.
- **S3.3** No `Authorization` header in terminate mode returns `401 CredentialRequired` from the
  edge, and game-service receives no request.
- **S3.4** The request game-service receives in S3.1 carries no `Authorization`, and carries exactly
  one `SubZeroDev-Principal-Assertion`. That assertion verifies under the S1 rules, and its `pis`
  and `psub` equal the token's issuer and subject.
- **S3.5** A caller-supplied `SubZeroDev-Principal-Assertion` sent with a valid token never reaches
  game-service: the forwarded value is the edge's.
- **S3.6** The edge refuses to start in `Terminate` mode with no `HandoffOptions`, or with an
  unreadable signing key file. It also refuses to start in `PassThrough` mode with `HandoffOptions`
  set. Each refusal names the setting.
- **S3.7** No log line, span attribute or metric label produced in S3.1–S3.5 contains the bearer
  token or the assertion.
- **S3.8** The composition in S3.1 runs in CI from a fresh clone, with dependencies installed first,
  as a user whose every non-loopback packet over IPv4 and IPv6, and every DNS query, is rejected and
  counted. The run fails on any counted rejection, on a guard that saw no loopback traffic from the
  suite, and on a skipped test; a negative control shows the guard rejects each path.

Added to the slice, by the 2026-11-03 decision ([`90-decisions.md`](90-decisions.md),
[ADR-010](../docs/docs/adr/ADR-010-authenticated-transport-profile.md)): the edge's terminate mode needs
a composition profile that neither `Local` nor `Operated` can state, so the slice also delivers
`AuthenticatedTransport` (invariants 130 to 132), proved in
`tests/SubZeroDev.Platform.Tests/AuthenticatedTransportProfileTests.cs` and
`workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/TerminateModeTests.cs`. A mode the profile cannot
hold is not started in that in-process suite, where the host's own disposal can be reported in place of
the refusal; S3.6 starts the real edge for each pairing and asserts the refusal's code.

Proved by `workloads/game-service/tests/s3-edge.test.ts` (the real edge in `Terminate` mode, the real
game-service and a real PostgreSQL schema, with a test issuer whose keys the edge pins), one `describe`
per criterion: S3.1 to S3.8. S3.7's span half needs `OTEL_COLLECTOR_BIN` and runs in CI only; its
process-output half runs everywhere. S3.8 is the `game-service-isolation` job in
`.github/workflows/build.yml`, which runs that file under the outbound guard
(`build/outbound-guard.sh`). The file's test of it only shows a token of an unresolvable issuer is
accepted; a connection attempt is seen by the job's counters, not the test.

Out of scope: token failure outcomes beyond "no credential" (S5). Every other header (S6).
Pass-through mode (S6). Abuse limits (S10).

## S4 — No operation crosses principals
**Status:** shipped

Delivers: every game operation that names a session, save or profile is proven to refuse another
player's ids, through both the HTTP and MCP surfaces, on both store targets.

Touches:
- **Proof harness**: the cross-principal matrix
- **Port conformance**: two owners, both targets

Depends on: S3, and the contract amendment's `ConformanceTarget` member.

Acceptance:
- **S4.1** For every operation in the vendored contract carrying `sessionId`, `saveId` or
  `profileId`, principal B's request on A's id produces the response a never-minted id produces.
  This holds for reads and writes, and the matrix fails if a contract operation carrying one of
  those fields is absent from it.
- **S4.2** After every refused write in S4.1, A's rows are byte-identical to before it, read back
  with SQL.
- **S4.3** S4.1 holds on the MCP surface for each tool carrying the same fields.
- **S4.4** The port-conformance suite runs every G2 assertion under owner A and under owner B on
  both targets, and asserts that each of the seven port methods sees nothing of the other owner's
  rows.
- **S4.5** `branch-session` by B from A's session returns `404 unknown_session`, and creates no
  row.

Out of scope: the account surface's listing (S8). Credential outcomes (S5).

## S5 — Every credential outcome is told apart
**Status:** shipped

Delivers: a player whose sign-in has failed, expired or is missing gets a clear refusal, an
operator can tell a player's bad credential from a fault in the edge's own validation, and the edge
keeps working when the issuer is down.

Touches:
- **Edge, Authentication**: Platform's existing mapping, exercised
- **Edge configuration**: pinned `SigningKeys`, no `Discovery`
- **Proof harness**: the test issuer stopped, and a key the edge does not pin

Depends on: S3.

Acceptance:
- **S5.1** A token signed by a key the edge does not pin returns `401 CredentialRejected`.
- **S5.2** An expired token returns `401 CredentialRejected`. A token for another audience returns
  the same.
- **S5.3** A malformed `Authorization` value returns `401 CredentialRejected`, never `Anonymous`.
- **S5.6** A provider failure injected mid-validation returns `401 ProviderFailed`.
- **S5.7** Sign-out: a token kept after the client discards it still succeeds until its `exp`, and
  returns `401 CredentialRejected` after it. This asserts the contract's stated outcome.
- **S5.8** In S5.1–S5.7, game-service receives no request whenever the edge answers `401`.
- **S5.9** With the test issuer stopped, valid tokens keep succeeding through the edge, and the
  edge opens no outbound connection except to game-service.
- **S5.10** The edge refuses to start in `Terminate` mode with a `Discovery` address, or with no
  `SigningKeys`, and each refusal names the setting.

Delivered ahead of this slice, by the PR #370 review fix: the startup guard (`PinnedKeys` in the edge)
refuses a provider with a `Discovery` address, even beside `SigningKeys`, and one with no `SigningKeys`,
naming the full key and before any connection to the issuer. It is proved by `PinnedKeysTests.cs`,
`TerminateModeTests.cs` and the "Invariant 125" `describe` in `s3-edge.test.ts`, which counts connections
to a listener standing in for the issuer. S5.1 to S5.9 are delivered by this slice: `s5-credentials.test.ts`
(S5.1–S5.3, S5.7–S5.9, run again under the outbound guard in `game-service-isolation`) and
`TerminateModeTests.cs` (S5.6), and S5.3 needed a fix in Platform's bearer provider (decision 2026-11-03).

Out of scope: token introspection or revocation. Changing any status Platform's pipeline maps.

## S6 — Each header follows its mode, in both directions
**Status:** shipped

Delivers: whichever kind of workload sits behind the edge, its callers' cookies, origins and
forwarding headers reach it exactly as its mode says. No caller's header can impersonate the edge
or another caller.

Touches:
- **Credential policy**: the per-mode rules in [`10-design.md`](10-design.md) *Control flow* §6
- **Edge configuration**: `PassThrough`, `TrustedProxies`
- **Forwarder**: the policy applied on the buffered and streamed paths

Depends on: S3.

Acceptance:
- **S6.1** There is one test per header row of the design's table, in each applicable direction,
  in each mode. Each runs on the buffered path and on a configured streaming route, and passes.
- **S6.2** In pass-through mode, `Cookie` and `Origin` each reach the workload exactly as sent,
  for requests carrying both, only `Cookie` and only `Origin`. In terminate mode, neither reaches it
  for any of the three.
- **S6.3** `A_cookie_the_workload_sets_for_one_caller_is_never_sent_on_anothers_request` passes in
  pass-through mode.
- **S6.4** A response with three `Set-Cookie` values reaches a pass-through caller as three values,
  byte-identical, and reaches a terminate caller as none.
- **S6.5** Caller-sent `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host` and `Forwarded`
  never reach the workload. The workload sees the edge's values from the connection. When
  `TrustedProxies` lists the connecting address, it sees that proxy's `X-Forwarded-For` instead.
- **S6.6** A pass-through preflight `OPTIONS` reaches the workload, and its `Access-Control-*`
  response headers reach the caller unchanged. The edge adds none.
- **S6.7** The edge's existing failure, timeout, streaming-abort and no-retry tests pass in both
  modes.

Delivered by `HeaderPolicy.cs` in the edge, one allow-list used by the buffered and the streamed relay,
and proved by `HeaderPolicyContract.cs` (the rows both modes share, run in each mode),
`PassThroughHeaderTests.cs` and `TerminateHeaderTests.cs`, every row on `/plain/x` and on a configured
streaming route, against the edge on a real listener. S6.3 and S6.7 are the existing
`EdgeHostTests`, `ForwardingTests`, `StreamingTests` and `TerminateModeTests`, unchanged and green. The
decisions of 2026-11-03 record the allow-list and `TrustedProxies`.

Out of scope: Adventures itself (S7). Rewriting a cookie's `Domain` or `Path`.

## S7 — Adventures through the edge
**Status:** shipped

Delivers: Adventures' players keep their guest sessions, sign-in and cross-origin play when their
traffic passes through Platform's edge.

Touches:
- **Proof harness**: a pinned Adventures revision behind a pass-through edge, with the test issuer
  as its OIDC issuer, and every route of Adventures' API routed through the edge

Depends on: S6.

Acceptance:
- **S7.1** A guest session started through the edge persists across three requests carrying the
  `Set-Cookie` the first response set.
- **S7.2** An OIDC sign-in against the test issuer completes through the edge. Its `3xx` `Location`
  values and `sza_oauth_state` cookie survive the round trip, and the signed-in session is then
  recognised.
- **S7.3** A write from a second origin with `Origin` and credentials succeeds through the edge,
  and the same write with a disallowed `Origin` is refused by Adventures, not by the edge.
- **S7.4** The Adventures revision is pinned by commit in the harness, and the proof runs in CI
  from a fresh clone.

Proved by `workloads/adventures-proof` (a test-only project; see its README): the Adventures commit named
in `adventures-pin.json`, cloned and installed by `npm run prepare-adventures`, run as a child process on
a per-run database behind the real edge in `PassThrough` mode, with a recording proxy between them and a
test issuer the browser and Adventures both reach. S7.1 to S7.3 are the three tests in
`adventures-through-edge.test.ts`; S7.3's refusal is shown to be Adventures' own by the recorded
exchange, in which the request reached Adventures with the foreign `Origin` and the 403 body left it.
S7.4 is the `adventures-proof` job in `build.yml`, which clones, prepares and runs the proof on a fresh
runner. The decisions of 2026-11-03 record the issuer's transport and the pin.

Out of scope: any change to the Adventures repository. The live deployment (S12).

## S8 — Players can list their saves and delete their account
**Status:** shipped

Delivers: a signed-in player can see every save they hold and can delete their account. No other
player can see or delete either.

Touches:
- **Account surface**: `GET /account/saves`, `DELETE /account`, `AccountSaveEntry`
- **Store**: the owner-wide reads and the deletion transaction from the contract amendment

Depends on: S4.

Acceptance:
- **S8.1** A, holding saves under two profiles, gets every save from `GET /account/saves`, with
  exactly the `AccountSaveEntry` fields and no blob, scene or other engine field.
- **S8.2** B's `GET /account/saves` returns none of A's saves.
- **S8.3** `DELETE /account` by A returns `204`. Afterwards SQL finds no `session`, `save`,
  `profile` or `profile_achievement` row with A's owner, and B's rows are unchanged.
- **S8.4** A failure injected mid-deletion leaves every one of A's rows in place.
- **S8.5** Neither route appears in the operation table or the MCP tool table, and the game surface
  lists the same operations as before S8.
- **S8.6** A write by A that commits before `DELETE /account` begins is deleted with the rest, and
  A's `act` on that session afterwards returns `404 unknown_session`.
- **S8.7** After deletion, A's still-valid token still authenticates, A's `create-session`
  succeeds, and A's `GET /account/saves` lists nothing from before the deletion.

Proved by `workloads/game-service/tests/account.test.ts`, which runs S8.1, S8.2, S8.6 and S8.7 against
both the in-memory and the durable store, and S8.3 and S8.4 against PostgreSQL only (the in-memory store
has no SQL to read back; S8.4's failure is a trigger on `session` that raises mid-transaction). S8.5 is
asserted against the contract's operation rows and `/mcp/list-tools`, and by the account paths answering
`404 unknown_operation` to any other verb. The decisions of 2026-11-03 record the six tables and the
in-memory behaviour.

Out of scope: Adventures' own account data. Platform account records. The issuer.

## S9 — Pre-G3 rows are deleted at migration
**Status:** shipped — acceptance met by #369; marked shipped when S3–S8 ahead of it shipped

Delivers: rows saved before players had owners are gone once G3 migrates, and an operator knows
that before upgrading.

Touches:
- **Proof harness**: a G2 schema seeded in all four tables, migrated
- **Operator documentation**: the deletion

Depends on: S2.

Acceptance:
- **S9.1** After migrating a G2 schema seeded with rows in `session`, `save`, `profile` and
  `profile_achievement`, none of those rows remains, read back with SQL.
- **S9.3** The operator documentation states that the G3 migration irreversibly deletes every
  pre-G3 session, save and profile.

Out of scope: migrating Adventures' rows.

## S10 — Abuse limits answer before they forward
**Status:** shipped

Delivers: one caller flooding the edge is slowed down and told when to retry, without affecting
whose data anyone sees.

Touches:
- **Abuse limiter**
- **Edge configuration**: `AbuseLimitOptions`

Depends on: S3.

Acceptance:
- **S10.1** With `PermitCount` at 3 and `Window` at 10 s, principal A's fourth request within the
  window returns `429 RateLimited` with a `Retry-After` header, and game-service receives three
  requests.
- **S10.2** In S10.1, principal B's first request succeeds.
- **S10.3** An unauthenticated caller in pass-through mode is limited by client address under the
  same settings, and a second address's first request succeeds.
- **S10.4** A request admitted by the limiter forwards bytes identical to the same request with
  limiting disabled by configuration (invariant 41, restated).
- **S10.5** An edge started with no `AbuseLimits` setting limits at `PermitCount` 60 and `Window`
  60 seconds.

Proved by `workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/AbuseLimitTests.cs`, which runs each
edge on a real listener because the anonymous rows count the connection's address and the in-memory test
server has none. S10.4 sends the same request through an edge limited at 3 and one limited at
`int.MaxValue` in front of one workload and compares what arrived. The test also shows that a refused
credential is answered `401` however often it asks and is never counted, and that a caller cannot choose
its own key through `X-Forwarded-For`. The decision of 2026-11-03 records how "disabled by configuration"
is modelled.

Out of scope: a shared limiter across instances. Limiting before authentication.

## S11 — A fresh clone proves all of it, and an operator can run it
**Status:** shipped

Delivers: anyone who clones the repository can rerun every G3 proof, and an operator has what they
need to configure, verify and roll back a deployment.

Touches:
- **CI workflow**: the G3 composition job
- **Operator documentation**: the edge README, the game-service README and the hosting guide

Depends on: S4–S10.

Acceptance:
- **S11.1** One CI job, from a fresh clone, runs S1–S10's proofs, G2's proofs and
  `build/Test-WorkloadIsolation.ps1`, and fails if any fails.
- **S11.2** The operator documentation covers each mode's settings, the handoff key and rotation
  order, the issuer, limits, verification, and the fact that CI is not the live smoke check.
- **S11.3** The rollback procedure restores the reverse proxy's direct route to Adventures, and is
  rehearsed once in the reproducible setup.
- **S11.4** The documentation states that MCP reaches game-service only through the edge, under the
  same ownership as HTTP.
- **S11.5** The documentation states that the edge fetches nothing from the issuer, and how to pin
  a new issuer key before the issuer signs with it.

Proved by the `g3-composition` job in `.github/workflows/build.yml`, which needs the four jobs that each run from a fresh clone (`build`, `game-service`, `game-service-isolation`, `adventures-proof`), runs even when one fails, and fails unless all succeeded. The one job is a gate over them rather than a fifth job repeating their setups. S11.3 is rehearsed by `workloads/adventures-proof/tests/cutover-rollback.test.ts` against a route table standing in for the proxy. S11.2, S11.4 and S11.5 are "Operating a deployment" in `workloads/game-edge/README.md`, with pointers from the game-service README and the hosting guide.

Out of scope: the live cutover (S12).

## S12 — adventures-api live through the edge
**Status:** in progress

Delivers: Adventures' real players reach it through Platform's edge on the existing VPS, with
nothing they can notice except that it still works.

Touches:
- **Deployment**: Platform's, with the edge behind the existing reverse proxy

Depends on: S7, S11.

Acceptance:
- **S12.1** `adventures-api.subzerodev.com` is served through the edge, deployed from this
  repository behind the existing reverse proxy, which keeps TLS.
- **S12.2** A live smoke check through the public API records the deployed edge and Adventures
  revisions. It verifies every Adventures API route, sign-in, a guest session and a cross-origin
  write, and that a sign-in made before cutover is still recognised after it.

Out of scope: moving Adventures' persistence onto game-service. game-service live (S14).

## S13 — An MCP client plays through the edge
**Status:** queued

Delivers: an MCP client signed in as a player reaches that player's games through the edge, and
only through it.

Touches:
- **Proof harness**: an MCP client composed with the real edge in `Terminate` mode, game-service and
  Postgres

Depends on: S4.

Acceptance:
- **S13.1** An MCP client with principal A's valid token creates a session and acts on it through
  the edge, and both succeed.
- **S13.2** Principal B's MCP tool call through the edge on A's session id returns what a
  never-minted id returns.
- **S13.3** An MCP request sent straight to game-service with a valid bearer token and no assertion
  returns `401 assertion_refused`.
- **S13.4** S11's CI job runs S13's proofs.

Out of scope: any MCP-specific authentication. MCP clients authenticate at the edge like HTTP
callers.

## S14 — game-service live behind its own edge
**Status:** queued

Delivers: real players sign in through Adventures' issuer and play game-service sessions live,
through Platform's edge, on the existing VPS.

Touches:
- **Deployment**: a second edge in `Terminate` mode and game-service, on the existing VPS behind the
  existing reverse proxy, under game-service's own hostname, pinning Adventures' issuer keys

Depends on: S11, S13. The hostname and the client registration (*Decisions* above).

Acceptance:
- **S14.1** *(Waits on the hostname and the client registration.)* game-service is served through
  its own edge under its hostname, behind the existing reverse proxy, which keeps TLS.
- **S14.2** *(Waits on the hostname and the client registration.)* A live smoke check signs in
  through Adventures' OIDC issuer, creates a session and acts on it through the edge, and records
  the deployed edge and game-service revisions.
- **S14.3** In the same check, a second principal's request on the first's session id returns
  `404 unknown_session`.
- **S14.4** A request reaching game-service's listener without passing through the edge returns
  `401 assertion_refused`.

Out of scope: moving Adventures' persistence onto game-service.
