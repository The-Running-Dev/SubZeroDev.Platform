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

**The identifier's SQLite encoding is pinned to RFC 4122 network byte order**, not the platform
`Guid` byte order, whose little-endian first three fields scramble precisely the bytes a version-7
UUID's time ordering lives in. Blob comparison is bytewise, so under the platform default the
identity column sorts in an order unrelated to time on one production provider and in time order on
the other. This is the same defect class as the instant format below — a lexicographic order that
silently stops matching a chronological one — and it reaches every identifier column in every
module's tables, not only the outbox. **The provider contract tests assert that blob sort order
equals mint order** across a run of identifiers minted at distinct clock instants — distinct,
because a millisecond is the resolution the ordering actually has; the tie rule is recorded with
the identity below.

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

**The comparand is pinned with the column, and its clock is the abstraction's.** Every instant
crossing into SQL as a parameter is written by the same fixed-width formatter as the column. The
platform's default parameter binding for SQLite uses a space separator, no `Z`, and a trimming
fractional format — all three of the properties the stored form exists to guarantee, violated — so
pinning only the write side moves the defect from the column to the other side of the comparison
and changes nothing else. And **`now` always comes from the clock abstraction, never from the
database**: eligibility, claim expiry and lease expiry are each evaluated against it, so a database
clock would put the whole dispatch loop beyond the reach of a fake clock and make Testing's
deterministic background work unprovidable. The boundary case binds a comparand as well as writing
a column.

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
| Correlation | text | the ambient correlation at enqueue | The origin's trace-id at any depth — a column because the traceparent stops carrying it after one hop; see below |
| Culture | text | the ambient culture at enqueue | The originating BCP-47 tag; empty means invariant, and the value propagates unchanged through derived events |
| Attempts | integer | dispatch | |
| Next attempt at | instant, null | dispatch | Null means eligible now |
| First deferred at | instant, null | dispatch | Stamped on first deferral — unresolvable type or undeserializable payload; the deferral age measures from it |
| Claimed by | text, null | dispatch | Dispatcher instance identity |
| Claimed at | instant, null | dispatch | **Present so a claim can expire** |
| Processed at | instant, null | dispatch | Null alone does not mean pending — see *Outbox row states* |
| Poisoned at | instant, null | dispatch | Set when attempts are exhausted, kept on discard. **A poisoned row is never eligible, and poisoned-at set means the poison window governs pruning** |
| Last error | text, null | dispatch | Most recent failure only, not a history |

**Ownership:** Persistence. **Lifecycle:** inserted inside the caller's transaction alongside the
domain write; claimed by a dispatcher; dispatched; marked processed *or* poisoned; pruned.

**Enqueue requires an ambient transaction and an ambient operation scope, and throws a named error
without either.** The atomicity above is structural rather than conventional — no code path exists
that silently commits an outbox row apart from the domain write it belongs to. A call site that
genuinely has only the enqueue opens a transaction around it, one explicit line that states the
intent. What an ambient transaction *is* mechanically — one connection every participant enlists
in — is pinned in *Where the provider abstraction cuts*, because with per-module contexts the
phrase otherwise admits two connections, which is two transactions and no atomicity at all.

**The scope requirement is the same move for the same reason.** Trace context, correlation, tenant
and culture are all stamped from the ambient scope, and the paths that have no scope are real rather
than hypothetical: a seeder, a migrate-mode utility, anything Hosting's request pipeline never
touched.
The two permissive alternatives are both worse than a throw. A nullable trace context admits rows
whose correlation identity appears nowhere upstream, so the value quoted in a bug report leads
nowhere. An implicitly minted scope is worse still — it fabricates a traceparent that dispatch will
faithfully rebuild and hand to the handler, a fiction indistinguishable at read time from a real
origin. One more explicit line at the handful of call sites that need it. **The provider contract
tests assert both throws.**

**An explicitly opened scope with no inbound context starts a real root trace — origination, not
the fabrication rejected above.** The row demands a traceparent, and the sanctioned path — the
seeder or utility opening its scope in one explicit line — had no stated source for one: the scope
carried correlation, tenant and principal, and the trace was a fourth value owned by nobody exactly
there. The scope primitive therefore establishes trace context as its fourth member, through the
trace-context contract Observability implements. A scope opened with nothing inbound starts a new
root whose trace-id is the correlation — not a fiction about an origin elsewhere but the true
statement that this scope *is* the origin, the same claim an inbound request with no traceparent
makes when Hosting mints fresh context for it. What stays rejected is the *implicit* version: a
scope nobody visibly opened, minting an origin nobody chose.

**Correlation is a column of its own, because the traceparent stops carrying it after one hop.** A
handler that enqueues a follow-up event is the ordinary case, and at that moment the ambient trace
is the dispatch's new linked trace — so the follow-up row's traceparent stores the link's trace-id,
not the origin's. Deriving correlation from the stored traceparent was therefore right for exactly
one hop: request → event → follow-up left the follow-up carrying a value the originating request
never logged, and the single greppable value went dark at depth two — the failure this design
rejected a second identifier to prevent, produced instead by not persisting the first one. The
column is stamped from the ambient correlation at enqueue — on the request path the trace-id, in a
dispatched handler the origin's value the scope was rebuilt with — so it propagates unchanged
through any depth of derived events, while the stored traceparent keeps the one job it can still
do: the link.

**Culture is a column because the process boundary makes the runtime ambient unrecoverable.** The
web host writes the row and the worker dispatches it later under its own operating-system culture,
so the originating culture is stamped from the ambient scope at enqueue and rebuilt from the row at
dispatch. It propagates unchanged through derived events exactly as correlation does. The value is
the origin's culture, never a recipient preference resolved later by Notifications; an empty tag is
the invariant culture and means the originating actor expressed no preference.

**Identity is the id — a version-7 UUID minted app-side at enqueue, its timestamp drawn from the
clock abstraction** so a fake clock controls it, the same rule occurred-at already follows. The
runtime supplies the generator, so no Platform code is written for it, per ADR-004. The id exists
before the insert — loggable and returnable at enqueue — survives a database restore, and is the
dedupe key at-least-once delivery offers handlers. **The provider contract tests assert id
uniqueness across a drain, prune-to-empty, insert cycle**, the exact sequence that exposed the
defect below, **and that ids sort in mint order on both providers**, which the encoding rule above
is what makes true.

**Mint order means millisecond order, and the tie is unspecified.** Version 7 carries its time at
millisecond resolution and fills the remainder with randomness, and the runtime generator keeps no
counter within a tick — so two ids minted in the same millisecond sort in an arbitrary order, on
both providers alike, and no contract test may assert otherwise. The sort-order tests therefore
advance the fake clock between mints: a frozen clock — the default gesture of a deterministic test
— makes the assertion false without the encoding being wrong. The same caveat bounds the cursor
claim in *Alternatives*: the id orders time to the millisecond and no finer, and anything paging by
it must tolerate ties, which the id doubling as the dedupe key already equips a consumer to do.

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

**One handler per Type, enforced at startup.** A second handler registration for the same Type is a
named startup failure, not a fan-out. Every dispatch-state column above is per row — attempts,
next-attempt, first-deferred-at, poisoned-at, processed-at, last error — so N handlers behind one row
share one retry budget and one poison verdict. A single broken subscriber then burns the attempts of
the subscribers that already succeeded, poisons delivery for all of them at once, and leaves a last
error that cannot even say which of them is broken. A product that wants two things to happen writes
one handler that does two things: it owns the composition, which is also where it can decide what
partial failure means. **The direction matters as much as the rule** — relaxing one handler to many
later is additive, while tightening many to one later breaks every consumer that spread work across
handlers, so the restrictive reading is the reversible one at 0.x.

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

**That sizing is only true because a claim covers one message rather than a batch.** See *Control
flow*: rows are claimed and marked one at a time under a per-tick budget. Claimed as a batch, a
claim would age across the whole batch's serial duration instead of its own message's, and the
window would have to be sized against the budget times the slowest handler rather than the slowest
handler — a number with no defensible value.

**Two retention windows, both required settings with no default**, validated as present at startup
and failing the host when absent. The brief chose configurable and declined to set a default, and
"configurable with no default" silently becomes "never prune" otherwise.

- **Processed rows** are pruned past their window.
- **Poisoned rows** are pruned past a separate, longer window, and pruning one logs at warning.

An earlier draft of this document claimed retention closed the only unbounded-growth surface Platform
owns. That was false: a poisoned row is never processed, so a recurring poison source — one handler
bug, one malformed payload shape — accumulated rows forever under the single-window rule. The second
window is what makes the original claim true of rows dispatch has finished with — and the pending
state, which no window may touch, is bounded by a decision stated rather than assumed:

**Pending rows are unbounded, by decision rather than oversight, and the bound that exists is the
operator.** No retention window can apply — pruning an undispatched row is dropping a committed
write, the failure the outbox exists to prevent, performed by the outbox — and no backpressure
applies either: enqueue is inside the caller's transaction, so refusing it fails the domain write
with it, converting a worker outage into a web outage that surfaces as request errors before
readiness says anything. What bounds growth in practice is the operator acting on the surface that
reports it: worker absence and backlog age already degrade readiness, and the pending *count* joins
them with a threshold of its own, so a backlog that is large rather than merely old is named while
there is still time to act. The honest limit is the disk, and its failure is total — on the
single-file homelab every write fails, the heartbeat among them — which is exactly why the count
sits on the always-on surface rather than in a metric nobody exports.

### Event Type names, and the payload's canonical form

Two things the *Type* and *Payload* rows above depend on and neither states. Both were surfaced by
deriving the contract, which could not write a signature over either.

**The stable Type name is supplied by an explicit registration call that binds three things at once:
the name, the CLR event type, and its handler.** Not an attribute, not a member on the event.

The deciding constraint is one the rules above imply without saying: **dispatch must get from a
stored string to a CLR type in order to deserialize, and it has no instance to ask.** An instance
member cannot answer that question, because the instance is what deserialization produces. Every
candidate mechanism therefore ends up building a name-to-type map at registration; what differs is
only where the literal lives and how a forgotten one fails.

Binding all three in one call is what makes the checks single rather than plural. Registration is
already the place one-handler-per-Type is enforced, so name uniqueness and handler uniqueness become
one verdict instead of two that can disagree. **Enqueuing an event type that was never registered is
a named throw at enqueue**, the same class as the missing transaction and the missing scope — the
name is stamped onto the row, so there is nothing to write without it. On the dispatch side an
unregistered name is already covered: it is the deferral path, which exists for exactly the upgrade
window where a name is legitimately unknown here and known elsewhere.

**The registration is declarative, and each role validates the half it runs.** Binding the handler
into the call puts the triple in both hosts — the web host must register in order to enqueue — but
a registration is a statement, not a resolution: the web host records the handler type without ever
constructing it, and the handler's constructor dependencies are resolved and validated only in the
role that dispatches. Anything else drags every handler's dependency graph into the web host's
container, where a handler depending on something worker-only fails the startup of a process that
would never have run it. Name uniqueness and one-handler-per-Type check identically in both roles,
off the declaration alone; a handler that cannot be constructed is a named worker startup failure,
and no failure at all in the web role.

Renaming the CLR class does not touch the literal at the registration site, which is the property
the Type row demands. **A breaking payload change is a new CLR type registered under a new name, with
the old registration retained until the old rows drain** — so the registry legitimately holds two
names for two types that mean successive versions of one event, which is the shape the additive rule
already assumed without saying where it lived.

**An attribute stays available as later sugar** — an attribute that supplies the name *to* a
registration call is additive and breaks nothing. The reverse is not, which is why this is the
direction to take at 0.x.

**The serialiser is `System.Text.Json`, with a Platform-pinned options instance that is not
injectable.** [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4's check: the
runtime already ships it and it covers the gap whole, so a serialisation dependency would fill
nothing.

**Not injectable, because the serialiser is part of the durable format rather than a preference.** A
dependency-injection registration is not a setting, so the settings fingerprint cannot see two hosts
of one installation disagreeing about it — and this design routes every silent condition through
readiness precisely because a condition nobody can observe is worse than one that fails loudly.
Pinning removes the condition instead of reporting it. A product that swapped the serialiser after
rows existed would mass-poison its own backlog with no operational signal at all.

Four properties are pinned because they *are* the format:

- **Unmapped members are ignored, in both directions.** The two-process overlap means an old worker
  reads rows a new web host wrote as well as the reverse, so tolerant-reader has to work both ways
  or the additive-only rule is half a rule.
- **Enums serialize as strings.** A numeric enum breaks additivity the moment a member is inserted.
- **Property naming and null handling are fixed by Platform**, not derived from a product's own
  serializer configuration.
- **Number handling is fixed**, so a value written by one host reads identically on the other.

**There is no extension point. The converter escape hatch an earlier draft allowed is cut, and the
cut is this section's own argument applied to itself.** That draft reasoned a converter's blast
radius matched the additive payload rule's; what it missed is that a converter is a
dependency-injection registration, and the paragraph above already establishes that the settings
fingerprint cannot see two hosts of one installation disagreeing about DI. A half-upgraded
installation with a converter on one host and not the other writes bytes the other cannot read,
defers the affected rows for a day, then mass-poisons them with no signal naming the cause — the
exact invisible divergence pinning exists to remove, readmitted through the last paragraph of the
section that removed it. So the format ships closed: a payload is what `System.Text.Json` handles
natively under the pinned options, or it is a different payload. The direction rule decides the
timing here as it does elsewhere at 0.x — adding converters later is additive, removing them later
breaks whoever used them.

Deserialization failure is unchanged: it is the deferral path, not the failure path.

### Outbox row states, by predicate

Three consumers read this row's state — readiness, the prune pass, and redrive — and an earlier
draft left each of them to infer it from the timestamps. They inferred differently, and the three
disagreements below were one omission. The states are therefore defined once, as predicates over
columns that already exist, and every consumer derives from them.

| State | Predicate | Prunes on | Counts toward |
|---|---|---|---|
| Pending | processed-at null, poisoned-at null | never | backlog age |
| Processed | processed-at set, poisoned-at null | processed window | nothing |
| Poisoned | poisoned-at set, processed-at null | poison window | poison count |
| Discarded | both set | poison window | nothing |

What the table fixes, in the order the omission bit:

- **Backlog age considers pending rows only.** Under "processed-at null means undispatched", one
  poisoned row is the oldest undispatched row within days and permanently exceeds a five-minute
  threshold whose stated meaning is that dispatch is absent or wedged. Dispatch would be fine. Two
  readiness conditions would then fire with contradictory diagnoses, one of them false, for as long
  as the poison retention window kept the row — and the backlog age is the very detail this design
  elected to separate "worker down" from "worker mispointed".
- **Poison count considers poisoned rows only, never discarded ones.** Discard is the operator
  saying *I have looked, and I have decided to stop retrying*. A surface that stays degraded for the
  full poison window after that disposition is demanding a decision that has already been made, and
  it trains operators to ignore the one signal this design elected as always-on.
- **Redrive requires the poisoned state, not merely a poison mark.** A discarded row still carries
  poisoned-at. Redriving one would clear the poison mark while processed-at stayed set, producing a
  row that can never deliver and that now prunes on the short window — an operator "recovery" that
  delivers nothing and destroys the forensic record discard exists to preserve.

**Eligibility is a predicate here too, stated once because three code paths evaluate it.** A row is
*due* at next-attempt-at, or at occurred-at while next-attempt-at is null; it is *eligible* when it
is pending, due, and either unclaimed or holding an expired claim. The claim query, the deferral
re-check and redrive all derive from this statement — the instant-format rules exist to make
exactly this comparison correct in SQL, and a predicate served by that much machinery deserves to
be written down where the machinery can be checked against it.

**Backlog age measures time past due, not time since occurred.** Age-since-occurred manufactures
the false diagnosis this table was built to kill, three routine ways: a deferred row during an
upgrade is pending and "old" within five minutes while dispatch faithfully re-defers it for up to a
day; a backing-off row behind a failing handler is pending and "old" through twelve attempts
spanning a day; and a bulk-redriven row is pending with an occurred-at days in the past, so the
recovery operation would trip "worker down" at the moment it succeeds. Time past due excludes all
three by the same mechanism that catches the real failure: deferral and backoff each push
next-attempt-at forward, so a dispatcher that is working keeps its backlog young *by acting on it*,
while a dead or mispointed one lets due rows age — which is the condition the threshold's stated
meaning has claimed all along. The poisoned-row version of this defect was one omission ago;
deferral, backoff and redrive were the same omission, closed here at once.

**State transitions are conditional on the live claim, so the states keep the producers this table
says they have.** Without that, a race manufactures the one state defined as a human decision:
dispatcher A stalls past the claim window mid-handler on a final attempt, B reclaims, fails and
poisons; A completes and marks processed — both marks set, which this table reads as *discarded*,
excluded from the poison count and refused by redrive, recording an operator disposition nobody
made. Every dispatch-state write — mark processed, record failure, defer, poison — therefore
applies only while the writer still holds the claim, and a write that lost its claim is a no-op,
counted as evidence of duplicate delivery rather than escalated, because losing a claim mid-flight
is the at-least-once window working as priced. Discard alone produces the both-set state, which is
what lets it keep meaning a decision.

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
nothing to review against. Testing ships the assertion and the sample's CI runs it, on both
providers: after migration, no foreign key in the applied schema references a table outside its
owner's model. The check needs no new declaration of ownership — per-module histories already put
each module's tables in a model of its own, so ownership is a fact the mechanism carries rather
than a thing anyone maintains — which turns the rule into a check rather than an intention.

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
(nullable), the trace context the scope established, and culture (always present, defaulting to the
invariant tag; D3 resolves it from no header, claim or preference). Scoped to one operation.
**Correlation identity is the originating trace-id, not a second value beside it** — two ids mean
two propagation paths and two chances to disagree, and the one that disagrees is the one quoted in
a bug report. On the request path it and the current trace-id are one value. They part company in
exactly one place — outbox dispatch starts a new linked trace while the correlation stays the
origin's, see *Control flow* — so a single value stays greppable end to end even where the trace
changes.

**It is established as well as read, and both operations have the same owner.** The scope-opening
primitive is a contract in Abstractions alongside the four accessors, because there are two
establishers rather than one — Hosting on an inbound request, Persistence on each dispatched message
— and an unowned write path gets invented twice. See *Module boundaries*.

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
| Oldest **pending** row past due longer than a threshold | Worker down, or dispatch wedged — past due, never past occurred, per *Outbox row states* |
| **Pending** rows above a count threshold | The disk is the only bound past this, and its failure is total |
| Any **poisoned** rows present, discarded excluded | The handler is broken and nobody is watching |
| Schema has pending migrations | See *Failure modes* |

**Wire mapping, because a three-state model over a two-state protocol is otherwise undefined:**
healthy and **degraded both return success**; only unhealthy fails. Degraded means *take traffic,
something needs attention*. Mapping degraded to failure would drain a host whose optional provider is
down — the precise outcome the criticality flag exists to prevent.

**Peer absence is scoped so the signal keeps meaning something.** In the development environment —
already host-derived and read-only — a missing peer is informational in the response body, never
degraded: the default developer gesture is one process, and a surface that is degraded on every
inner-loop run trains everyone to ignore the one signal this design elected as always-on.
Everywhere else, peer absence degrades only once it has **persisted for the grace window — a
rolling measure on the observing host's clock from the absence first being seen, never a
startup-scoped exemption.** Startup-scoped could not cover the case the setting exists for: the
surviving web host watching a routine worker restart is long past its own startup, and graceful
shutdown deletes the peer's row, so the absence lands immediately — under a startup grace every
deploy would flap the surface the grace exists to steady. Rolling covers both ends with one clock:
a peer back inside the window degrades nothing, and a crash, which deletes no row, degrades at
liveness lapse plus the window.

**The sample runs both roles in a non-development environment, so CI exercises the real check
rather than the carve-out** — the carve-out keys on the environment, not on how many roles run, so
a development-environment CI would exercise the carve-out however many processes it started.
Outside development nothing migrates automatically, so CI runs migrate mode as an explicit step
before the hosts start. That is coverage rather than ceremony: the one-shot command this design
hands operators is otherwise the one path no assertion touches.

**Every condition in the table above is contributed by Persistence, and a host composed without it
has none of them.** The dependency graph permits that composition deliberately — Hosting does not
reference Persistence — so the guarantees are scoped out loud rather than lost in silence: split
detection, backlog age, poison visibility and the migration comparison hold for hosts that register
Persistence, which the sample does through the standard registration call and the products will.
What keeps the scoping honest is the probe body: a report enumerates the condition sources that are
registered as well as how each scored, so an absent check reads as absent instead of being
indistinguishable from a passing one. A persistence-less host is a supported shape with a smaller
surface — stated as such here, because the alternative reading, Persistence as a de facto
requirement of Hosting, would put a dependency in fact where the graph records none.

### In-memory — error envelope, telemetry signals

The error envelope is wholly derived from the failure and the ambient context, serialized, then
discarded. Telemetry signals are buffered and **droppable by design** — see *Concurrency*.

**The local log is mandatory; remote export is optional.** Both host roles write structured UTF-8
JSON Lines through Serilog to console and to
`<content-root>/logs/<service>-<role>-.jsonl` by default, with only the directory configurable. Both
sinks use the same formatter and one 10 000-event asynchronous buffer that drops rather than blocks.
The file rolls daily and at 100 MB, retains no more than 31 files and no file older than 14 days,
and the role is part of its filename. Serilog's shared-file mode permits multiple instances of that
role to append safely. File creation, write and buffer
failures never stop the host and never reach application work; an emergency console diagnostic is
written once when the sink enters failure or dropping and once when it recovers. The supported
async-sink inspector supplies the exact local-output queue drop count.

**OTLP turns on only when `Platform:Telemetry:OtlpEndpoint` is set.** The value is an absolute HTTP
or HTTPS base URI; anything else is `ConfigurationError.InvalidSetting` at startup. An absent value
starts no exporter and makes no outbound connection. A present value enables the official
OpenTelemetry log, trace and metric exporters using fixed OTLP HTTP/protobuf and the standard
`v1/logs`, `v1/traces` and `v1/metrics` signal paths. The SDK's experimental in-memory retry is
enabled while OpenTelemetry is pinned to 1.17.0; its queue is bounded, its retries never use disk,
and upgrading the package requires this feature to be revisited. Authentication headers, client
certificates, per-signal endpoints, alternate protocols and a second configuration source through
`OTEL_EXPORTER_OTLP_*` are out of D3.

**All three signals share one identity and one sanitisation boundary.** Their resource carries
`service.name`, `service.version`, `deployment.environment.name` and the bounded
`subzerodev.host.role`; those four fields also appear on every JSONL record. Logs additionally carry
ambient correlation, tenant, culture and actor when present. `service.instance.id` is deliberately
not a resource attribute because resources become metric attributes. Request correlation is the
trace id; dispatch correlation stays in structured logs while the span link represents the origin,
so no duplicate unbounded correlation attribute is added to a span.

The internal, non-injectable redactor runs before Serilog's console/file sinks and the OTLP branch.
It discovers secret non-empty configuration values by case-insensitive key segments including
`authorization`, `cookie`,
`password`, `secret`, `token`, `api-key`, `connection-string` and `client-certificate`, and replaces
those values with `[REDACTED]` in structured properties, rendered messages, exceptions and nested
text, and span attributes and events. Platform never captures HTTP headers or
bodies, event payloads, SQL parameter values or connection strings. This is a fixed safety policy,
not an extension point.

**Metric labels are outside the redactor, and the allowlist below is what keeps them clean
instead.** Redaction is a filter over values a signal happened to carry; a label allowlist is a
statement of which labels may exist at all, and the second is the stronger guarantee where it
applies. A closed set drawn from host role, HTTP method, route template, status, provider and closed
enums has nowhere for a secret to arrive, so a redaction pass over it would have nothing to find —
and stating redaction as the mechanism there would leave the real one, the allowlist, looking like
defence in depth rather than the load-bearing part.

**Sampling and metric cardinality are fixed policy in D3.** Incoming traces honour the upstream
sampled flag. A new root HTTP trace uses deterministic trace-id head sampling at 10%. Dispatch starts
a new linked trace and a small Platform sampler copies the stored origin's sampled decision; every
other trace uses the official parent-based ratio sampler. Keeping errors or slow traces requires
tail sampling at a collector and is not promised locally. Every exported instrument's labels are
drawn only from host role, HTTP method, route template rather than raw path or query,
status, database provider, and closed outcome or signal enums. Tenant, correlation, instance,
message, event and user identifiers — and arbitrary tag pass-through — are forbidden.

**Platform authors no instrument of its own in D3, and the allowlist therefore governs the
instrumentation packages' metrics rather than Platform's.** `PlatformTelemetry.MeterName` is
established as the stable name a later Platform instrument will publish under, and Observability
subscribes to it so that adding one is additive; nothing publishes to it yet. What the metric
pipeline carries in D3 is the official ASP.NET Core, HTTP and runtime instrumentation, and the
allowlist is asserted against those. **No operational condition depends on a Platform metric** —
that is the point of routing every one of them through readiness, and it is what makes the absence
a scoping decision rather than a gap. A metric is not a mitigation on an installation that exports
nowhere by default, which the health section already argues at length; declaring instruments here
in order to satisfy a sentence would put public telemetry surface at 0.x ahead of a consumer for
it.

---

## Module boundaries

Six packages, as the brief decides. Two ownership overlaps in §2 block the graph and are resolved
here.

**Overlap 1 — correlation ids sit under both Hosting and Observability.** Resolved in three parts,
because two are not enough: **the ambient correlation accessor is a contract in Abstractions**, next
to current-principal, current-tenant and current-culture, the other accessors over the same operation
context; **Observability owns the identity's derivation, its propagation across process boundaries,
and sampling**, because those *are* trace context; **Hosting owns establishing it on an inbound
request**.

The accessor had to move because Persistence stamps the trace context onto every outbox row and
reconstructs it at dispatch, while depending on Abstractions and Core only. Without the contract in
Abstractions, that column is unimplementable without either a new Persistence→Observability edge or
a silent relocation by the first implementer.

**The accessors have an owner; the writer needed one too.** Reading the context is not the only
operation on it — something must *establish* it, and there are two establishers rather than one:
Hosting on an inbound request, and Persistence on each dispatched message. **The operation-scope
contract therefore lives in Abstractions beside the accessors** — one primitive that opens a scope
carrying correlation, tenant, principal, trace context and culture, and closes it. Trace context is
the fourth member for the reason recorded with the enqueue rule: the row demands a traceparent, and
the explicit-scope path had no source for one. Culture is the fifth: it defaults to invariant in D3,
may be supplied explicitly by a product, and has to cross the same row boundary. Left unowned, the
second establisher
invents its own write path, which is the silent relocation this overlap was resolved to prevent,
one layer further down and harder to see.

**Trace context crossing the outbox is propagation, and propagation is Observability's.** Persistence
stamps a traceparent and tracestate, parses them back at dispatch, and starts a new linked trace
honouring the stored sampling flags — every one of which the resolution above assigns to
Observability, performed in a package with no edge to it. So **the W3C parse, root-start and link
operations are a contract in Abstractions that Observability implements**, and Persistence calls the
contract. **Formatting is not among them**, because the scope's fourth member already holds the
established context: a handle returns the `TraceContext` it started, the scope carries it, and every
stamping site reads it from there rather than asking for the current one to be rendered. The alternative — Persistence handling W3C strings itself against the runtime — puts trace
handling in two packages that can drift apart on the one value this design promises stays greppable
end to end.

**Overlap 3 — background work is owned by nobody, and Hosting has to start it.** The worker role must
run the outbox dispatcher, which belongs to Persistence, while not depending on Persistence.
Resolved the same way: **the background-work contract lives in Abstractions**; **Core owns
registering, ordering and role-scoping** the registrations; **Hosting runs everything registered
against the contract, in the role each registration declares**, without knowing what any of it is;
**Persistence registers the dispatcher, the prune pass and the registration heartbeat as background
work** and supplies the lease that guards the first two.

**Registrations declare their role, because not all background work is the worker's.** The
dispatcher and the prune pass are worker-only. The registration heartbeat is not, and that is what
forced the scoping: the peer check detects a split database only if *both* hosts write their row, so
the web host has one loop it must run — and under a worker-only rule no package was permitted to run
it. Hosting cannot start it without an edge to Persistence; dropping it leaves the split undetectable
from the side positioned to notice, which is the whole mechanism. One declared role per registration
covers both cases through the channel that already exists, and it states out loud which loops run
where instead of leaving every future infrastructure loop to improvise a second channel. The brief's
"the worker host owns all background work" is preserved where it carries meaning: no product work
and no dispatch runs in the web host. A telemetry exporter's own threads are that exporter's
internals, not registrations, and are governed by *Concurrency* instead.

**The background-work contract is tick-shaped, which is what makes Testing's determinism
providable rather than aspirational.** A registration exposes one tick — a dispatch pass under its
budget, a prune batch, one heartbeat — and Hosting owns the timers that invoke ticks on the
intervals *Settings inventory* names. Nothing looser lets Testing keep its done-criterion: the
clock abstraction answers *now*, so a fake clock moves claim expiry, backoff and lease expiry, but
no fake clock drives a real five-second timer — determinism needs the schedule and the clock
separated, so the test host replaces the one and controls the other. The test host invokes ticks
directly, the fake clock supplies the instants those ticks compare against, and no timing-dependent
test contains a wall-clock wait. The shape also keeps the boundary honest in the other direction: a
loop that hides its schedule inside itself is a loop Hosting cannot run in the role it declares,
and tick-shaping is what keeps the schedule Hosting's.

**None of these adds an edge.** That is the test each resolution had to pass — a boundary problem
solved by adding a dependency is a boundary problem renamed.

**Overlap 2 — health.** §2 puts the endpoints under Hosting, Observability owns provider health, and
Persistence must contribute a database check. Resolved: **the check contract lives in Abstractions;
the endpoints live in Hosting.** Any package contributes a check depending on Abstractions alone.
Without this, Persistence depends on Hosting — a storage package coupled to the transport package, a
dependency pointing the wrong way that every future check-contributing package would copy.

| Package | Owns | Depends on | Exposes |
|---|---|---|---|
| **Abstractions** | Result and error types, clock, current principal, current tenant, **current correlation**, **current culture**, **the operation-scope contract that establishes them, trace context included**, module contract, event and **event-handler** contracts, **health check contract**, **background-work contract**, **trace-context parse/root-start/link contract**, **stable activity-source and meter names** | The BCL, and the dependency-injection abstractions the module contract's signature requires | Interfaces, value types and well-known names only |
| **Core** | Default implementations, module registration, ordering, startup validation, typed configuration binding, **background-work registration, ordering and role scoping** | Abstractions | Registration surface, module graph |
| **Observability** | Telemetry wiring, correlation identity derivation and propagation, **the trace-context contract's implementation**, redaction, instrumentation, sampling policy, mandatory Serilog console/file sinks and optional official OpenTelemetry OTLP exporters | Abstractions | Configuration surface, correlation derivation and propagation |
| **Persistence** | Transaction boundary, **a provider-neutral child activity around each unit-of-work transaction**, per-module migrations, provider abstraction, outbox and dispatcher, **handler resolution and per-message scope reconstruction**, leases, **host registration**, audit fields, soft delete, tenant column | Abstractions, Core | Transaction abstraction, outbox enqueue, lease acquisition, **redrive and discard**, **readiness checks for peer presence, backlog age, poison count and pending migrations**, **dispatcher, prune and the registration heartbeat registered as background work** |
| **Hosting** | Host bootstrap for **both host roles**, DI wiring, middleware order, graceful shutdown, health and readiness **endpoints**, request/principal/correlation/tenant/culture scope establishment, **running registered background work in the role each registration declares** | Abstractions, Core, Observability | The standard registration call, in web and worker forms |
| **Testing** | Test host for both roles, fake clock, fake principal, tenant and culture, capture, deterministic background work, **provider contract tests** | All five | Test host builder |

**Abstractions is BCL-only in every member but one, and the exception is named rather than
finessed.** The module contract's registration delegate takes an `IServiceCollection`, which lives
in the dependency-injection abstractions package rather than in the BCL — so a literal "nothing but
the BCL" was false the moment the module contract was written down, and the two ways to make it true
are both worse. Dropping the parameter leaves a module contract that cannot register anything, which
is the only thing a module does. Moving the contract to Core costs a product the ability to declare
a module against Abstractions alone, which is the property that makes Abstractions a separate
package at all.

**What the rule was protecting survives intact**, which is why widening it is not a concession:
[`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 states the criterion
as **no dependency on any other Platform package**, and a consumer compiling against Abstractions
alone. Both still hold. §2's reason — that Abstractions is the one package a product may depend on
without inheriting a runtime choice — holds too: the dependency-injection *abstractions* are
container-agnostic by construction and supply no container. An implementation package here would
break the rule; this does not. Nothing else in Abstractions reaches outside the BCL, and anything
that later wants to is a design change rather than a precedent.

**Two host roles, one package.** The worker is not a second Hosting package — it is the same
bootstrap with the product HTTP surface omitted and background work enabled: no product endpoints,
no request pipeline, a minimal listener retained for its probes. Splitting it would duplicate startup
validation, module ordering and health registration, which is where the behaviour that must not
diverge lives.

**The provider abstraction is real, not notional.** Two production providers is what forces it; §2's
contract tests are what verify it. A single-provider design with an abstraction "for later" would
have the abstraction shaped entirely by one provider's semantics.

### Where the provider abstraction cuts

Named here because the sentence above committed to a real abstraction and then left its shape to
whoever wrote the first line of it — the third thing deriving the contract could not write a
signature over.

**The seam is one store per Platform-owned table — outbox, lease, host registration — with a single
implementation of each, parameterised by a provider-capability contract.** Not one implementation
per provider.

**Stores, and not a general data-access abstraction**, because §2 has Persistence *refuse* to impose
a repository pattern: a product uses the data-access layer directly for its own tables. These three
are the tables Platform both defines and stores, and the seam covers those and nothing else.

**One implementation and not two**, because the policy is where the correctness lives — which row to
claim, what to stamp, whether a failure consumes an attempt, when a row is poisoned rather than
deferred. Two copies of that is the same objection this document already raised against a
dialect-specific claim: *two implementations of "claim exactly once" is one more than the number
that can be trusted*, and it applies with more force to the surrounding policy than to the statement.

**The membership rule for the capability contract, so its growth is checkable rather than a matter
of taste: a member belongs in the capability when the two providers must do something *different* to
produce the same observable result.** Anything the providers do identically stays in the store.
That rule admits exactly the differences this document has already named, and it is what stops the
capability accreting policy:

- the instant formatter — one formatter serving both the column and the bound comparand;
- the identifier encoder — RFC network byte order on SQLite;
- the claim statement, portable by default, with PostgreSQL free to use its locking read underneath;
- the bounded delete statement, which the two dialects express differently;
- transaction-begin mode, immediate where the transaction will write;
- the migration lock — an advisory lock on PostgreSQL, an immediate transaction on
  SQLite; why it is native rather than the lease is recorded in *Failure modes*;
- the exception classifier — what counts as busy, as a conflict, or as unreachable differs per
  provider while the response to each does not;
- the startup preconditions — WAL, and the busy-wait bound;
- the migration history table's name per module.

**Transaction intent becomes a parameter, and that is a consequence worth stating rather than
discovering.** "A transaction that will write begins immediate" is only actionable if something
tells the boundary which kind it is opening, and no implementation can infer it before the first
write. The unit of work therefore takes the intent from its caller. The alternative — treating every
transaction as a writer — is safe and costs read concurrency; it is rejected because it makes the
rule unfalsifiable and hides the one case the rule exists for.

**"Ambient transaction" is one connection, stated mechanically because per-module contexts made the
phrase ambiguous.** Separate migration histories put each module's model in a context of its own,
and Platform's stores are a further one — so "the caller's transaction" spans contexts that would
each open a connection by default, and two connections is two transactions: the domain write and
its outbox rows would commit separately, which is the partial write the outbox exists to make
impossible, reintroduced by the seam between contexts. The unit of work therefore owns the
connection and the transaction, and every participant — the product module's context and Platform's
stores alike — enlists against that one connection; the outbox store never opens its own. Enqueue's
required ambient transaction is this pair, and nothing else satisfies it. This is also why CI's
kill-between-commit-and-dispatch assertion means something: atomicity that held only when a sample
happened to co-locate two writes on one context would be the bespoke wiring the brief's definition
of done names as failure.

**The contract tests target the stores and the capability together.** The stores are what
"interchangeable" means, and the capability is the only place a difference is permitted to live —
so a deliberately broken capability is what the suite must go red against, and a suite that only
exercised one provider's store would prove nothing about either.

### Dependency direction

```text
Abstractions ──► (BCL + DI abstractions)
     ▲   ▲   ▲
     │   │   └────── Observability ──►┐
     │   └────────── Core ──►┬────────┴──► Hosting
     └──────────────────────┴──► Persistence

Testing ──► all five          (test-only; never a dependency of anything shipped)
sample ──► Hosting, Persistence, …
```

**Acyclic.** Abstractions has no outbound edge to another Platform package; Core depends only on it; Observability only on it;
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
health registrations freeze → module graph freezes → **the host starts the background work registered
for its role**, the registration heartbeat among them in both roles → host runs in its role.

**Registration is the heartbeat; there is no separate write.** The heartbeat loop upserts the host's
row — role, instance, settings fingerprint — so the first successful beat is the registration and
every renewal is the same statement. One mechanism instead of two, and it is what makes a fresh
database non-fatal: against a store whose schema does not exist yet the beat fails, the loop retries
at its ordinary interval, and the row appears the moment migrations run. No bespoke startup retry,
and no ordering dependency between startup and the schema.

**Registration happens in the store, not in memory**, which is what makes the peer check work: a host
writing to the wrong database registers itself there too, so its absence from the right one is
detectable from the other side.

The two roles diverge only at the end: the web role maps endpoints and serves; the worker role serves
probes only. **Both start the registrations that declare their role**, which for the web host is the
heartbeat alone and for the worker is the heartbeat, the outbox dispatcher and the prune pass.
Hosting does not know what any registration is, which is what lets it start Persistence's dispatcher
without depending on Persistence — and what lets the web host run a Persistence-owned heartbeat on
the same terms rather than through a second mechanism invented for it.

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
the implicit tenant and culture to invariant → span opened → product handler runs → **the
transaction commits the domain write and any outbox rows atomically** → response.

On unhandled failure the transaction rolls back, taking the outbox rows with it; the failure maps to
an error envelope carrying the correlation identity; the span records the error.

### 3. Outbox dispatch — triggered by a timer in the worker role

Claim **one** eligible row — **expired claims are eligible, so reclaim needs no separate pass** →
dispatch it in-process → mark processed, or record the failure, increment attempts and set the next
attempt → repeat up to a per-tick budget → separately, under a lease, prune processed and poisoned
rows past their windows.

**A claim covers one message, and the batch size is a per-tick budget rather than a single claim.**
An earlier draft claimed twenty rows at once and dispatched them serially, which meant each claim
aged across the *batch's* duration rather than its own message's: twenty handlers averaging sixteen
seconds exceed the five-minute window, so the tail of every busy batch expires while still queued in
this worker's own memory, becomes eligible, and is picked up and redelivered — by this same
dispatcher's next poll, or by the overlapping twin during a deploy. That is routine duplicate
delivery under nothing but moderate load, presented to consumers as the rare crash-window case the
at-least-once caveat prices. Per-row claiming also holds SQLite's single write lock for one row at a
time instead of twenty, and it is what makes the claim window's sizing argument — the slowest
legitimate handler — true as written rather than aspirational.

**The dispatcher does not claim at all while this host has unapplied migrations**, and consumes no
attempts while it waits. The reasoning is in *Failure modes*, under the wrong-schema branch.

**Prune runs in bounded batches, and it is the one background path that is not per-message.** That
is what made it easy to overlook: on SQLite it competes for the same single write lock, and its
worst case is the largest of anything here — a worker returning after days down, or a retention
window shortened, leaves an arbitrarily large backlog to delete while the web process needs that
lock for every request.

**The bound resolves that worst case by spreading it, not by absorbing it, and the arithmetic is
worth stating.** A tick issues one bounded delete per target, so a backlog clears at the batch size
per target per interval — the two numbers in *Settings inventory* — rather than in one pass. The
worst case above therefore costs days of ordinary hourly ticks instead of one long hold on the write
lock, which is the trade this bound exists to make. It is only a sound trade because a row awaiting
prune is inert: processed and discarded rows are never read again, a poisoned row stays queryable
the whole time, and no readiness condition counts any of them. Nothing degrades while the backlog
drains slowly, so slowly is the right speed. **What this rules out is a prune expected to keep pace
with a heavy poison source** — 12 000 rows a target per day at the defaults, against a poison
retention window measured in days; past that the row count grows, and the poison-count readiness
condition is already the surface that says so.

**The trigger is a timer, not a signal from the writer.** With the writer in the web process and the
dispatcher in the worker, an in-process signal cannot reach it, and a cross-process one would need a
transport — which is unchosen and would need the network the brief forbids depending on. The timer is
also the only mechanism that survives the process dying between commit and dispatch, which is the
case this exists for.

**Each message is dispatched in its own scope, with its context rebuilt from the row — not inherited
from the worker.** The dispatcher opens a fresh dependency scope per message, resolves **the**
handler for the row's Type through the event-handler contract — one per Type, per *Data model* — and
opens an ambient operation scope populated from the row itself: correlation from the row's
correlation column — the origin's value at any depth, where the stored traceparent by the second
hop carries only the previous link's trace — tenant from the row's tenant column, culture from the
row's culture column, principal null — the worker has no principal and must not invent one.

**Dispatch starts a new trace and links it to the stored one; it does not continue it.** An earlier
draft said the opposite, and the design's own worker-down scenario is what breaks it: a backlog can
drain days after the originating request ended. Continuing produces a trace of unbounded duration
that no backend joins usefully, and orphan spans whenever the origin was sampled out. A link is the
standard shape for a consumer decoupled in time from its producer, and it degrades gracefully — the
correlation survives even when the origin is long gone. The stored trace flags travel with it, so the
new trace can honour the origin's sampling decision rather than re-deciding blind, and the stored
trace state carries any vendor sampler detail beside it.

**The trace changes here; the correlation does not.** The ambient correlation is rebuilt from the
row's correlation column, so handler logs, error envelopes and follow-up rows carry the value the
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
earlier. Culture would likewise fall back to the worker's default and lose the origin's language at
the first hop. That is precisely the cost class these columns were pulled into D3 to avoid.

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

**That answer is only honest if the pending change is additive, so additivity is a rule and not a
hope.** Schema change is expand-then-contract: a column is added, populated and read before anything
stops writing the one it replaces, and a breaking change is two releases rather than one. Without
the rule, the justification above prices the additive case and stays silent on the other, where a
host reports degraded — **which maps to success on the wire** — while every request touching the
missing column fails. A probe saying *take traffic, something needs attention* in front of a host
returning errors is worse than one that refuses, because nothing drains it. This is the schema
counterpart of the additive-only payload rule, it exists for the same reason, and it is what turns
an operator's restart-before-migrate from an outage into a delay.

**The dispatcher holds while this host has unapplied migrations, and consumes no attempts.** A
handler throwing because its table is not there yet is the **third** deploy hazard with exactly the
shape the deferral path was built for: the Type resolves, the payload deserializes, and the failure
is environmental and temporary. Left on the handler-throws path it increments attempts against a
backoff spanning roughly a day, so an operator who runs migrate mode the next morning may find the
entire pre-upgrade backlog already poisoned — the catastrophe the payload rule exists to prevent,
arriving through the one door deferral did not cover. Holding is cheaper than deferring here because
the condition is per-host rather than per-row: there is nothing to stamp and nothing to age, and the
backlog-age readiness condition already reports the wait.

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

**Migrate mode's exclusion is a provider-native lock — an advisory lock on PostgreSQL, an immediate
transaction on SQLite — not the lease.** It is the operation with the most destructive potential
per statement, the ways to invoke it twice at once are entirely ordinary — a unit restarting a run
that appeared to fail, an operator retrying in a second shell, both hosts' deploy scripts each
helpfully migrating — and neither provider serialises it on our behalf unasked. An earlier draft
guarded it with the lease, which cannot do this job twice over, and the design had already conceded
both halves without connecting them: the lease table is created by the very migration migrate mode
is about to apply, so on a fresh store there is nothing to acquire against and the guard is absent
on exactly the run competing deploy scripts are most likely to race — and a lease *expires*, so a
migrator stalled past five minutes is unfenced while its DDL still lands, against the least
idempotent work in the system, precisely the reliance the lease section forbids. The native lock is
a capability member — the two providers doing something different to produce the same observable
result, which is the membership rule verbatim — and connection-scoped is the property that closes
both holes at once: no table, so no bootstrap ordering; released by the provider when the holding
process dies, so no expiry window in which a stalled migrator and a fresh one run concurrently. A
second invocation fails fast with a named error. The lease keeps its one job, scheduled work, where
idempotency is required anyway.

**Immediate rather than exclusive, and one run is one transaction.** Immediate takes SQLite's single
write lock at begin, which is the exclusion this needs; exclusive's additional property is blocking
readers, and WAL — which this design mandates — exists so readers never block. Because the lock on
SQLite *is* that transaction, the run is atomic as a whole: a failure rolls back every migration the
run applied rather than leaving the successful ones behind, since committing per migration would
release the exclusion between them. **A partially-migrated store is therefore not a state migrate
mode can produce**, which is a stronger guarantee than the failure-mode table previously implied and
worth relying on deliberately rather than by accident.

**On SQLite a non-additive migration is a planned outage, and implying otherwise is the error.** A
table rebuild holds the single write lock for far longer than the busy-wait bound, so the web
process does not degrade during it — it fails. The degraded-and-serve branch covers a host that is
*behind* the schema, not a host serving *through* a rebuild. The operator stops the hosts, runs
migrate mode, starts them. Additive migrations need no such stop on either provider, which is most
of them, and is most of why the additivity rule above is worth having.

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
**System does:** waits, bounded, then fails the operation normally. Dispatch claims one row at a
time and prune deletes in bounded batches, so the worker holds the write lock briefly.
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

**Two configuration facts everything above depends on, stated rather than assumed:**

- **Write-ahead logging is required, not preferred.** A reader and a writer coexisting, and
  contention appearing as bounded waiting, are properties of WAL. In the rollback journal they are
  false — the worker's every poll then contends with the web process's *reads* as well as its
  writes, and the "nothing at the stated scale" line above stops holding. An analysis whose
  conclusions depend on a mode nobody wrote down is not an analysis.
- **A transaction that will write begins immediate, never deferred.** A deferred transaction that
  upgrades to a write after reading — which is the shape of both a claim and a mark — can take a
  busy condition that waiting *cannot* resolve, because no amount of waiting makes its read snapshot
  valid again. The bounded busy-wait assumes waiting helps; for this one class it does not, and the
  answer is to take the write lock at the start rather than to wait longer. Left unstated, it
  surfaces as intermittent immediate failures on the two paths that matter most, misdiagnosed as
  load.

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
**poisoned** — no longer retried, marked with a poison time, and counted by the poison-count
readiness condition. Not a metric: nothing in D3 publishes one, and a poisoned row that only a
collector could see is invisible on the installation this design centres.
**Partial failure:** one poisoned message must not stop the queue and **must not fail readiness**;
taking an installation out of rotation over one bad message is worse than the bad message.
**State left behind:** the row, queryable, with its last error, until its poison retention window
expires.

**A poisoned message has two exits, and Persistence exposes both as operations: redrive** — a
conditional update clearing the poison mark, attempts, first-deferred-at and the claim columns,
**and setting next-attempt-at to now** — a poisoned row still carries whatever next attempt the
final backoff wrote, hours ahead at the cap, and a redrive that left it would report success and
deliver nothing for hours; now rather than null keeps the redriven row's past-due age measured from
the recovery, not from an occurred-at days old — applied only if the row still
exists and is **still in the poisoned state as *Outbox row states* defines it** — poisoned-at set,
processed-at null — so racing the prune pass returns a clear "already pruned" rather than silent
nothing, and a row someone already discarded is not silently resurrected into one that can never
deliver — **and discard**, which sets processed-at, keeps poisoned-at, and appends the reason to the
last error. A discarded row carries both marks, and poisoned-at governs pruning: it prunes on the
longer poison window, so the forensic record outlives the decision to stop retrying, while the
poison *count* on readiness excludes it, because the decision it was demanding has been made. **Both
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
offline homelab this deployment targets. **Readiness is the whole mitigation, and no metric exists
beside it** — that draft's replacement kept a sentence saying the metric survived for anyone
exporting, which was never true of any code and would have sent an operator looking for a signal
nothing publishes. If a Platform instrument arrives later it reports this condition second, never
first.

### Telemetry collector unreachable, slow, or absent

**Detected by** the exporter, out of band. **System does:** uses the official bounded batch
processors and experimental in-memory retry, then **drops** when those bounds are exhausted; a
successful export is recovery. **User sees:** nothing — the request path is untouched by
construction. **State left behind:** none. The OpenTelemetry SDK exposes no supported exact
dropped-signal count or queue-transition hook in the pinned version, so Platform neither invents
one with a custom processor nor parses internal diagnostic strings.

Absent is not a failure: export is opt-in with console and file as defaults, no exporter starts and
no outbound connection is attempted. This is
[`observability.md`](../docs/docs/observability.md)'s commitment and §2's Game Engine constraint made
operational — **collection must never become a path by which a game can fail.**

### Mandatory log file unavailable, slow, or saturated

**Detected by** the Serilog file sink and asynchronous-sink inspector, out of band. **System does:**
drops rather than blocking and writes one emergency console diagnostic on entry to failure or
dropping and one on recovery. **User sees:** application work continue; the console diagnostic names
the degraded local sink. **State left behind:** an exact dropped-event count supplied by the
supported inspector. Failure to create or write the file never fails startup.

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

## What the operational surfaces expose

Identity is D5, so none of this is authentication. It is the smaller question D3 cannot postpone,
because D3 ships two surfaces that carry internal detail and one of them listens on a port: **what
goes in the body, and which interface serves it.** Left unstated, both shapes become public API the
moment a third party compiles against them — the audience the brief names — and D5 would have to
break them in order to secure them. A homelab is not a trusted network by declaration.

- **The worker probe binds loopback by default.** It exists for the operator and for CI, not for the
  network. The web host's probes follow whatever the product's own listener does, because they share
  it. The default port is one per box, not one per design: the brief's own environment puts two
  products on one server, so a second installation overrides it, and a collision fails startup with
  a named bind error citing the setting rather than falling back silently.
- **Full structured detail on loopback and in the development environment; a minimal body
  elsewhere.** The detail this design deliberately routes through readiness — pending migration
  names, peer instance identities, settings-fingerprint disagreement, backlog age, poison counts — is
  precisely what an operator needs and precisely what an attacker would enjoy. Only the body
  narrows; the status is the same either way, so nothing that consumes the probe programmatically
  changes behaviour.
- **Last error never crosses a wire in D3.** It is store-only. A deserialization failure's message
  plausibly embeds payload content, and no redaction rule can be trusted to find it reliably.
- **The error envelope carries a correlation identity and a stable error code, never exception text
  or payload content.** "Wholly derived from the failure and the ambient context" was a construction
  rule, not a disclosure rule, and the two were being read as one. The correlation identity is what
  ties the envelope to the log line that does carry the detail — which is the whole reason the
  design insisted on a single greppable value.

---

## Concurrency and ordering

**Startup is not concurrent within a process.** Module sort, options binding, registration and freeze
run in order on one thread, enforced by the host builder being single-threaded by construction.
Module order is deterministic across runs because ties in the topological sort break by name.

**Two processes are concurrent with each other, always.** Nothing may assume a single process — the
brief puts a worker alongside the web host permanently, not only during restarts.

**Requests are concurrent**, and that concurrency belongs to the runtime. Platform holds no shared
mutable per-request state: the ambient context is scoped and flows with the operation, so no two
requests can observe each other's tenant, principal, culture or correlation identity. Enforced
structurally — there is no static mutable state to race on.

**The health registry and module graph freeze when the host is built.** Registration after that
throws rather than mutating a structure concurrent readers are walking, which is what makes lock-free
probing correct.

**Outbox rows are claimed one at a time by a conditional update that stamps a claim, not by a
locking read.** A
locking read that skips locked rows exists in PostgreSQL and does not exist in SQLite, and the brief
made both production paths. A conditional update stamping holder and time works identically on both
and needs no dialect-specific correctness path. PostgreSQL may use its locking read underneath the
same interface as an optimisation; the portable path stays the one that defines the semantics. Every
dispatch-state mark is conditional the same way — a writer that lost its claim writes nothing — per
the transition rules in *Outbox row states*.

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
fail a request.** Enforced by four choices: both telemetry queues are bounded and drop rather than block;
probe endpoints do not share a pipeline with request handling; dispatch runs in a different process
entirely; and **every background write is bounded — dispatch claims and marks one row at a time
under a per-tick budget, prune deletes in bounded batches** — so that under SQLite the worker holds
the single write lock briefly.

Prune is named explicitly because it is the one background path that is not per-message. Bounding
"dispatch batches" alone left it uncovered, and its worst case is the largest of any of them.

The registration heartbeat runs in both hosts and is a single small upsert per interval, which is
why it needs no bound of its own — but it is a background write in the *web* process, and it takes
the same busy-wait and immediate-transaction discipline as every other write.

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
| Dispatch per-tick budget | 20 | no | Rows are claimed and marked one at a time; the budget bounds a tick, not a claim — see *Control flow* |
| Prune batch size | 500 | no | The same bound; larger because a delete is cheaper than a dispatch |
| Prune timer interval | 1 h, **not configurable** | no | The three windows it prunes against are hours to days wide, so nothing is served by pruning oftener — and unlike the dispatch interval, no latency depends on it. The one cadence here with no setting, because no deployment can want a different one without also wanting a different retention window |
| Prune drain rate | batch size per target per interval | — | **A consequence, stated because it is not obvious:** a tick issues one bounded delete per target, so the default clears 500 processed, 500 poisoned and 500 dead registrations an hour — 12 000 each per day |
| Dispatch timer interval | 5 s | no | The latency floor for async work; idle polling at this scale is noise |
| SQLite busy-wait bound | 5 s | no | Longer than any bounded batch holds the lock, shorter than a probe timeout |
| Graceful-shutdown drain window | 30 s | no | Longer than a typical handler, far shorter than the claim window that backstops it |
| Host heartbeat interval | 15 s | no | Liveness resolves at 45 s — fast enough to notice a dead peer, slow enough to survive contention |
| Peer-liveness threshold | 3 × heartbeat interval, derived | — | Derived so the two values cannot disagree; one missed beat cannot flap readiness |
| Registration retention window | 7 days | no | Dead rows are forensic breadcrumbs, then noise |
| Backlog-age threshold | 5 min | no | The claim window's twin: past due longer than this means dispatch is absent or wedged, not merely busy — measured past due, per *Outbox row states* |
| Pending-count threshold | 100 000 | no | Orders beyond any routine backlog at the stated scale, far short of the disk; it means days of worker absence, not a busy hour |
| Peer-absence grace | 60 s, rolling; **at least one heartbeat interval** | no | From first observed absence, on the observer's clock — a startup-scoped grace cannot cover the peer's restart; see the scoping paragraph under health. Floored at a heartbeat because a shorter grace elapses before the peer's next beat can land, degrading a host that is working |
| Worker probe port | 5100, **loopback** | no | Any fixed default beats a required setting for a probe surface; loopback because the probe is for the operator, not the network; one per box — a second installation on the same server overrides it |
| SQLite journal mode | **WAL, required** | — | A property of the file rather than of a host, so two hosts cannot disagree; listed because the contention analysis is false without it |
| Telemetry log directory | `<content-root>/logs` | no | Mandatory local evidence without requiring an observability stack; only the directory is configurable |
| Telemetry file rolling | daily and 100 MB | no | Bounds an individual file while preserving a predictable daily sequence |
| Telemetry file retention | 14 days and at most 31 files | no | Both age and count are bounded; whichever limit is reached first wins |
| Telemetry file queue | 10 000 events, drop rather than block | no | The Serilog async sink's supported bounded default, with an inspector that exposes exact drops |
| OTLP endpoint | absent; absolute HTTP/HTTPS when set | no | Absence means no exporter and no outbound path; one base URI configures all three standard signal paths |
| OTLP protocol and retry | HTTP/protobuf; bounded in-memory retry | no | One fixed deployment contract, with no disk queue and no request-path backpressure |
| Root trace sample ratio | 10% by trace id | no | Deterministic head sampling bounds routine HTTP volume; upstream and stored dispatch decisions remain authoritative |

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
PostgreSQL and covers any future cursor need at the millisecond granularity it actually has — the
tie caveat is recorded with the identity in *Data model* — which is what lets the sequence stay
demoted.
**Reversibility: cheap** — no rows exist yet, which is precisely why this happened now.

### Claim unit — one row, not a batch

**Chosen, reversing this document's earlier decision:** the dispatcher claims and marks one row at a
time, and the batch number becomes a per-tick budget. The earlier draft claimed a batch and
dispatched it serially, which silently redefined a claim's lifetime as the batch's duration rather
than the message's — so the five-minute window, sized against the slowest single handler, did not
cover what it was actually timing.
**Rejected — keep the batch claim and size the window against the batch.** Fewer statements, and one
round trip instead of twenty. Rejected because the window would then have to exceed the budget times
the slowest handler, a product of two numbers with no defensible joint value, and because
overshooting it lengthens every genuine dead-dispatcher stall.
**Rejected — keep the batch claim and dispatch it concurrently.** Keeps the window meaningful and is
faster. Rejected because it puts twenty concurrent mark-writes against SQLite's single write lock,
which is the contention the batch bound existed to limit, and it would require restating the whole
contention section for a throughput this design does not need at single-digit concurrent users.
**Reversibility: cheap** — it is a loop shape behind an interface, and no persisted shape changes.

### Pending backlog — reported, never refused

**Chosen:** pending rows are unbounded; the pending count joins the readiness surface with a
threshold, and no write is ever refused over backlog. Decided when a fifth review named the
unbounded state the two retention windows still left open.
**Rejected — backpressure at enqueue.** A hard cap past which enqueue throws. It protects the disk
by failing the domain write — the enqueue is inside the caller's transaction, so refusing the row
refuses the business operation — turning a worker outage into a web outage that surfaces as request
errors before readiness says anything.
**Rejected — a retention window for pending rows.** Symmetric with the other two windows and wrong:
an undispatched row is a committed write not yet delivered, and pruning it is silent message loss —
the failure the outbox exists to prevent, performed by the outbox.
**Reversibility: cheap** — a cap can be added later as a new named condition; a threshold number
can change without touching any persisted shape.

### Fan-out — one handler per Type, not many

**Chosen:** a second handler registration for a Type is a named startup failure; a product composes
inside its one handler.
**Rejected — many handlers behind one row, all-or-nothing.** The obvious shape, and the one an
event-handler contract invites. Rejected because every dispatch-state column is per row: one broken
subscriber burns the shared attempts budget, poisons delivery for the subscribers that already
succeeded, and leaves a last error that cannot name which of them is at fault.
**Rejected — a delivery row per resolved handler.** Actually correct, and it is the shape a mature
outbox converges on. Rejected as the largest schema and semantics addition available in D3, for a
fan-out no consumer has yet asked for — the extraction guard's argument exactly.
**Reversibility: asymmetric, which is the reason for the choice.** One-to-many later is additive;
many-to-one later breaks every consumer that had spread work across handlers.

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
is written into the section it governs rather than left here. A third adversarial review the same
day produced thirteen findings; every one is dispositioned in the section it touches, and the
numbers it demanded now live in *Settings inventory*.

**A fourth review, also 2026-08-03, produced thirteen more findings and twelve decisions** — two of
the findings were one omission wearing two faces, and are answered together by *Outbox row states*.
One was blocking: the web host's registration heartbeat had no package permitted to run it, so the
peer check that detects a split database could only ever have half-worked. The rest split between
things stated for one side of a comparison and not the other (the instant format, the identifier's
byte order), owners named for readers but not for writers (the operation scope, trace propagation
across the outbox), and rules the design relied on without writing down (additive schema change,
WAL, one handler per Type). Each is dispositioned where it belongs; **nothing from that review is
parked here**, which is the same discipline the third review's findings were held to.

**Deriving the contract then found three things this document had not determined**, all of them
undetectable by review because each was a gap rather than a contradiction — nothing here said
anything wrong about them, and nothing here said anything at all. What supplies an event's stable
Type name, what serializes a payload, and where the provider abstraction cuts were each named as a
requirement and left without a mechanism. All three are settled on 2026-08-03 and written into the
sections they govern — *Data model* for the first two, *Module boundaries* for the third. **That a
fourth adversarial review missed all three is the useful finding**: red-teaming a document tests
what it claims, and these were absences. Writing signatures is what found them, which is an argument
for deriving the contract early rather than a criticism of the reviews.

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

**A fifth review, also 2026-08-03, produced eighteen findings — five of them blocking — and four
forks settled by direct decision the same day.** The blocking five were mechanisms named but never
stated: the ambient transaction had no stated mechanism across per-module contexts; migrate mode's
guard could neither exist on a fresh store nor fence a stalled holder — one contradiction wearing
two findings; correlation survived exactly one hop of derived events; and the identifier's
mint-order test was unsatisfiable at the resolution version 7 actually has. The forks, each settled
on the recommendation: migrate-mode exclusion moved to a provider-native lock; the pending backlog
is reported and never refused; the converter extension point is cut; and a persistence-less host is
supported with its guarantees scoped and its probe body enumerating what is registered. Every
finding is dispositioned in the section it touches; nothing from this review is parked here. The
contract predates this revision and contradicts it — it must be re-derived before any slice runs.
