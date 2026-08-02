---
sidebar_position: 10
sidebar_label: Events and Notifications
---

> **Moved from the ecosystem specification staging tree**
> (`SubZeroDev/Specs/SubZeroDev.Platform/07-events-and-notifications.md`), which names this repository as its
> destination. Content is unchanged; only this provenance note and the front matter
> were added. See [Platform Identity](platform-identity.md) for why it moved.

# Events and Notifications

Split from `07-events-notifications-artifacts.md`. The execution event catalogue and artifacts moved
to `SubZeroDev.Automator/07-execution-events-and-artifacts.md`, because those are Automator's
domain. What remains here is the infrastructure any product on Platform reuses.

Platform provides the envelope, the bus, the outbox, and the notification channels. It defines no
event types of its own beyond lifecycle plumbing — the products name their own events.

## Event envelope

Every event carries the same envelope. Products vary only in `type` and `data`.

```json
{
  "schemaVersion": "1.0.0",
  "eventId": "01J8Z3K9QW7X2N4M6P8R0T5V7Y",
  "type": "Automator.Execution.Completed",
  "occurredAt": "2026-07-28T10:02:13.482Z",
  "correlationId": "…",
  "causationId": "…",
  "tenantId": null,
  "actorId": null,
  "source": "automator.control-plane",
  "dataSchemaVersion": "1.0.0",
  "data": {}
}
```

Field rules that are easy to get wrong and expensive to change later:

- **`eventId` is sortable.** ULID or UUIDv7 rather than UUIDv4, so an event log can be ordered and
  paged by ID without a secondary index on time. Retrofitting this after a log exists means
  rewriting every stored identifier.
- **`occurredAt` is when the fact happened**, not when the event was published or handled. A
  handler that needs publish time gets it from the transport.
- **`correlationId` groups a whole causal chain**; `causationId` names the single event that
  directly caused this one. They are different, and collapsing them loses the ability to
  reconstruct a tree rather than a set.
- **`dataSchemaVersion` is separate from `schemaVersion`.** The envelope and the payload evolve
  independently, and a payload change should not force an envelope version bump.
- **`tenantId` is present from the first schema**, nullable, defaulted to the implicit single tenant.
  See the tenancy note in `10-tenancy-billing-licensing.md`: adding the column later is easy, adding
  isolation later is a correctness migration on every table.

## Naming convention

`<Product>.<Aggregate>.<PastTenseVerb>`

```text
Automator.Execution.Started
Automator.Execution.Completed
Automator.Workflow.Succeeded
Platform.Notification.Delivered
```

The original set used two conventions — dotted namespaced names in `07` and bare PascalCase in `06`.
Dotted wins: it namespaces by product, which matters as soon as two products publish to the same
bus, and it sorts usefully in a log.

Events are named in the past tense because they are facts that already happened. An event named as a
command is a sign the design has slipped into RPC.

## Events are not log messages

A log line is for a human reading a failure. An event is a typed fact other code subscribes to.

The practical test: if adding a subscriber would be a reasonable thing to do, it is an event. If it
only exists so someone can grep it, it is a log line. Publishing every log line as an event produces
a bus nobody can afford to subscribe to.

## Delivery

| Category    | Scope                                 | Transport                | Guarantee        |
| ----------- | ------------------------------------- | ------------------------ | ---------------- |
| Domain      | Within one aggregate's transaction    | In-process, synchronous  | Same transaction |
| Application | Within one process                    | In-process, asynchronous | At-most-once     |
| Integration | Crosses a process or product boundary | Outbox, then transport   | At-least-once    |

**Integration events use the transactional outbox.** The event row is written in the same
transaction as the state change, and a relay publishes it afterwards. Without this, a crash between
"state committed" and "event published" silently loses the event — the classic dual-write failure,
and the one that produces bugs nobody can reproduce.

At-least-once delivery means **subscribers must be idempotent**. Platform provides a deduplication
helper keyed on `eventId`, but the guarantee belongs to the subscriber; a helper cannot make a
non-idempotent side effect safe.

Distributed buses are future providers behind the same abstraction. In-process plus outbox is the
Phase One implementation.

## Ordering

No global ordering is promised. Ordering holds **per correlation chain** and only when the transport
provides it.

Consumers that need order derive it from `causationId`, not from arrival. Anything that genuinely
requires strict global ordering should not be built on events.

## Notifications

Automator emits events. Platform notification handlers translate selected events into notifications
for people. **Plugins never send notifications** — a plugin that emails someone has taken on
orchestration that belongs to the host.

Notification model:

- recipient
- channel
- template
- variables
- priority
- deduplication key
- correlation ID
- tenant
- scheduling metadata

Channels: email, Discord, Slack, Teams, webhooks, SMS, push, in-app. Providers are independently
replaceable.

### Routing

Rules may consider event type, severity, workflow, plugin, tenant, user preference, environment,
schedule, and deduplication key.

### Deduplication and storms

**The failure mode worth designing against:** a schedule fires every five minutes against a broken
dependency, and every run notifies. By morning there are 288 identical messages and the recipient has
muted the channel — so the next real failure is invisible.

Defaults:

- A deduplication key suppresses repeats within a window. Default key: event type plus subject.
- Repeated identical notifications collapse into one with an occurrence count.
- A recovery notification is sent when a previously failing subject succeeds, so a suppressed alert
  does not leave the recipient assuming it is still broken.

### Delivery failure

Notification delivery failing must never fail the work that triggered it. A Discord outage does not
fail a sync.

Delivery is retried with backoff, and a permanently failed notification is recorded and surfaced in
health rather than retried forever.

## Decisions on previously open points

**Retention.** Append-only within a 90-day window, then compacted to terminal-state summaries. Stated
once, in `SubZeroDev.Automator/07-execution-events-and-artifacts.md`, since Automator owns the
execution history that dominates the volume.

**The transport after in-process need not be chosen now.** The outbox is transport-agnostic by
design — that is most of its value. When the choice arrives, prefer PostgreSQL `LISTEN`/`NOTIFY` or a
durable queue over a broker: the deployment targets are a homelab and a single server, and a broker
adds an operational component heavier than the problem it solves at that scale.

**Notification preferences are per tenant and per user, with user overriding tenant.** The tenant sets
a floor — which categories are on at all — and a user tunes within it. One level alone fails
predictably: tenant-only cannot silence an individual, user-only cannot enforce an organizational
policy.
