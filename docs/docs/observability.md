---
sidebar_position: 13
sidebar_label: Observability
---

> **Moved from the ecosystem specification staging tree**
> (`SubZeroDev/Specs/SubZeroDev.Platform/11-observability.md`), which names this repository as its
> destination. It was subsequently reconciled with the D3 design and contract; the exact D3
> settings and invariants are canonical in
> [the repository contract](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/20-contract.md). See
> [Platform Identity](platform-identity.md) for why it moved.

# Observability

Split from `11-observability-and-operations.md`. Execution-specific monitoring and administrative
operations moved to `SubZeroDev.Automator/11-operations.md`.

Platform provides the instrumentation; products decide what to instrument.

## Defaults

The standard registration call configures logs, traces and metrics. Serilog supplies mandatory
UTF-8 JSON Lines console and file logging through one redaction and buffering boundary, and the
official OpenTelemetry SDK provides instrumentation and optional OTLP export.

Nothing requires a product to write exporter code. A product that wants a collector sets the typed
`Platform:Telemetry:OtlpEndpoint` URI. When it is absent, no exporter starts and no outbound
connection is attempted. The only local-log setting is `Platform:Telemetry:LogDirectory`, which
defaults to `<content-root>/logs`.

## Logs

Structured, never interpolated strings. Required fields:

- timestamp, level, message template, and named properties
- service name, service version, deployment environment, and bounded host role
- correlation, tenant, culture, and actor when present in the ambient operation
- exception with stack, where present

Each role writes to console and to `<service>-<role>-.jsonl` through one 10 000-event asynchronous
buffer that drops rather than blocks. Files roll daily and at 100 MB and retain no more than 31
files and no file older than 14 days. A file failure cannot fail startup or application work. The
console reports one transition into failure or dropping and one recovery, while the supported queue
inspector supplies the exact dropped-event count.

### The rule that matters most

**Secrets never reach a log, at any level, including `trace`.** A fixed internal processor finds
non-empty secret configuration values from case-insensitive key segments such as `authorization`, `cookie`,
`password`, `secret`, `token`, `api-key`, `connection-string`, and `client-certificate`, and replaces
those values with `[REDACTED]` in structured properties, rendered messages, exceptions and nested
text. The same processor runs before local and OTLP output.

Platform never captures HTTP headers or bodies, event payloads, SQL parameter values, or connection
strings. Redaction is a backstop, not permission to log freely.

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

A trace spans a request end to end. Platform propagates W3C trace context across the outbox boundary.
Dispatch starts a new trace linked to the stored origin rather than continuing it, and preserves the
origin's sampled decision.

Span attributes must carry no secrets and no unbounded values — an artifact digest belongs on a span,
an artifact body does not.

Incoming traces honour the upstream sampled flag. New root HTTP traces use deterministic 10% trace-id
head sampling. Errors and slow traces are not automatically retained in-process; that requires
collector-side tail sampling. Persistence emits one provider-neutral child activity around each
unit-of-work transaction for both database providers, with provider and operation only and no SQL.

## Metrics

Platform supplies the primitives and standard host, HTTP, database, and background-job metrics.
Products define their own domain metrics.

**Cardinality is bounded by an allowlist per instrument.** Platform metrics may use host role, HTTP
method, route template rather than raw path or query, status, database provider, and closed outcome
or signal enums. Tenant, correlation, instance, message, event, and user identifiers are forbidden,
as is arbitrary tag pass-through.

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

**The provider choices are deliberate.** Serilog supplies both mandatory local sinks because the
.NET logging stack does not include a file provider, while the official OpenTelemetry packages own
OTLP traces, metrics, and logs. Both queues are bounded and non-blocking. The OpenTelemetry retry is in memory only; no
disk spool is part of D3.

**Sampling is protocol-led rather than product-led.** Upstream and persisted origin decisions are
honoured, while new HTTP roots use a fixed 10% trace-id ratio. Platform makes no special promise for
plugins, errors, or slow traces; those are workload or collector policies outside this repository.
