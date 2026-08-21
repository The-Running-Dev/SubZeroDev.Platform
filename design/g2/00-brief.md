# Brief — durable sessions (G2)

> Written by me, not by a model. A model may interrogate it (`/brief-check`) but not author it.
>
> **Provenance of this draft:** the decisions below were taken by me in answer to direct questions
> on 2026-08-12 and transcribed. The *Problem* statement and the *Environment* section are drafted
> from this repository's own documents, the engine's source, and a reading of
> [`SubZeroDev.Adventures`](https://github.com/The-Running-Dev/SubZeroDev.Adventures), and need my
> words before they are binding.

## Problem

G1 proved the transport is a projection: the same arc, the same seed, the same choices, played
through the hosted wire, serialize byte-identically to the in-process run, through one hop and
through two. It proved it against **state that does not survive a restart**. Every session G1 ever
held was in a `Map`, and the brief said so deliberately — sessions lost on restart, bounded by my
own use of them.

That makes the proof real and the product unusable. It also leaves the sharpest hosted problem
untouched. [`engine-hosting-contract.md`](../../docs/docs/engine-hosting-contract.md) §6.1 names it:
two `submitAction` calls against one session, arriving at two instances, both read the same
envelope, both apply an action, and one write silently overwrites the other. **It is a determinism
break that presents as a lost update** — no error, no failed validation, and a surviving state the
engine would never have produced. G1 recorded on 2026-08-08 that it built no compare-and-swap
because the failure "arrives with a second instance, which G1 does not have."

G2 gives it a second instance and then refuses to lose the update.

## Who it is for

Me, as the operator of the first hosted engine deployment that survives a restart — and the engine
itself, which records that its session-layer composition root has two real call sites and **zero
real implementations** of the abstraction it specifies, to be revisited *"when a second
`SessionStore` implementation is actually needed."* This is that implementation, and answering that
question is a deliverable rather than a side effect.

No player, creator, or third-party developer is served by G2 directly. Durable state is what an
account surface is eventually built on, but the account surface is G3's and nothing here anticipates
it.

## Scope

1. **Real `SessionPersistence` and `ProfileStore` implementations**, against a durable store,
   filling the ports the engine already specifies.
2. **Compare-and-swap, so a lost update is not reachable** — and a caller who lost the race can tell
   that is what happened.
3. **One deliverable into the engine**: a conflict outcome distinguishable from a storage outage.
4. **Session lifecycle** — bounds and expiry for state that no longer clears itself on restart.
5. **A second instance**, because the failure mode §6.1 describes has no other shape.

**The stores live in the Node workload, end to end.** They are engine ports and the engine is Node,
so the implementations are Node: the workload's own database client, its own migrations, inside
`workloads/game-service/`. G2 is Track B only, and Platform's `Persistence` package gains no
consumer from it. **The named cost:** this repository ends up with two persistence stories that
share no code, and [ADR-002](../../docs/docs/adr/ADR-002-implementation-technology.md)'s EF Core
baseline governs neither of them on this side.

**Noted for later, not decided now:** whether the migrations and the tenant column should follow
Platform's conventions rather than the workload's own, keeping one schema story across the
repository while conceding the write path is Node's. That is a live option and this brief does not
take it.

**`SubZeroDev.Adventures` is the reference implementation, and not a source this effort copies
from.** The 2026-08-09 decision that established this was written before G2 started and holds:
[`server/src/persistence.ts`](https://github.com/The-Running-Dev/SubZeroDev.Adventures/blob/main/server/src/persistence.ts)
and
[`server/src/profile-store.ts`](https://github.com/The-Running-Dev/SubZeroDev.Adventures/blob/main/server/src/profile-store.ts)
fill both ports in about 460 lines of Node against plain `pg`, which is proof the shape works. Its
schema is **not** reusable — it carries no tenant identifier, and
[`engine-hosting-contract.md`](../../docs/docs/engine-hosting-contract.md) §7 requires one from the
first schema, defaulted to a single implicit tenant, precisely because adding isolation after data
exists is a correctness migration on every table at once.

## Non-goals

The binding list. Everything here is out of scope for every agent until this file changes.

- **Principals.** No authentication, no ownership checks, no authorization decorator, no account
  surface. All G3. Durable state is what ownership eventually attaches to; attaching it now is G3
  arriving early because persistence made it look convenient.
- **Tenancy behaviour.** The tenant identifier is carried in the schema from the first migration,
  defaulted to a single implicit tenant, because §7 requires it and retrofitting is the expensive
  direction. **No request resolves or carries a tenant, and no behaviour varies by tenant.** The
  store supplies the single implicit tenant as a constant in every key and statement. Shipping this
  fixed schema shape is not shipping tenancy.
- **Billing, metering, catalogue, publishing.** G4 and later.
- **A raw-state endpoint, under any name.** Not staged — permanent, inherited unchanged from G1.
  Responses carry a projected `Scene`, never the envelope. Durable storage makes this *more*
  tempting, not less: a stored blob is right there, and a debugging endpoint that returns it would
  put hidden variables, visit counts and the seed on the far side of a boundary the engine built
  structurally. The store persists the canonical serialization to storage the player cannot read,
  and never returns it through the API.
- **An eleventh game operation invented here.** A hosting need the store does not meet is a new
  store operation *in the engine* plus a coverage-checklist row, never transport-side logic. The
  account operations a hosted service will eventually need (`list_saves`, `delete_account`) are the
  account surface — G3's, and never merged with the game surface. The per-player resume query
  Adventures fills with its own `listSavesForPlayer` is exactly this shape, and it stays out.
- **The edge becoming a Platform package.** [ADR-007](../../docs/docs/adr/ADR-007-second-hosted-workload.md)
  admits SkyNet HR as a second hosted workload and justifies generalising the edge on that evidence.
  Rule 5 of that ADR says it schedules nothing and the work is a new effort with its own brief.
  **This is not that brief.** G2 touches `workloads/game-edge/` only where durable state forces it
  to, and grows no streaming, no WebSocket path, and no package boundary.
- **Any change to engine behaviour beyond the named deliverable.** The conflict outcome in *Scope*
  is agreed. A second behaviour change made to ease persistence is transport-side logic wearing a
  different hat.
- **Copying Adventures' schema, routes, or replay endpoints.** Its routes are hand-written REST where
  this workload derives uniform `POST /v1/<operation>` from the row table; its replay endpoints
  return stored and replayed blobs in a failure body, which is the raw-state surface declared
  permanently out of scope above.
- **A human-facing interface.** No front end, no playground, no operator console. G2's audience is
  a test suite, a trace, and a database.
- **Reachability beyond trusted-local.** No public exposure, no transport security, no cross-origin
  access. A second instance is not a step toward public reachability and must not be designed as
  one.
- **Performance work.** No latency or throughput target, no load test, no benchmark, no connection-pool
  tuning presented as a result. G2 answers whether the write is correct under contention, not how
  many of them fit in a second.
- **Serving more than one wire version at once.** Unchanged from G1.
- **Deployment machinery beyond what two instances require.** A way to run two instances against one
  store is in scope because the criteria below cannot be met without it. Container images, release
  publishing of the workload, process supervision, and orchestration are not.
- **Observability beyond what a lost update needs.** No metrics, no dashboards, no log aggregation,
  no alerting. G1's single cross-language trace stays green; nothing here builds a second one.

## Definition of done

**The store:**

- **The byte-identity proof passes against the durable store.** A committed replay fixture
  round-trips through it byte-identically to the in-memory one. This is G1's invariant with the
  stores swapped, and it is the reason G1 was ordered first.
- **G1's in-memory replay is still green.** The durable store is an addition, not a replacement, and
  two proofs passing is not evidence that the first still does.
- **Every store operation is exercised against the durable implementation**, and the engine's API
  coverage checklist reflects it — delivered as a PR against `SubZeroDev.GameEngine`, opened by the
  slice that produced the evidence, the way G1 delivered its column.
- **The schema carries a tenant identifier from the first migration**, defaulted to a single
  implicit tenant, and a test asserts it is present and non-null on every row a write produces.
- **Host metadata stays out of game state.** Timestamps, owner ids and tenant ids live on the
  store's own record, never in the blob. A test asserts the blob the store writes is exactly the
  engine's canonical serialization and carries nothing else — this is what keeps determinism intact
  while still allowing resume elsewhere.
- **A failed profile write does not roll back a completed game action**, and a missing or corrupt
  profile degrades to "no achievements" rather than a broken game. Both asserted, not argued.

**Compare-and-swap:**

- **Two concurrent actions against one session produce one success and one explicit rejection —
  never a silent overwrite.** This is the implementation plan's own criterion and it is met in full,
  including the word *explicit*.
- **Proven twice, asserted separately.** Concurrent requests to a **single instance**, and the same
  contention across **two genuine instances** sharing one store. Neither alone answers §6.1: the
  single-instance case may not even be reachable, because the engine's session store already queues
  same-session commands behind their predecessor, and the multi-instance case is the shape the
  contract actually describes. A gate that cannot go red proves nothing, and running only the first
  risks exactly that.
- **The rejection is distinguishable from a storage outage at the caller.** Today it is not. The
  engine's `writeSession` catches every error from `persistence.sessions.put` and rethrows
  `SessionStoreError("session", "storage_failure")`, discarding the cause — so a lost race and a
  dead database arrive at the client as the same `503`. **G2's engine deliverable is a conflict
  outcome that survives that boundary**, delivered as a PR against `SubZeroDev.GameEngine`. Without
  it the criterion above cannot be met by any amount of work on this side.
- **The gate has failed at least once.** A deliberately perturbed run — a stale version deliberately
  written — goes red. Inherited from G1's rule, and it applies with more force here, because an
  optimistic lock that never rejects is indistinguishable from no lock at all.
- **Merging is never attempted.** Two actions applied to the same base state are two different
  games. A test asserts the loser is rejected rather than reconciled.

**Session lifecycle:**

- **Durable sessions are bounded, and the bound is asserted.** In-memory state was self-limiting
  because a restart cleared it; durable state is not, and G1's deferral of eviction to "G2 sizing a
  store it does not have yet" expires the moment the store exists.
- **What expires, on what clock, and what a caller sees when it has** are decided and tested. A
  session that has been evicted is not the same answer as a session that never existed, and the
  wire says which.
- **Saves are in scope too, on their own clock.** *(Amended 2026-08-20. The clauses above named
  sessions only, and `/design` read that as the whole of the scope while specifying an absolute TTL,
  a sweep and a distinct wire code for saves as well — recorded as design Open question 10 and
  carried unresolved through `/contract` and `/slices`.)* The same reasoning applies and reaches the
  same place: a save that no longer clears itself on restart is unbounded storage, and a save that
  is gone is not the same answer as one that never existed. **Sessions and saves do not share a
  number.** A session is resumable working state on an idle clock; a save is immutable and is the
  artifact a player would notice losing, so it gets an absolute clock from insert and a much longer
  one. G3 owns whatever account surface later wants to override either.

**Both:**

- **The evidence runs in CI from a fresh clone**, including the two-instance case and the store it
  shares. A concurrency proof that only runs on my machine is the anecdote G1 already refused once.
- **The repository tells a reader how to provision the store, run two instances, replay the proof,
  and roll the schema forward.** G3 begins by rerunning G2's proof; it should not begin by
  reconstructing it.
- **`build/Test-WorkloadIsolation.ps1` still passes.** A durable store is exactly the kind of
  capability that invites a Platform package to reach into `workloads/`, and the gate that fails
  that build is unchanged.

## Environment

The Node service consumes `@the-running-dev/game-engine` from GitHub Packages over authenticated
restore, current LTS Node, unchanged from G1. It gains a durable store alongside it.

**The store is shared by two processes**, which is a consequence of the compare-and-swap criterion
rather than a separate decision: an embedded in-process store cannot exhibit the failure §6.1
describes, so it cannot prove the fix. The same self-host constraint set as D3 and G1 still applies —
local developer execution, homelab, single-server — so the store must be self-hostable and reachable
without a vendor's SaaS tenant, per `AGENTS.md`'s *depend on the protocol, not the vendor*. **Steady
state stays fully offline.**

Code lives in this repository under `workloads/game-service/`, unchanged from G1 and for the reasons
recorded there.

Scale is small and deliberately so: two instances, because one cannot demonstrate the problem and
three demonstrate nothing further.

## Lifespan

**Built to last, unlike most of G1.** G1's in-memory composition was declared disposable and this
effort disposes of it. What G2 builds is not: the schema, the tenant column, the optimistic-locking
discipline, and the engine's conflict outcome all survive G3 and G4. G3 wraps these stores with an
authorization decorator that must produce byte-identical `serialize()` output; G4 meters what they
hold. A schema shortcut taken here is paid for in a correctness migration later, which is the whole
reason §7 asks for the tenant column before anything needs it.

The exception is the two-instance harness, which exists to make a failure reachable and may be
replaced without ceremony once it has done so.

---

## Decisions taken here that override a recommendation elsewhere

1. **The §6.1 contradiction is logged, not resolved in this brief.**
   [`engine-hosting-contract.md`](../../docs/docs/engine-hosting-contract.md) §6.1 resolves concurrency
   with compare-and-swap on the sequence number, stating that *"the engine's save handle already
   exposes `savedAtSeq` — so the version is present and needs no new concept."* The evidence
   disagrees. The contended row is the **session**, whose version is `attemptCounter` — the engine
   increments it by exactly 1 on every write to an existing session — and `savedAtSeq` lives on the
   **save** record, which Adventures writes with an unconditional upsert and no lock at all. Taken
   literally, §6.1 would version the wrong table and leave the contended one unguarded.

   **The recommendation was to correct §6.1 in this effort**, on the grounds that a known-wrong
   resolution sitting in a document G2, G3 and G4 all read is how it gets implemented wrongly later.
   **Declined, and recorded here rather than dropped**, per this repository's rule on declined
   findings: the contradiction is named in the brief and `/design` decides which side is wrong,
   including whether saves need a lock of their own. The retained risk is that the document stays
   wrong for the duration of the design stage.

2. **The store is Node's, end to end, and Platform's Persistence package gains nothing.** The
   implementation plan pairs G2 with "provisioned persistence" without saying whose. Settled: the
   ports are the engine's and the engine is Node, so the implementations are Node. The alternative —
   the workload calling a .NET persistence service across a wire — would put a network hop inside
   the write path the compare-and-swap has to survive, to reach a package a 460-line Node
   implementation demonstrably does not need.

3. **Session lifecycle is admitted rather than deferred again.** G1's non-goal deferred eviction,
   expiry and quotas to "G2 sizing a store it does not have yet." That reasoning expires here.
   Admitting it means G2 carries criteria the implementation plan's G2 entry does not currently
   list, and this brief is where that scope binds.
