---
sidebar_position: 8
sidebar_label: ADR-008 Observability Startup Failure
---

# ADR-008: Standalone Observability Fails Fast on Invalid OTLP Configuration

## Status

Accepted

## Context

`AddPlatformObservability` has two configuration paths. A Platform host first binds and validates
`PlatformOptions`; a present `Platform:Telemetry:OtlpEndpoint` that is not an absolute `http` or
`https` URI aborts host construction with a named `ConfigurationError.InvalidSetting`. A consumer
using Observability without the rest of Platform reads the same setting directly, but currently
converts the same invalid value to `null`, which means no exporter starts.

The two paths therefore give opposite meanings to one configuration value. A typo is fatal when the
Hosting package is present and silently disables export when it is absent.

Observability cannot throw Hosting's `PlatformStartupException`: Hosting references Observability,
and the reverse reference would make the package graph cyclic. Persistence already pays this cost for
the same reason through `PersistenceStartupException`.

## Decision

**Standalone Observability validates the OTLP endpoint on the same terms as the Platform-host path and
fails fast when a present value is invalid.**

1. An absent or whitespace endpoint still means no exporter starts.
2. A present endpoint is valid only when it is an absolute `http` or `https` URI.
3. An invalid value throws a public `ObservabilityStartupException` from the Observability package.
   The exception carries the existing `PlatformError` contract, specifically
   `ConfigurationError.InvalidSetting` naming `Platform:Telemetry:OtlpEndpoint` and its constraint.
4. Hosting keeps its existing `PlatformStartupException` wrapper. The wrapper differs across package
   boundaries; the carried error and its meaning do not.
5. This decision applies only while configuration is being accepted. Failures from a validly
   configured file or OTLP exporter remain absorbed by the existing bounded, non-blocking pipelines
   and never propagate to application work.

## Consequences

- A standalone consumer can distinguish invalid configuration by a stable public exception type and
  inspect the same enumerable error it would receive through Hosting.
- A consumer catching every Platform startup abort now has one package-local type per package that can
  abort independently: Hosting, Persistence and Observability. This duplication is the cost of the
  acyclic package graph.
- Adding the exception expands Observability's public 0.x surface. Generated API-reference and package
  consumer gates must include it.
- No deployment, exporter retry, throughput or runtime buffering policy changes. The accepted endpoint
  set and exporter behavior are unchanged; only the malformed standalone case stops being silent.

## Alternatives considered

**Reference Hosting and throw `PlatformStartupException`.** One startup type for consumers to catch.
Rejected because Hosting already references Observability, so the reverse edge creates a cycle and
makes a lower-level package depend on its composer.

**Move a universal startup exception into Core and migrate Hosting and Persistence to it.** Removes the
package-local wrappers and gives every package one type. Rejected because it turns a narrow additive
fix into breaking changes across three published packages and gives Core knowledge of failures it does
not raise.

**Throw an internal or general-purpose exception.** Avoids a public API addition. Rejected because the
module boundary would then expose a bare exception with no stable enumerable error, contrary to the
existing startup contracts, and a consumer could not handle the package's startup refusal by type.

**Keep returning `null`.** Preserves compatibility and treats malformed as disabled. Rejected because
it makes a typo indistinguishable from deliberate absence and leaves the same setting with opposite
meaning depending on which registration entry point is used.
