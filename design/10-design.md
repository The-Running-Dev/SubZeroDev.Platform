# Design — the minimal package set (D3)

**Document status:** Design. Derived from [`00-brief.md`](00-brief.md); if the brief changes, this is
re-derived, not patched.

Package boundaries and done-criteria are owned by
[`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 and are not restated
here. This document decides what that document leaves open: what is persisted and in what shape,
which package owns each concern where two of them overlap, and what happens when each of it fails.

Three facts from the brief shape almost everything below, so they are stated once here rather than
rediscovered in each section: **two persistence providers** (PostgreSQL and SQLite, both production
paths), **two processes** per installation (a web host, and a worker host owning all background
work), and **no outbound network** at startup or in steady state.

**Licensing is named in the brief and is not designed here, deliberately.**
[`implementation-plan.md`](../docs/docs/implementation-plan.md) §5 places Licensing in D5, alongside
Identity, Billing and Audit. The brief's mention of it is a **constraint on what D3 may depend on**,
not a capability D3 builds: because the licence verifies locally on a per-installation basis, no
dependency may require the network. That constraint is discharged rather than deferred — nothing in
this design contacts anything at startup or in steady state, and telemetry export, the only outbound
path that exists at all, is opt-in and treats absence as normal rather than as failure. No package
below owns licence verification, and none should acquire it during D3.

---

## Data model

Most of Platform is in-memory. Persistence owns three durable Platform-authored tables and the
per-module migration histories, plus two sets of columns it contributes to tables products own.

### Logical types, and how each provider stores them

Two providers means no PostgreSQL-specific type may appear in the model. The model is logical; the
storage is per provider.

| Logical type | PostgreSQL | SQLite |
|---|---|---|
| Identifier | native uuid | 16-byte blob |
| Sequence | 64-bit identity | 64-bit rowid alias |
| Instant | timestamp with time zone | ISO-8601 UTC text |
| Tenant | native uuid | 16-byte blob |
| Payload | native json | text |
| Text | text | text |

**Instants are stored UTC and compared as instants, never as local time.** SQLite has no date type,
so an ISO-8601 form that sorts lexicographically in the same order it sorts chronologically is the
requirement, not a convenience.

**That requirement is not met by "ISO-8601" alone, so the form is pinned:** UTC only, `Z`-suffixed,
**fixed width, with exactly seven fractional digits, zero-padded and never trimmed.** No offset
forms, no variable precision.

The failure is silent and specific. Trim trailing zeros and `…00.1Z` sorts *after* `…00.15Z`, because
the comparison reaches `Z` against `5` and `Z` is the higher character — while 0.1 is the earlier
instant. Eligibility (`next attempt at <= now`) and claim expiry are both evaluated in SQL over this
text, so a variable-width writer makes messages intermittently ineligible when they are due, and
eligible when they are not. **The provider contract tests must include a sub-second boundary case**;
nothing else in the definition of done would catch it.

### Persisted — outbox message

The entity Platform both defines and stores.

| Field | Logical type | Derived from | Notes |
|---|---|---|---|
| Id | identifier | version-7 UUID at enqueue, its timestamp from the clock abstraction | **Identity.** Time-ordered; minted before the insert |
| Sequence | sequence | provider | **Claim order only.** Values may be reused after prune on SQLite — see below |
| Occurred at | instant | the clock abstraction at enqueue | Never the database clock — a fake clock must control it |
| Type | text | the event contract's stable name | Not a runtime type name; renaming a class must not orphan rows |
| Payload | payload | the event instance | |
| Tenant | tenant | the ambient tenant at enqueue — the implicit tenant in D3 | Same column and rule as every product table |
| Trace context | text | the ambient trace at enqueue | The **full traceparent including trace flags**, not the trace-id alone — the sampling decision travels with it |
| Trace state | text, null | the ambient trace at enqueue | The W3C tracestate when present, so vendor and sampler state crosses the boundary with the traceparent |
| Attempts | integer | dispatch | |
| Next attempt at | instant, null | dispatch | Null means eligible now |
| First deferred at | instant, null | dispatch | Stamped on first deferral — unresolvable type or undeserializable payload; the deferral age measures from it |
| Claimed by | text, null | dispatch | Dispatcher instance identity |
| Claimed at | instant, null | dispatch | **Present so a claim can expire** |
| Processed at | instant, null | dispatch | Null means undispatched |
| Poisoned at | instant, null | dispatch | Set when attempts are exhausted, kept on discard. **A poisoned row is never eligible, and poisoned-at set means the poison window governs pruning** |
| Last error | text, null | dispatch | Most recent failure only, not a history |

**Ownership:** Persistence. **Lifecycle:** inserted inside the caller's transaction alongside the
domain write; claimed by a dispatcher; dispatched; marked processed *or* poisoned; pruned.

**Enqueue requires an ambient transaction and throws a named error without one.** The atomicity
above is structural rather than conventional — no code path exists that silently commits an outbox
row apart from the domain write it belongs to. A call site that genuinely has only the enqueue opens
a transaction around it, one explicit line that states the intent. The provider contract tests
assert the throw.

**Identity is the id — a version-7 UUID minted app-side at enqueue, its timestamp drawn from the
clock abstraction** so a fake clock controls it, the same rule occurred-at already follows. The
runtime supplies the generator, so no Platform code is written for it, per ADR-004. The id exists
before the insert — loggable and returnable at enqueue — survives a database restore, and is the
dedupe key at-least-once delivery offers handlers. **The provider contract tests assert id
uniqueness across a drain, prune-to-empty, insert cycle**, the exact sequence that exposed the
defect below.

**The sequence is claim order and nothing else.** An earlier draft made it the identity and called
it monotonic per database, and both claims were false on SQLite: a plain rowid alias allocates
max(current)+1, so a fully drained and pruned outbox restarts numbering at 1, reusing values earlier
messages carried. Demoted, the reuse is harmless — a reused value can only collide with a dead
row's, never a live one, so claim order among live rows stays consistent, and delivery order is not
guaranteed anyway. Nothing downstream may treat the sequence as durable; anything needing a cursor
across time uses the time-ordered id. The reversal is recorded in *Alternatives*.

**Payload shapes change additively or not at all.** A change to an event's payload must be
tolerant-reader safe — new optional fields, never renames, removals or meaning changes. A breaking
change of shape is a new event under a new stable Type name, with the old handler retained until the
old rows drain. The rule exists because a backlog is this design's normal shape: the worker-down
failure mode makes days-old rows routine, so an upgrade that changes what a Type's payload means is
dispatching against history — and without the rule it mass-poisons everything enqueued before the
deploy.

**Claimed-at exists because a dispatcher can die holding a claim.** Without an expiry those rows are
stranded forever, undispatched and invisible — the exact failure the outbox exists to prevent,
reintroduced by the mechanism meant to prevent it.

**Reclaim is inline and has exactly one mechanism.** A claim older than the claim window is simply
eligible again, so any dispatcher's ordinary claim query picks it up. There is no separate reclaim
pass. The conditional-update claim already makes concurrent reclaim safe, so guarding it would add
latency — an expired claim would wait for a scheduled sweep rather than the next poll — to protect
something that needs no protection.

**The claim window is five minutes, with a default rather than a required setting.** Unlike
retention, a wrong value here degrades rather than corrupts — too short duplicates delivery to
idempotent handlers, too long stalls a dead dispatcher's rows — so a sensible default is better than
refusing to start.

Five minutes is sized by the only pressure left on it: the slowest handler that may legitimately
still be running. Graceful shutdown releases unstarted claims, so routine deploys no longer argue for
a short window, and the two pressures that used to point in opposite directions have become one. **A
handler that can exceed five minutes is a design signal, not a tuning problem** — it belongs behind
its own durable state rather than inside a dispatch attempt.

**Two retention windows, both required settings with no default**, validated as present at startup
and failing the host when absent. The brief chose configurable and declined to set a default, and
"configurable with no default" silently becomes "never prune" otherwise.

- **Processed rows** are pruned past their window.
- **Poisoned rows** are pruned past a separate, longer window, and pruning one logs at warning.

An earlier draft of this document claimed retention closed the only unbounded-growth surface Platform
owns. That was false: a poisoned row is never processed, so a recurring poison source — one handler
bug, one malformed payload shape — accumulated rows forever under the single-window rule. The second
window is what makes the original claim true.

### Persisted — background work lease

A consequence of the worker owning *all* background work while two processes may overlap during a
restart.

| Field | Logical type | Notes |
|---|---|---|
| Name | text | **Identity.** The work item's stable name |
| Holder | text | Process instance identity |
| Acquired at | instant | |
| Expires at | instant | Renewed by heartbeat while the work runs. **Five minutes**, the same window as a claim |

**Ownership:** Persistence. **Lifecycle:** acquired before scheduled work runs, renewed while it
runs, released or expired after. **Not needed by outbox dispatch** — that is protected by the row
claim instead, which is finer-grained and lets two dispatchers work different messages concurrently.

**The lease reduces duplicate runs; it does not prevent them, and the wording matters more than the
mechanism.** A holder can stall past its expiry — a garbage-collection pause, or a heartbeat write
losing the SQLite write lock to the web process — while its work continues. A second worker then
acquires the expired lease and runs concurrently, and nothing fences the first: its in-flight writes
still land. This is the same hole the claim window has, and this document already concedes it there.

So, stated as the guarantee it actually is:

- **Leased work must be idempotent.** The lease is an optimisation against wasted duplicate work, not
  a mutual-exclusion primitive.
- **A holder that fails to renew must abort**, rather than continuing on the assumption it still
  holds.
- **Non-idempotent work does not belong under a lease.** There is nothing here that would make it
  safe.

Pruning satisfies this trivially, which is exactly why the defect was invisible: the only current
consumer cannot be harmed by running twice. The earlier phrasing — work that "must not run twice at
all" — invited a future consumer to rely on a promise this cannot keep, and by this design's own
argument about ordering, a guarantee that fails precisely when it is relied upon is worse than none.

### Persisted — host registration

Each running host records itself in the store it is actually using. That last clause is the entire
point.

| Field | Logical type | Notes |
|---|---|---|
| Role | text | **Identity, with instance.** Web or worker |
| Instance | text | Process instance identity |
| Started at | instant | |
| Heartbeat at | instant | Renewed periodically while the host runs |
| Settings fingerprint | text | Hash of every fingerprinted setting — the membership rule and full list live in *Settings inventory* |

**Ownership:** Persistence. **Lifecycle:** upserted by the heartbeat from startup on, expired by
absence.
**Never read by the host that wrote it** — its only consumer is the *other* role's readiness check.

**This exists because nothing else can detect two hosts pointed at different databases.** A relative
database path resolved against two different working directories is an ordinary homelab
misconfiguration, and its symptom is that every mechanism in this design silently no-ops: the web
process commits domain writes and outbox rows to one file, the worker polls another forever, both
report ready, and both are individually configured correctly. Neither can see the other to compare
notes — which is exactly what makes the absence detectable. **A host that finds no live peer in its
own store has found the split**, from whichever side notices first.

A live peer whose fingerprint disagrees is the milder case — same store, different settings, which
happens when only one host is upgraded. It is reported rather than fatal.

**Liveness has a stated threshold, and dead rows do not linger.** A row is live while its heartbeat
is within three heartbeat intervals, so one beat lost to SQLite contention cannot flap readiness —
and heartbeat writes take the same bounded busy-wait as every other write. Peer and fingerprint
checks consider live rows only; a dead instance's stale settings cannot contradict a live one's. A
host deletes its own row on graceful shutdown, and the prune pass removes rows dead past the
registration retention window — the unbounded-growth class the second retention window closed is not
reintroduced by the table that watches for everything else.

### Persisted — columns Platform contributes to tables products own

Platform owns the shape and the population. The product owns the table.

**Tenant.** Logical tenant type, **not null**, on every product table from the first migration,
defaulted to a well-known all-zero value standing for the single implicit tenant. Decided in
*Alternatives*; the least reversible decision in this document.

**No query filter ships in D3**, and this is the line the brief's non-goal draws. The column is data
and is ruinous to add after products have rows; a filter is code and is cheap to add whenever
tenancy becomes a feature. Shipping one now would also prove nothing — a filter that always matches
the implicit tenant is indistinguishable at runtime from no filter at all, so it would commit the
public surface to isolation semantics without ever exercising them.

**Audit.** Created at, created by, modified at, modified by — times from the clock abstraction,
actors from the ambient principal, both null-tolerant because identity is D5 and there is frequently
no principal. **Soft delete** applies only where a product opts in, never globally: a soft delete
nobody asked for silently changes the meaning of every query against that table.

### Persisted — migration history, one per module

Each module owns its own history so two modules' migrations apply in either order, per §2. The
mechanism is a **history per module rather than one shared table**, because a shared one serialises
the ordering it is supposed to permit.

**Either-order application is only true if the schemas are disjoint, so that is a stated rule: a
module's migrations may not reference another module's tables.** No cross-module foreign keys.
Modules relate by holding an identifier and resolving it in application code, and they integrate
through events — which is a large part of why the outbox exists.

**Nothing in the mechanism enforces this.** Separate histories permit a cross-module foreign key just
as readily as a shared one would; they merely make its application order nondeterministic, so the
first such reference works or fails depending on which history ran first. That is the exact class of
order-dependence the per-module design claims to remove, and without the rule written down there is
nothing to review against. The provider contract tests can assert it directly — no foreign key
crosses a module boundary — which turns the rule into a check rather than an intention.

### In-memory — configuration root

Service name and version (**derived** from the entry assembly when unset), environment (**derived**
from the host and read-only — a service must not declare itself production in a file that shipped
from a developer's machine), **host role** (web or worker), and the per-package settings groups. One
instance per host, bound once, validated at startup, immutable after. Never persisted.

### In-memory — module descriptor

Name, declared dependencies, registration delegate. **Order derived by topological sort with ties
broken by name**, so the order is reproducible across runs rather than dependent on discovery order.
Frozen when the host is built. Missing or cyclic dependency is a named startup failure, per Core's
done-criterion.

### In-memory — ambient operation context

Correlation identity (always present), tenant (always present, and in D3 **always the implicit
tenant** — nothing resolves it from host, header or claim, per the brief's non-goal), principal
(nullable). Scoped to one operation. **Correlation identity is the originating trace-id, not a
second value beside it** — two ids mean two propagation paths and two chances to disagree, and the
one that disagrees is the one quoted in a bug report. On the request path it and the current
trace-id are one value. They part company in exactly one place — outbox dispatch starts a new linked
trace while the correlation stays the origin's, see *Control flow* — so a single value stays
greppable end to end even where the trace changes.

### In-memory — health registration and report

A registration is a name, a kind (liveness or readiness), a check, a timeout, and a criticality
(required or optional). Collected at startup, **frozen when the host is built**; a duplicate name is
a startup failure, not a silent overwrite. A report is derived per probe and never cached.

**Readiness is the always-on operational surface, and that is a deliberate load to put on it.**
Telemetry export is opt-in, droppable, and absent by default on the offline homelab the brief
centres — so a metric is not a mitigation there, it is a convention. Every operational condition this
design calls silent therefore reports **degraded** on readiness, with the detail in the response body:

| Condition | Why it would otherwise be invisible |
|---|---|
| No live peer host in this store | The split-brain above. Nothing else can see it |
| Peer present, settings fingerprint disagrees | Half-upgraded installation |
| Oldest undispatched row older than a threshold | Worker down, or dispatch wedged |
| Any poisoned rows present | The handler is broken and nobody is watching |
| Schema has pending migrations | See *Failure modes* |

**Wire mapping, because a three-state model over a two-state protocol is otherwise undefined:**
healthy and **degraded both return success**; only unhealthy fails. Degraded means *take traffic,
something needs attention*. Mapping degraded to failure would drain a host whose optional provider is
down — the precise outcome the criticality flag exists to prevent.

**Peer absence is scoped so the signal keeps meaning something.** In the development environment —
already host-derived and read-only — a missing peer is informational in the response body, never
degraded: the default developer gesture is one process, and a surface that is degraded on every
inner-loop run trains everyone to ignore the one signal this design elected as always-on. Everywhere
else, peer absence degrades only after a startup grace period, so a routine restart of the other
role does not flap. The sample runs both roles, so CI exercises the real two-process shape rather
than the carve-out.

### In-memory — error envelope, telemetry signals

The error envelope is wholly derived from the failure and the ambient context, serialized, then
discarded. Telemetry signals are buffered and **droppable by design** — see *Concurrency*.

---

## Module boundaries

Six packages, as the brief decides. Two ownership overlaps in §2 block the graph and are resolved
here.

**Overlap 1 — correlation ids sit under both Hosting and Observability.** Resolved in three parts,
because two are not enough: **the ambient correlation accessor is a contract in Abstractions**, next
to current-principal and current-tenant, which are the other two members of the same operation
context; **Observability owns the identity's derivation, its propagation across process boundaries,
and sampling**, because those *are* trace context; **Hosting owns establishing it on an inbound
request**.

The accessor had to move because Persistence stamps the trace context onto every outbox row and
reconstructs it at dispatch, while depending on Abstractions and Core only. Without the contract in
Abstractions, that column is unimplementable without either a new Persistence→Observability edge or
a silent relocation by the first implementer.

**Overlap 3 — background work is owned by nobody, and Hosting has to start it.** The worker role must
run the outbox dispatcher, which belongs to Persistence, while not depending on Persistence.
Resolved the same way: **the background-work contract lives in Abstractions**; **Core owns
registering and ordering** the registrations; **Hosting's worker role runs everything registered
against the contract**, without knowing what any of it is; **Persistence registers the dispatcher and
the prune pass as background work** and supplies the lease that guards them.

**None of these adds an edge.** That is the test each resolution had to pass — a boundary problem
solved by adding a dependency is a boundary problem renamed.

**Overlap 2 — health.** §2 puts the endpoints under Hosting, Observability owns provider health, and
Persistence must contribute a database check. Resolved: **the check contract lives in Abstractions;
the endpoints live in Hosting.** Any package contributes a check depending on Abstractions alone.
Without this, Persistence depends on Hosting — a storage package coupled to the transport package, a
dependency pointing the wrong way that every future check-contributing package would copy.

| Package | Owns | Depends on | Exposes |
|---|---|---|---|
| **Abstractions** | Result and error types, clock, current principal, current tenant, **current correlation**, module contract, event and **event-handler** contracts, **health check contract**, **background-work contract** | Nothing but the BCL | Interfaces and value types only |
| **Core** | Default implementations, module registration, ordering, startup validation, typed configuration binding, **background-work registration and ordering** | Abstractions | Registration surface, module graph |
| **Observability** | Telemetry wiring, correlation identity and propagation, instrumentation, sampling policy | Abstractions | Configuration surface, ambient correlation |
| **Persistence** | Transaction boundary, per-module migrations, provider abstraction, outbox and dispatcher, **handler resolution and per-message context reconstruction**, leases, **host registration**, audit fields, soft delete, tenant column | Abstractions, Core | Transaction abstraction, outbox enqueue, lease acquisition, **readiness checks for peer presence, backlog age, poison count and pending migrations**, **dispatcher and prune registered as background work** |
| **Hosting** | Host bootstrap for **both host roles**, DI wiring, middleware order, graceful shutdown, health and readiness **endpoints**, request/principal/correlation/tenant context establishment, **running registered background work in the worker role** | Abstractions, Core, Observability | The standard registration call, in web and worker forms |
| **Testing** | Test host for both roles, fake clock, fake principal and tenant, capture, deterministic background work, **provider contract tests** | All five | Test host builder |

**Two host roles, one package.** The worker is not a second Hosting package — it is the same
bootstrap with the product HTTP surface omitted and background work enabled: no product endpoints,
no request pipeline, a minimal listener retained for its probes. Splitting it would duplicate startup
validation, module ordering and health registration, which is where the behaviour that must not
diverge lives.

**The provider abstraction is real, not notional.** Two production providers is what forces it; §2's
contract tests are what verify it. A single-provider design with an abstraction "for later" would
have the abstraction shaped entirely by one provider's semantics.

### Dependency direction

```text
Abstractions ──► (BCL only)
     ▲   ▲   ▲
     │   │   └────── Observability ──►┐
     │   └────────── Core ──►┬────────┴──► Hosting
     └──────────────────────┴──► Persistence

Testing ──► all five          (test-only; never a dependency of anything shipped)
sample ──► Hosting, Persistence, …
```

**Acyclic.** Abstractions has no outbound edge; Core depends only on it; Observability only on it;
Persistence on Abstractions and Core; Hosting on those plus Observability; Testing is a sink nothing
references. No package appears on both sides of any edge.

**Platform never depends on a product**, per [`platform-identity.md`](../docs/docs/platform-identity.md)
§1 — a reference from Platform to the Automator or the Game Engine is a build failure, not a review
comment. The sample sits on the consumer side of the arrow.

---

## Control flow

### 1. Host startup — triggered by process start, in either role

Modules collect → **topological sort; missing or cyclic dependency aborts with a named error** →
options bind and validate, **including both required retention settings** → provider selected →
health registrations freeze → module graph freezes → **the host starts its registration heartbeat**
→ host runs in its role.

**Registration is the heartbeat; there is no separate write.** The heartbeat loop upserts the host's
row — role, instance, settings fingerprint — so the first successful beat is the registration and
every renewal is the same statement. One mechanism instead of two, and it is what makes a fresh
database non-fatal: against a store whose schema does not exist yet the beat fails, the loop retries
at its ordinary interval, and the row appears the moment migrations run. No bespoke startup retry,
and no ordering dependency between startup and the schema.

**Registration happens in the store, not in memory**, which is what makes the peer check work: a host
writing to the wrong database registers itself there too, so its absence from the right one is
detectable from the other side.

The two roles diverge only at the end: the web role maps endpoints and serves; the worker role
**starts everything registered against the background-work contract** — the outbox dispatcher and
the prune pass among them — and serves probes only. Hosting does not know what any registration is,
which is what lets it start Persistence's dispatcher without depending on Persistence.

**The worker serves its probes over HTTP on its own port — a defaulted setting in *Settings
inventory* — through the same endpoint code as the web role.** It exists for nothing else. The alternative — inferring worker health from the store alone —
would surface the worker's *absence* but not its *wedging*: a dispatcher heartbeating while failing
to make progress looks identical to a healthy one from the other side. Reusing the endpoint also
keeps the structured detail body intact, which a command-based probe would have to re-serialise.

**Migrations are registered here, not applied here.** Application is a separate explicit operation,
automatic only in the development environment. A host that migrates on every start applies schema
changes at the least controlled moment available, and with two processes starting concurrently it
races itself.

### 2. Inbound request — triggered by an HTTP request, web role only

Correlation adopted from inbound trace context or minted → ambient context populated, tenant set to
the implicit tenant → span opened → product handler runs → **the transaction commits
the domain write and any outbox rows atomically** → response.

On unhandled failure the transaction rolls back, taking the outbox rows with it; the failure maps to
an error envelope carrying the correlation identity; the span records the error.

### 3. Outbox dispatch — triggered by a timer in the worker role

Claim a batch of eligible rows — **expired claims are eligible, so reclaim needs no separate pass** →
dispatch each in-process → mark processed, or record the failure, increment attempts and set the next
attempt → separately, under a lease, prune processed and poisoned rows past their windows.

**Prune runs in bounded batches, exactly as dispatch does.** It is a lease-guarded pass rather than a
per-message one, which made it easy to overlook, but on SQLite it competes for the same single write
lock — and its worst case is the largest: a worker returning after days down, or a retention window
shortened, leaves an arbitrarily large backlog to delete in one statement while the web process needs
that lock for every request.

**The trigger is a timer, not a signal from the writer.** With the writer in the web process and the
dispatcher in the worker, an in-process signal cannot reach it, and a cross-process one would need a
transport — which is unchosen and would need the network the brief forbids depending on. The timer is
also the only mechanism that survives the process dying between commit and dispatch, which is the
case this exists for.

**Each message is dispatched in its own scope, with its context rebuilt from the row — not inherited
from the worker.** The dispatcher opens a fresh dependency scope per message, resolves handlers for
the row's Type through the event-handler contract, and populates the ambient operation context from
the row itself: correlation from the stored traceparent's trace-id — the origin's, not the new
linked trace's — tenant from the row's tenant column, principal null — the worker has no principal
and must not invent one.

**Dispatch starts a new trace and links it to the stored one; it does not continue it.** An earlier
draft said the opposite, and the design's own worker-down scenario is what breaks it: a backlog can
drain days after the originating request ended. Continuing produces a trace of unbounded duration
that no backend joins usefully, and orphan spans whenever the origin was sampled out. A link is the
standard shape for a consumer decoupled in time from its producer, and it degrades gracefully — the
correlation survives even when the origin is long gone. The stored trace flags travel with it, so the
new trace can honour the origin's sampling decision rather than re-deciding blind, and the stored
trace state carries any vendor sampler detail beside it.

**The trace changes here; the correlation does not.** The ambient correlation is rebuilt as the
origin's trace-id, so handler logs, error envelopes and follow-up rows carry the value the
originating request logged — the one quoted in the bug report. The link serves backends that can
follow links; the correlation serves the console-and-file installation that cannot. The two are the
same value everywhere except across this boundary, and this is the only place they are permitted to
differ.

This refines rather than contradicts [`observability.md`](../docs/docs/observability.md), whose
end-to-end trace commitment concerns **synchronous** propagation across process boundaries. The
outbox is asynchronous by construction.

**Rebuilding rather than inheriting is the whole point.** A handler that enqueues a follow-up event
is the ordinary case, and the new row is stamped from the ambient context. If that context were the
worker's default rather than the originating row's, every derived event would carry the implicit
tenant regardless of where it came from — invisible today, when the implicit tenant is the only
value, and a cross-tenant write the moment D5 makes tenants real, against data written years
earlier. That is precisely the cost class the tenant column was pulled into D3 to avoid.

---

## Failure modes

### Database — unreachable at startup

**Detected by** the readiness check, not a startup probe.
**System does:** starts, reports **not ready**, keeps liveness healthy.
**User sees:** a process running and refusing traffic.
**State left behind:** none.

**The distinction that matters:** *misconfiguration* — absent or unparseable connection settings, or
the missing retention setting — fails startup with a named error, because it will never resolve
itself. *Unavailability* with valid configuration starts and reports not-ready, because on a
self-hosted box a database thirty seconds behind the application should not need a human.

### Database — reachable, valid config, wrong schema

**The third branch, and the one the two-branch taxonomy above missed.** An operator upgrades the
binaries and restarts; migrations are registered but not applied.

**Detected by** a readiness check comparing applied migrations against those registered per module.
**System does:** starts, reports **degraded** with the pending migrations named, and serves.
**User sees:** a host that is up and honest about being behind.
**State left behind:** none.

**Why degraded rather than refusing to start:** the operator's next action is to run the migration,
and a host that will not start is harder to inspect than one that says what it needs. Refusing
traffic outright would also make every upgrade an outage even when the pending change is additive.

**A fresh database is the extreme case of this branch, not a fourth one.** On a first production run
nothing has been applied — including Platform's own tables, which registration and the persistence
checks themselves need. Every persistence readiness check therefore self-guards: pending migrations
reports degraded with the schema named absent, and peer presence, backlog age and poison count
report degraded citing the absent schema as their cause rather than throwing — a known condition
never becomes unhealthy-by-exception, and the probe body tells one story with one root cause. The
registration heartbeat simply retries until migrate mode has run. So the promise above holds from
the very first start: the host comes up, says exactly what it needs, and serves.

**The comparison is symmetric.** Applied migrations this host never registered mean the binaries are
behind the schema — the normal state of the not-yet-restarted process mid-upgrade, once migrate mode
has run. That reports degraded too, naming the surplus migrations, and the host keeps serving: the
additive-only payload rule and the preference for additive schema change make straddling survivable,
and the operator's next action — restart onto the new binaries — is the same one the degraded body
already implies.

**The migration itself is a one-shot host command**, distinct from the two long-running roles: the
same image started in a migrate mode applies pending migrations per module and exits with a status.
Automatic application happens only in the development environment. Naming this matters because the
design previously said migrations were "applied by an explicit operation" without saying anywhere
what that operation *was* — leaving a homelab operator with a documented requirement and no
documented way to meet it.

### Hosts disagree, or cannot see each other

**Detected by** the peer's absence from, or disagreement in, the host registration table.
**System does:** reports **degraded** on both sides, naming which peer is missing or which settings
differ.
**User sees:** requests succeeding while nothing propagates — but now with a readiness surface saying
so, rather than silence.
**State left behind:** on the split-database case, a permanently growing outbox in the web host's
store, and an idle worker polling an empty one.

**Partial failure is the normal shape here**, not the exception: a worker that has been down for ten
minutes and one pointed at the wrong file look identical for the first ten minutes. Both are
degraded, and the age-of-oldest-row detail is what separates them as time passes.

### Database — fails mid-transaction

**Detected by** the transaction boundary. **System does:** rolls back; the domain write and its
outbox rows disappear together, which is the entire point. **User sees:** an error envelope with a
correlation identity. **State left behind:** none. **Retry:** none by Platform — a generic retry on
the request path doubles load on a struggling database and turns a fast failure into a slow one.

### SQLite — writer contention between the two processes

**Specific to one provider, and a direct consequence of two processes.** SQLite permits one writer at
a time across the whole database. The web process writes domain rows and outbox rows; the worker
writes claims, marks and prunes. They contend.

**Detected by** the provider returning a busy condition.
**System does:** waits, bounded, then fails the operation normally. Dispatch batches are kept small
so the worker holds the write lock briefly.
**User sees:** nothing at the stated scale; under contention, latency.
**State left behind:** none — a failed write is a failed transaction.

**This is a supported production configuration, not a developer-only footnote.** The brief puts
SQLite in single-file homelab installations and puts two processes in every installation, so
web-plus-worker on SQLite follows from both. Two consequences that would be optional if it were a
footnote and are not:

- **Batch bounds on dispatch and prune are correctness-adjacent, not comfort settings.** They are what
  keeps the worker's hold on the single write lock short enough that the web process is not starved,
  so they are configurable and validated, not constants someone picked.
- **The busy-wait bound is part of the contract**, because it decides whether contention shows up as
  latency or as a failed request.

It remains the cost of SQLite as a production path rather than a test double, acceptable only because
scale is single-digit concurrent users. It does not survive being scaled.

### Process killed between commit and dispatch

**Detected by** nothing — it needs no detection. **System does:** the row is committed; the
dispatcher's timer finds it. **State left behind:** an undispatched row, which is correct. This is
Persistence's stated done-criterion and one of the four things CI must assert.

### Worker shuts down gracefully, mid-batch

**Every restart and every upgrade, so this is the common case rather than an edge one.**

**Detected by** the shutdown signal.
**System does:** stops claiming immediately, **releases claims it has not started**, and finishes
in-flight messages within a bounded drain window. Anything still running when the window closes is
abandoned and left to claim expiry.
**State left behind:** released rows, immediately eligible for any dispatcher.

**Releasing unstarted claims is what stops a routine deploy costing a dispatch dead zone** of up to
the full claim window. It also removes one of two opposing pressures on that window's size: without
graceful release, the window would have to be short enough to keep deploys cheap *and* long enough to
tolerate a slow handler, which are contradictory. With release, only the slow-handler pressure
remains — see *Open questions*.

### Dispatch cannot resolve a handler for the row's Type

**Manufactured routinely by the two-process overlap**: during an upgrade the new web process enqueues
a type the still-running old worker has never heard of. Also occurs when a handler is removed while
rows referencing its type remain.

**Detected by** handler resolution returning nothing.
**System does:** **defers.** Releases the claim, stamps first-deferred-at if unset, sets the next
attempt one deferral interval ahead — a fixed interval, not backoff: there is no attempts counter on
this path to key a curve to, and resolution either works once the deploy finishes or never will —
and **does not consume an attempt.** A row still unresolvable past the deferral age, **measured from
first-deferred-at**, is poisoned. Measuring from first deferral rather than from occurred-at is what
preserves the grace after a long outage: a days-old backlog row gets the full deferral window on its
first attempt instead of poisoning instantly.
**State left behind:** an eligible row, undispatched.

**Deferring rather than failing is the decision**, and the alternatives are both bad. Treating it as
a failure burns attempts toward poison on messages that would succeed the moment the upgrade
finishes — a deploy that poisons valid work, with no redrive existing at the time this was written.
Treating it as success loses the message silently. Age-bounding the deferral is what keeps a
genuinely orphaned type from deferring forever.

### Dispatch resolves the handler, but the payload does not deserialize

**The same deploy hazard, and the likelier one:** the additive-only payload rule was violated, or a
contract bug shipped. The Type resolves; every pre-upgrade row throws before its handler runs.

**Detected by** deserialization failing, distinguished from the handler itself throwing.
**System does:** joins the deferral path, not the failure path — defers without consuming an
attempt, stamps first-deferred-at, poisons past the deferral age.
**State left behind:** eligible rows, undispatched.

**Why deferral rather than failure:** burning attempts here mass-poisons the entire pre-upgrade
backlog within minutes of a bad deploy — the exact catastrophe the payload rule exists to prevent,
delivered by the retry machinery itself. Deferring holds the window open for the fix the rule
demands, and **bulk redrive is the recovery when poison happens anyway.**

### Dispatcher dies holding a claim

**Detected by** the claim's age exceeding its window.
**System does:** another dispatcher reclaims the row and dispatches it.
**State left behind:** a row that was claimed and never processed, for at most the claim window.
**Consequence, stated honestly:** if the original dispatcher had already dispatched but not yet
marked, the message is delivered twice. That is at-least-once working as specified, not a defect.

### Outbox handler throws

**Detected by** the dispatcher. **System does:** records the error, increments attempts, sets the
next attempt with exponential backoff, moves on. After a bounded attempt count the message is
**poisoned** — no longer retried, marked with a poison time, raising a metric.
**Partial failure:** one poisoned message must not stop the queue and **must not fail readiness**;
taking an installation out of rotation over one bad message is worse than the bad message.
**State left behind:** the row, queryable, with its last error, until its poison retention window
expires.

**A poisoned message has two exits, and Persistence exposes both as operations: redrive** — a
conditional update clearing the poison mark, attempts and first-deferred-at only if the row still
exists and is still poisoned, so racing the prune pass returns a clear "already pruned" rather than
silent nothing — **and discard**, which sets processed-at, keeps poisoned-at, and appends the reason
to the last error. A discarded row carries both marks, and poisoned-at governs: it prunes on the
longer poison window, so the forensic record outlives the decision to stop retrying. **Both
operations exist per row and in bulk by Type**, because a violated payload rule poisons in bulk and
the recovery must not be a thousand hand-invocations. Without these the only recovery is editing the
database by hand, which this design's own posture treats as unthinkable.

**No endpoint or console ships in D3 to invoke them.** The operations exist on Persistence's public
surface, and the sample demonstrates calling them. An administrative UI is nowhere in the brief and
is not smuggled in here.

**Delivery is at-least-once and handlers must be idempotent.** Exactly-once is not offered, because
it cannot be honestly provided across a boundary Platform does not control.

### Worker process down, web process up

**Detected by** the worker's host registration expiring, and by outbox rows ageing undispatched.
**System does:** the web process keeps serving and keeps enqueuing, and reports **degraded** on
readiness with the missing peer and the backlog age named. Nothing dispatches.
**User sees:** requests succeed; their effects do not propagate — and readiness says why.
**State left behind:** a growing backlog of undispatched rows, which drains when the worker returns.

**This is the failure mode the two-process split creates**, and it is silent from the web side. An
earlier draft made an age-of-oldest-undispatched-row *metric* the only mitigation, which was no
mitigation at all: telemetry export is opt-in, droppable, and absent by default on exactly the
offline homelab this deployment targets. The metric still exists for anyone exporting; **readiness is
what makes it visible to everyone else.**

### Telemetry collector unreachable, slow, or absent

**Detected by** the exporter, out of band. **System does:** buffers to a bounded queue, retries with
backoff internally, then **drops**; logs once on state transition, never per failure. **User sees:**
nothing — the request path is untouched by construction. **State left behind:** dropped telemetry and
a count of it.

Absent is not a failure: export is opt-in with console and file as defaults. This is
[`observability.md`](../docs/docs/observability.md)'s commitment and §2's Game Engine constraint made
operational — **collection must never become a path by which a game can fail.**

### Health check throws or hangs

**Detected by** the exception boundary and per-check timeout. **System does:** a throwing check is
unhealthy; a hanging check is unhealthy at timeout; neither escapes the probe endpoint. **Partial
failure:** an *optional* check failing yields **degraded**, so traffic keeps flowing to a host whose
non-essential provider is down. **State left behind:** none.

**Liveness never depends on an external service**, enforced at registration rather than by
convention: a check declared external cannot be registered as liveness. A database check reachable
from liveness produces a restart loop during the outage it was meant to report.

### Migration application fails

**Detected by** the explicit migration operation. **System does:** fails loudly, stops, does not
continue to the next module. **State left behind:** both providers apply a single migration
atomically, so the failed one is rolled back whole and previously applied ones stay applied. The
database is at a known point, not a partial one.

### Malformed inbound correlation header

**Detected by** parsing. **System does:** ignores it and mints fresh context; never rejects the
request, because a broken upstream header is not the caller's fault. **State left behind:** a trace
that is a new root, counted rather than logged per occurrence.

---

## Concurrency and ordering

**Startup is not concurrent within a process.** Module sort, options binding, registration and freeze
run in order on one thread, enforced by the host builder being single-threaded by construction.
Module order is deterministic across runs because ties in the topological sort break by name.

**Two processes are concurrent with each other, always.** Nothing may assume a single process — the
brief puts a worker alongside the web host permanently, not only during restarts.

**Requests are concurrent**, and that concurrency belongs to the runtime. Platform holds no shared
mutable per-request state: the ambient context is scoped and flows with the operation, so no two
requests can observe each other's tenant, principal or correlation identity. Enforced structurally —
there is no static mutable state to race on.

**The health registry and module graph freeze when the host is built.** Registration after that
throws rather than mutating a structure concurrent readers are walking, which is what makes lock-free
probing correct.

**Outbox rows are claimed by a conditional update that stamps a claim, not by a locking read.** A
locking read that skips locked rows exists in PostgreSQL and does not exist in SQLite, and the brief
made both production paths. A conditional update stamping holder and time works identically on both
and needs no dialect-specific correctness path. PostgreSQL may use its locking read underneath the
same interface as an optimisation; the portable path stays the one that defines the semantics.

**Scheduled work runs under a named lease, and leased work must be idempotent anyway.** Row claims
protect the outbox; work with no per-row guard — pruning — takes the coarser one, which reduces
duplicate runs without preventing them. **Reclaim is not on that list:** it happens inline through
the ordinary claim query, and the conditional-update claim already makes it safe to run concurrently.

**No ordering guarantee is offered.** Rows are *claimed* in sequence order, but concurrent dispatch
and per-message retry mean handlers observe messages out of order. Saying so plainly is the point: a
guarantee that holds only until the first retry is worse than none, because code gets written
against it.

**Telemetry export runs on background threads and is batched.** Ordering across signals is not
guaranteed; a log line and the span describing it may export in either order.

### What must not happen

**No background work — telemetry export, health probing, dispatch, or prune — may block, slow, or
fail a request.** Enforced by four choices: the export queue is bounded and drops rather than blocks;
probe endpoints do not share a pipeline with request handling; dispatch runs in a different process
entirely; and **every background write is batch-bounded, dispatch and prune alike**, so that under
SQLite the worker holds the single write lock briefly.

Prune is named explicitly because it is the one background path that is not per-message. Bounding
"dispatch batches" alone left it uncovered, and its worst case is the largest of any of them.

---

## Settings inventory

Every operational number this design commits to, in one place, each with the reason it holds its
value. A number here is a stated commitment: changing one is a design change, not a tuning tweak.
Only the two retention windows are required — everything else defaults, per the rule that a wrong
value which degrades rather than corrupts should not stop a host.

**The fingerprint membership rule, replacing the enumerated list an earlier draft carried:** a
setting is fingerprinted when two hosts disagreeing on it changes **what happens to rows they
share** — when it decides outcomes, not merely timing. Cadence, batch and transport settings are
exempt: they decide when and how fast, and most are read by one role only, so a disagreement is
meaningless rather than dangerous.

| Setting | Default | Fingerprinted | Why this value |
|---|---|---|---|
| Processed retention window | **required, no default** | yes | The brief chose configurable and declined a default |
| Poison retention window | **required, no default**; validated longer than processed | yes | Forensics outlive routine cleanup |
| Claim window | 5 min | yes | Sized by the slowest legitimate handler — see *Data model* |
| Lease duration | 5 min | yes | Deliberately the claim window's twin |
| Poison attempt count | 12 | yes | With the backoff below, retries span roughly a day |
| Retry backoff | base 30 s, factor 2, cap 6 h | yes | Early retries are cheap; late ones land hours apart, so a same-day fix beats poison |
| Deferral age | 24 h | yes | Longer than any sane upgrade overlap; short enough that a genuinely orphaned Type surfaces the next day |
| Deferral retry interval | 1 min, fixed | no | Resolution flips when the deploy finishes; polling faster buys nothing |
| Dispatch batch size | 20 | no | Bounds the worker's hold on SQLite's single write lock |
| Prune batch size | 500 | no | The same bound; larger because a delete is cheaper than a dispatch |
| Dispatch timer interval | 5 s | no | The latency floor for async work; idle polling at this scale is noise |
| SQLite busy-wait bound | 5 s | no | Longer than any bounded batch holds the lock, shorter than a probe timeout |
| Graceful-shutdown drain window | 30 s | no | Longer than a typical handler, far shorter than the claim window that backstops it |
| Host heartbeat interval | 15 s | no | Liveness resolves at 45 s — fast enough to notice a dead peer, slow enough to survive contention |
| Peer-liveness threshold | 3 × heartbeat interval, derived | — | Derived so the two values cannot disagree; one missed beat cannot flap readiness |
| Registration retention window | 7 days | no | Dead rows are forensic breadcrumbs, then noise |
| Backlog-age threshold | 5 min | no | The claim window's twin: older means dispatch is absent or wedged, not merely busy |
| Peer-absence startup grace | 60 s | no | Covers a routine restart of the other role without flapping readiness |
| Worker probe port | 5100 | no | Any fixed default beats a required setting for a probe surface |

---

## Alternatives considered

### Tenant column — non-null, opaque, with a sentinel

**Chosen:** the logical tenant type, not null, with a well-known all-zero value for the implicit
tenant.
**Rejected — nullable, where null means "no tenant".** The obvious way to model "tenancy isn't built
yet". Rejected because it makes every filter three-valued and lets a row escape a filter silently,
reintroducing the exact correctness class the column exists to prevent.
**Rejected — a readable string slug.** Better logs, no sentinel needed. Rejected because collation
and case sensitivity become correctness properties of every tenant comparison, and those are
database-configuration-dependent in a way an opaque identifier is not — sharper still with two
providers whose collation defaults differ.
**Rejected — an integer key.** Compact, but needs an allocator and makes tenants enumerable across
installations.
**Reversibility: the most expensive decision here.** Changing it after any product has data is a
migration of every table at once, which is the cost §2 flagged.

### Outbox — built, not adopted

**Chosen:** implement it against an in-process bus. ADR-004 §4 requires this evaluation and requires
the reason recorded either way; §3a performed it, and this records the conclusion.
**Rejected — the established messaging libraries.** One is disqualified on licence durability, its
free line's maintenance ending. Another is a real outbox whose every supported transport is a broker
— adopting it puts a broker into local developer execution, an in-scope deployment mode, to serve a
transport decision not taken. A third's outbox claim was unconfirmed.
**Rejected — deferring entirely**, which §3a recommended; overridden by the brief.
**Reversibility: moderate.** It sits behind an interface this repository owns, which is why §4 asks
for the interface to be ours.

### Outbox identity — a version-7 UUID, the sequence demoted to claim order

**Chosen, reversing this document's earlier decision:** a time-ordered UUID minted at enqueue is the
identity; the sequence is claim order only. The earlier draft made the sequence sole identity and
rejected a second identifier as two identities to keep consistent. Adversarial review (2026-08-03)
falsified its premise: on SQLite a rowid alias reuses values after a drain and prune, so the "one
identity" was non-unique over time on a production provider — the choice was never between one
identity and two, but between one and zero.
**Rejected — monotonic allocation alone.** One line of DDL per provider makes the old claim true.
Declined because it repairs a property that, after demotion, nobody is promised, while leaving
identity hostage to provider allocation mechanics and unable to survive a database restore.
**Rejected — dropping the identity claim.** Honest and free, but it pushes dedupe-key invention onto
every consumer, which is the burden Platform exists to absorb.
**Version 7 specifically** because its time-ordering keeps the identity index append-mostly on
PostgreSQL and covers any future cursor need — which is what lets the sequence stay demoted.
**Reversibility: cheap** — no rows exist yet, which is precisely why this happened now.

### Claiming — portable conditional update, not a dialect-specific locking read

**Chosen:** a conditional update stamping holder and time, identical on both providers.
**Rejected — a locking read that skips locked rows on PostgreSQL, with a separate path for SQLite.**
Faster under contention and the idiomatic answer on PostgreSQL. Rejected because it makes the
correctness-critical path dialect-specific, and the SQLite variant would be the less exercised of the
two while being the one running on developer machines. Two implementations of "claim exactly once"
is one more than the number that can be trusted.
**Rejected — no claim at all, relying on one dispatcher.** Impossible now: the brief puts a worker
alongside the web process and permits overlap during restart.
**Reversibility: cheap** — the optimisation can go underneath the interface later.

### Database unavailability at startup — start and report not ready

**Chosen:** distinguish misconfiguration (fails startup) from unavailability (starts, not ready).
**Rejected — fail startup on any database problem.** Simpler, and a more literal reading of §2's
"fails startup rather than the first request". Rejected because on a self-hosted box it turns a
transient database delay into an outage needing manual intervention, and startup ordering between an
application and its database is not something a homelab operator should have to guarantee.
**Rejected — start and serve regardless.** Answers "ready for traffic?" with yes when it is not.
**Reversibility: cheap.**

### Worker as a host role, not a separate package

**Chosen:** one Hosting package, two roles.
**Rejected — a separate worker-hosting package.** Cleaner dependency story for a worker that needs no
web framework. Rejected because startup validation, module ordering, options binding and health
registration would exist twice, and those are precisely the behaviours that must not diverge between
the two processes of one installation.
**Reversibility: cheap now, expensive once consumers reference either.**

### Health check contract in Abstractions, endpoints in Hosting

**Chosen:** split the contract from the transport exposing it.
**Rejected — the whole health concern in Hosting**, which is where §2 lists it. Rejected because
Persistence must contribute a database check and would then depend on Hosting, coupling storage to
the transport package that exposes the probes — inverting the Core/Hosting line §3 names as the
boundary most likely to erode. An earlier wording claimed this kept "a web framework out of the
worker process"; the worker hosts a minimal listener for its probes regardless, so the dependency
direction is the reason, and the one recorded.
**Reversibility: cheap now, expensive once a consumer references the contract's location.**

### Ordering — no guarantee, stated

**Chosen:** at-least-once, unordered, documented as such.
**Rejected — guaranteed global ordering.** Achievable by dispatching serially in sequence order, and
genuinely useful. Rejected because one slow handler becomes a queue-wide stall, and because the
guarantee cannot survive per-message retry — the first backoff reorders the stream, so the promise
would be false exactly when someone relies on it.
**Reversibility: cheap to add, impossible to remove** once handlers assume it.

---

## Open questions

**None.** All eight are settled — four by the brief, four by direct decision on 2026-08-03, and each
is written into the section it governs rather than left here. A further adversarial review the same
day produced thirteen findings; every one is dispositioned in the section it touches, and the
numbers it demanded now live in *Settings inventory*.

That is a claim worth being suspicious of rather than pleased about, so it is qualified:

- **An empty list means nothing further can be decided without new information**, not that the design
  is right. Every operational number is now a stated commitment that can be wrong, and each lives in
  *Settings inventory* with the reason it holds its value.
- **One was removed rather than answered.** Whether SQLite runs the two-process configuration was
  never open — the brief says SQLite serves "single-file homelab installations" and that every
  installation runs two processes, so it follows by composition of two binding statements. Parking it
  here kept a settled obligation where nothing would force it into the contract or the slices; it now
  sits in *Failure modes*.
- **What would reopen this list:** a handler that legitimately runs longer than the claim window, a
  second consumer whose needs contradict the sample's, or the G1 edge arriving and disagreeing — the
  last of which the brief already expects and prices as cheap at 0.x.
