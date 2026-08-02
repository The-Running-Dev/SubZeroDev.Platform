---
sidebar_position: 12
sidebar_label: Observability
---

> **Moved from the ecosystem specification staging tree**
> (`SubZeroDev/Specs/SubZeroDev.Platform/11-observability.md`), which names this repository as its
> destination. Content is unchanged; only this provenance note and the front matter
> were added. See [Platform Identity](platform-identity.md) for why it moved.

# Observability

Split from `11-observability-and-operations.md`. Execution-specific monitoring and administrative
operations moved to `SubZeroDev.Automator/11-operations.md`.

Platform provides the instrumentation; products decide what to instrument.

## Defaults

OpenTelemetry for logs, traces, and metrics, with service name and version, correlation IDs, and
resource attributes configured once at host startup.

Nothing here should require a product to write exporter configuration. A product that wants
OTLP to a collector sets a connection string; a local developer gets console output and no
collector.

## Logs

Structured, never interpolated strings. Required fields:

- timestamp, level, message template, and named properties
- correlation ID
- tenant, where tenancy is enabled
- actor, where a request context exists
- exception with stack, where present

### The rule that matters most

**Secrets never reach a log, at any level, including `trace`.** Redaction is configured centrally on
known secret field names, authorization headers, request bodies for auth endpoints, and nested
exception causes.

Redaction is a backstop, not permission to log freely. It catches the field named `token`; it cannot
catch a token concatenated into a message string.

### Level guidance

| Level   | Use                                                            |
| ------- | -------------------------------------------------------------- |
| `error` | The operation failed and someone should look                   |
| `warn`  | Degraded but handled — a retry succeeded, a fallback was taken |
| `info`  | A significant state change a human would want in a timeline    |
| `debug` | Developer detail, off in production                            |
| `trace` | Wire-level detail, off by default and dangerous around secrets |

`info` is the level most often abused. If it fires per item rather than per operation, it is `debug`.

## Traces

A trace spans a request end to end. Platform propagates W3C trace context across process boundaries,
which is what lets a control-plane request and a plugin container appear on the same trace.

Span attributes must carry no secrets and no unbounded values — an artifact digest belongs on a span,
an artifact body does not.

## Metrics

Platform supplies the primitives and standard host, HTTP, database, and background-job metrics.
Products define their own domain metrics.

Two rules that prevent an expensive mistake:

- **Cardinality is bounded.** Never label a metric with an execution ID, a correlation ID, an
  artifact digest, or anything else unbounded. This is the single most common way to make a metrics
  backend fall over, and it is difficult to undo once dashboards depend on the labels.
- **Tenant labels only where safe.** Tenant count is bounded, so tenant is an acceptable label;
  a tenant _name_ may be sensitive, so the label is the ID.

## Health

Platform provides liveness and readiness endpoints and a check registry that modules contribute to.

The distinction is worth keeping precise, because getting it backwards causes outages:

- **Liveness** answers "should this process be restarted". It must not depend on external services —
  a database outage that fails liveness turns into a restart loop that makes recovery slower.
- **Readiness** answers "should traffic be routed here". It may depend on external services.

Checks report degraded as well as healthy and unhealthy, so a working system with a failing optional
provider is distinguishable from a broken one.

## Configuration diagnostics

Platform exposes which configuration source supplied each effective value, with secret values
redacted and only their source shown.

This is small and pays for itself the first time a setting is overridden somewhere nobody expects.

## Decisions on previously open points

**Exporting is opt-in.** A self-hosted installation logs to console and file by default and needs no
collector to start. Requiring one would make an observability stack a prerequisite for running a
homelab tool, which is a disproportionate ask; setting an OTLP endpoint turns it on.

**Sampling is split by workload, because the two have opposite characteristics.** Plugin executions
are low-volume, long-running, and are the main thing anyone traces — they are **always sampled**.
HTTP and background-job traces are high-volume and individually uninteresting, so they are
ratio-sampled at 10%, with errors and traces exceeding a latency threshold always kept.

Uniform sampling would be the mistake here: at any ratio low enough to control HTTP volume, the
executions worth diagnosing would be discarded most of the time.
