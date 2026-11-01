# Slices — principals (G3)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a signature the contract
does not carry, it stops and asks for a contract amendment rather than inventing one.

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code
that satisfies them. Everything is in this repository except [S7](#s7--adventures-through-the-edge),
which runs a pinned Adventures revision without changing it, and
[S12](#s12--adventures-api-live-through-the-edge), which changes the deployment.

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

Neither the design nor the contract settles these, and no slice may settle one silently. OQ numbers
are [`10-design.md`](10-design.md#open-questions)'s.

| Question | Needed before |
|---|---|
| **A contract amendment.** The contract declares `forRequest(owner)`, `ownershipDecorator` and `verifyHandoff`, but not the signatures below them. That covers the maximum lifetime `lifetime_exceeded` is checked against, which `HandoffSettings` does not carry. It also covers the test-scope assertion minter the harnesses use (invariant 124), the Store's owner-scope parameter and the `DurableStore` members that run under it, the store reads and deletes behind the account surface, and `ConformanceTarget`'s second owner | **S1** for the maximum lifetime and the minter. **S2** for the owner scope, **S4** for the conformance target, **S8** for the account reads and deletes |
| **The brief's "ownership is checked at the edge".** The design authenticates at the edge and enforces ownership in game-service's store, behind a handoff only the edge can sign. The owner must confirm that this reading meets the brief, or amend the brief | **S2** |
| OQ3: whether pre-G3 rows are kept under the reserved owner or deleted | **S9**. S2's migration stamps the reserved owner as the contract declares, and S9 holds the agreed treatment |
| OQ2: account deletion beyond invariant 120's floor | **S8**'s criteria S8.6 and S8.7 |
| OQ4: which Adventures routes move behind the edge | **S7** and **S12** |
| OQ5: cutover and topology | **S11**'s rollback documentation, and **S12** |
| OQ7: whether game-service needs a live deployment | **S12**. If yes, a slice is appended |
| OQ8: the abuse-limit defaults and the instance count | **S10**'s default values. Its criteria use configured values and do not wait |
| OQ1: MCP's path | **Not blocking any slice.** S1 gates MCP either way (invariant 109). If MCP is in scope, a proof slice is appended. If not, S11 documents MCP as unavailable for owned state |
| OQ6 and OQ9 | **Not blocking any slice.** S11 records what each answer means for operators |

---

## S1 — game-service refuses requests the edge did not sign
**Status:** in progress

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
**Status:** queued

Delivers: a signed-in player's sessions, saves and profiles belong to them. Another player who
sends the same ids sees only what a stranger would: nothing there.

Touches:
- **Migrations**: the one G3 migration (owner columns, profile keys, indexes, reserved owner for
  existing rows)
- **Store**: an owner predicate on every statement, and the owner stamped on every insert
- **Ownership decorator**: `ownershipDecorator`
- **Composition**: `forRequest(owner)`, with the process-lived lifecycle probe wrapped per request
  and used
- **In-memory target**: owner-scoped the same way

Depends on: S1, and the contract amendment's store members.

Acceptance:
- **S2.1** Migrating a G2 schema holding one session, one save and one profile leaves all three
  rows present, with `owner_issuer = ''` and `owner_subject = ''`, and `profile`'s primary key is
  `(tenant_id, owner_issuer, owner_subject, profile_id)`.
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
- **S2.9** An assertion whose `pis` and `psub` are both empty is refused before composition runs,
  and the reserved-owner rows from S2.1 stay unreachable to every request.

Out of scope: every remaining id-bearing operation and both surfaces (S4). The edge (S3). What
happens to the reserved-owner rows afterwards (S9). The account surface (S8).

## S3 — A signed-in player plays through the edge
**Status:** queued

Delivers: a player holding a token from the game's issuer can create and play a session through
the edge, and the edge alone vouches for who they are.

Touches:
- **Edge, Authentication**: Platform Identity's bearer chain composed in `Terminate` mode
- **Edge configuration**: `CredentialMode`, `HandoffOptions`
- **Handoff signer**
- **Forwarder**: refusing `Anonymous` in terminate mode, consuming `Authorization`, stripping a
  caller's handoff header
- **Proof harness**: a test issuer serving a real discovery document and key set and signing real
  tokens, composed with the real edge, game-service and Postgres

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
- **S3.8** The composition in S3.1 runs in CI from a fresh clone, with no network access beyond the
  composed processes.

Out of scope: token failure outcomes beyond "no credential" (S5). Every other header (S6).
Pass-through mode (S6). Abuse limits (S10).

## S4 — No operation crosses principals
**Status:** queued

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
**Status:** queued

Delivers: a player whose sign-in has failed, expired or is missing gets a clear refusal, and an
operator can tell a player's bad credential from the edge's own trouble reaching the issuer.

Touches:
- **Edge, Authentication**: Platform's existing mapping, exercised
- **Edge readiness**: Identity's key-set readiness check
- **Proof harness**: test-issuer failure modes

Depends on: S3.

Acceptance:
- **S5.1** A token signed by a key outside the issuer's key set returns `401 CredentialRejected`.
- **S5.2** An expired token returns `401 CredentialRejected`. A token for another audience returns
  the same.
- **S5.3** A malformed `Authorization` value returns `401 CredentialRejected`, never `Anonymous`.
- **S5.4** With the test issuer unreachable before any key set is fetched, a token returns
  `401 KeyMaterialUnavailable` and the edge's readiness reports unhealthy.
- **S5.5** With the test issuer unreachable after one successful fetch, valid tokens keep
  succeeding and readiness reports degraded.
- **S5.6** A provider failure injected mid-validation returns `401 ProviderFailed`.
- **S5.7** Sign-out: a token kept after the client discards it still succeeds until its `exp`, and
  returns `401 CredentialRejected` after it. This asserts the contract's stated outcome.
- **S5.8** In S5.1–S5.7, game-service receives no request whenever the edge answers `401`.

Out of scope: token introspection or revocation. Changing any status Platform's pipeline maps.

## S6 — Each header follows its mode, in both directions
**Status:** queued

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

Out of scope: Adventures itself (S7). Rewriting a cookie's `Domain` or `Path`.

## S7 — Adventures through the edge
**Status:** queued

Delivers: Adventures' players keep their guest sessions, sign-in and cross-origin play when their
traffic passes through Platform's edge.

Touches:
- **Proof harness**: a pinned Adventures revision behind a pass-through edge, with the test issuer
  as its OIDC issuer

Depends on: S6. OQ4 for the route list.

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

Out of scope: any change to the Adventures repository. The live deployment (S12).

## S8 — Players can list their saves and delete their account
**Status:** queued

Delivers: a signed-in player can see every save they hold and can delete their account. No other
player can see or delete either.

Touches:
- **Account surface**: `GET /account/saves`, `DELETE /account`, `AccountSaveEntry`
- **Store**: the owner-wide reads and the deletion transaction from the contract amendment

Depends on: S4, and the contract amendment's account members. OQ2 for S8.6 and S8.7.

Acceptance:
- **S8.1** A, holding saves under two profiles, gets every save from `GET /account/saves`, with
  exactly the `AccountSaveEntry` fields and no blob, scene or other engine field.
- **S8.2** B's `GET /account/saves` returns none of A's saves.
- **S8.3** `DELETE /account` by A returns `204`. Afterwards SQL finds no `session`, `save`,
  `profile` or `profile_achievement` row with A's owner, and B's rows are unchanged.
- **S8.4** A failure injected mid-deletion leaves every one of A's rows in place.
- **S8.5** Neither route appears in the operation table or the MCP tool table, and the game surface
  lists the same operations as before S8.
- **S8.6** *(Waits on OQ2.)* The agreed treatment of a write in flight when deletion begins.
- **S8.7** *(Waits on OQ2.)* The agreed outcome of a creation by A's still-valid credential after
  deletion, and the agreed effect on anything beyond game-service's rows.

Out of scope: Adventures' own account data. Platform account records. The issuer.

## S9 — Pre-G3 rows: the agreed treatment
**Status:** queued

Delivers: rows saved before players had owners are handled the way the owner agreed, and an
operator can read what happened to them.

Touches:
- **Migrations**: only if OQ3 is "delete"
- **Operator documentation**: the treatment

Depends on: S2. OQ3.

Acceptance:
- **S9.1** *(Waits on OQ3.)* After migrating a G2 schema with seeded rows, the outcome is the
  agreed one. Under "unreachable", no principal's request returns any seeded row, and G2's sweep
  removes the expired sessions and saves. Under "delete", none remains.
- **S9.2** The operator documentation states the treatment and what happens to reserved-owner
  profiles.

Out of scope: migrating Adventures' rows.

## S10 — Abuse limits answer before they forward
**Status:** queued

Delivers: one caller flooding the edge is slowed down and told when to retry, without affecting
whose data anyone sees.

Touches:
- **Abuse limiter**
- **Edge configuration**: `AbuseLimitOptions`

Depends on: S3. OQ8 for the defaults.

Acceptance:
- **S10.1** With `PermitCount` at 3 and `Window` at 10 s, principal A's fourth request within the
  window returns `429 RateLimited` with a `Retry-After` header, and game-service receives three
  requests.
- **S10.2** In S10.1, principal B's first request succeeds.
- **S10.3** An unauthenticated caller in pass-through mode is limited by client address under the
  same settings, and a second address's first request succeeds.
- **S10.4** A request admitted by the limiter forwards bytes identical to the same request with
  limiting disabled by configuration (invariant 41, restated).
- **S10.5** *(Waits on OQ8.)* The shipped defaults are the agreed values.

Out of scope: a shared limiter across instances. Limiting before authentication.

## S11 — A fresh clone proves all of it, and an operator can run it
**Status:** queued

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
- **S11.3** *(Waits on OQ5.)* The rollback procedure, rehearsed once in the reproducible setup.
- **S11.4** The documentation states the MCP path's G3 status as OQ1 resolved it.

Out of scope: the live cutover (S12).

## S12 — adventures-api live through the edge
**Status:** queued

Delivers: Adventures' real players reach it through Platform's edge on the existing VPS, with
nothing they can notice except that it still works.

Touches:
- **Deployment**: owned per OQ5

Depends on: S7, S11. OQ4, OQ5, OQ7.

Acceptance:
- **S12.1** *(Waits on OQ5.)* `adventures-api.subzerodev.com` is served through the edge with TLS.
- **S12.2** A live smoke check through the public API records the deployed edge and Adventures
  revisions. It verifies the agreed routes, sign-in, a guest session, a cross-origin write, and the
  agreed treatment of existing sign-ins.
- **S12.3** *(Waits on OQ7.)* Whatever live check game-service needs.

Out of scope: moving Adventures' persistence onto game-service.
