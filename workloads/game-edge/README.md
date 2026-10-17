# The game edge

`SubZeroDev.Platform.GameEdge`: the Platform-hosted front door for one hosted workload. It
accepts HTTP, forwards it to the workload's base address, and returns what the workload
answered. It is composed by Platform's one standard registration call
(`AddPlatformWebHost`) plus a catch-all forwarding route and a readiness check; there is no
`AddGameEdge`. The end-to-end local run, with a real workload and the replay, is in
[`../game-service/README.md`](../game-service/README.md#build-and-start-the-edge).

Despite the name, the edge never learns what a game is. Its extraction into a reusable
Platform package, and the second workload that justifies it, are
[ADR-007](../../docs/docs/adr/ADR-007-second-hosted-workload.md); the workload-versus-edge
boundary is [`engine-hosting-contract.md`](../../docs/docs/engine-hosting-contract.md) §2.

## What it does not do

Each of these is a decision, not an omission. The rule lives in the linked document; this
file does not restate it.

| The edge does not | Ruled by |
|---|---|
| Authorize anything, or hold any per-session state, connection affinity or cache | [G1 design](../../design/g1/10-design.md) ("The .NET edge"); invariant 41 in the [G1 contract](../../design/g1/20-contract.md) |
| Know the workload's REST paths: it forwards every path without consulting the contract, so an unknown operation reaches the workload and is answered there | [G1 decisions](../../design/g1/90-decisions.md), 2026-08-08 "The edge forwards by prefix and does not consult the contract" |
| Retry, or record an attempt: a retry of an action whose outcome is unknown is a second action | [G1 contract](../../design/g1/20-contract.md), `EdgeError` and invariant 43 |
| Carry the caller's credentials to the workload, or the workload's cookies back | [#339](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/339), [#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340) |
| Proxy a WebSocket upgrade | [Streaming decision, 2026-10-08](../../design/state/decisions/2026-10-08-the-edge-streams-per-route-with-its-own-first-byte-budget-and-aborts-a-broken-stream.md) |

## Configuration

Bound from the `GameEdge` section. `appsettings.json` ships `ForwardTimeout` and
`ReadinessTimeout`; everything else is yours to supply. The `Platform` section in the same
file is Platform's own and is not documented here.

| Key | Type | Required | Default (shipped) | Rule |
|---|---|---|---|---|
| `GameEdge:WorkloadBaseAddress` | absolute URI | **yes** | none, deliberately | Must be an absolute URI. Where the workload listens; used by the forwarder and the readiness probe only. |
| `GameEdge:ForwardTimeout` | `TimeSpan` | yes | `00:00:10` | Greater than zero. Budget for a buffered forward, send through the last body byte. Exceeding it is a `504`. |
| `GameEdge:ReadinessTimeout` | `TimeSpan` | yes | `00:00:05` | Greater than zero. Bounds the readiness probe of the workload and is the check's own timeout. |
| `GameEdge:StreamingRoutes` | list | no | empty (nothing streams) | See below. |
| `GameEdge:StreamingRoutes:<i>:PathPrefix` | string | yes, per entry | none | Begins with `/`; not `/` alone; no trailing `/`; no `?` or `#`. Matched ordinal (case counts) and on segment boundaries: `/console/stream` covers `/console/stream` and `/console/stream/42`, not `/console/streamer`. No prefix may equal, or lie under, another entry's. |
| `GameEdge:StreamingRoutes:<i>:FirstByteTimeout` | `TimeSpan` | yes, per entry | none | Greater than zero and no greater than `ForwardTimeout`. |

**Invalid configuration fails startup**, not the first request. The host throws
`InvalidOperationException` before it listens:

| Breach | Message |
|---|---|
| `WorkloadBaseAddress` missing or not absolute, or either timeout zero or negative | `Configuration section 'GameEdge' is required: WorkloadBaseAddress must be an absolute URI, and ForwardTimeout and ReadinessTimeout must both be positive.` |
| Any streamed-route rule | `Configuration section 'GameEdge:StreamingRoutes' is invalid: ` followed by one sentence per breach, each naming its full key (for example `'GameEdge:StreamingRoutes:0:PathPrefix' must begin with '/'.`) |

The streamed-route check collects every breach, so one start reports them all. The same keys
work as environment variables with `__` for `:`, for instance
`GameEdge__StreamingRoutes__0__PathPrefix=/console/stream` and
`GameEdge__StreamingRoutes__0__FirstByteTimeout=00:00:02`.

## What crosses the edge

The route is a catch-all (`/{**catchAll}`) for every method and path. Platform's own probe
middleware answers `/health/live` and `/health/ready` before it, so those two paths never
reach the workload.

| Direction | Crosses | Notes |
|---|---|---|
| Caller to workload | Method, path, query string | Verbatim. |
| | Body | Read whole into memory, forwarded verbatim. |
| | `Content-Type` | Only when there is a body. |
| | `traceparent`, `tracestate` | Written by the edge from the ambient operation scope (`tracestate` only when present), not copied from the caller's header. See invariant 47 in the [G1 contract](../../design/g1/20-contract.md). |
| Workload to caller | Status code | Whatever the workload sent. |
| | `Content-Type` | When present. |
| | Body | Byte for byte (buffered), or as it arrives (streamed). |

**Everything else is dropped in both directions**: `Cookie` and `Set-Cookie`, `Authorization`,
`Origin`, CORS headers, `Cache-Control` and the rest.

- **Cookies.** The forwarder's HTTP handler keeps no cookie jar (`UseCookies = false`). It used
  to share one across every caller, replaying one player's session onto the next
  ([#339](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/339), fixed). Test:
  `A_cookie_the_workload_sets_for_one_caller_is_never_sent_on_anothers_request`.
- **Other headers.** Which caller headers should cross is the question of who owns a caller's
  credential at the edge, and G3 rules on it
  ([#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340), parked). The edge
  does not change before then.

**Signed-in flows therefore do not work through the edge.** A workload that keeps sessions in a
cookie, signs players in over OIDC, or relies on browser cross-origin headers sees every caller
as anonymous. Anonymous reads work. Such routes cannot be served through the edge until G3
rules ([#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340)).

**MCP is forwarded like any other path.** G1's design routes only the JSON wire through the edge
and names MCP as a stated gap ([G1 design](../../design/g1/10-design.md), "The MCP surface is not
reachable through the edge"), but the catch-all forwards `/mcp/*` too. The edge does nothing
MCP-specific; whether it should cover MCP is open for G3.

## Probes

| Endpoint | Meaning | Touches the workload |
|---|---|---|
| `GET /health/live` | The edge process is up. | Never. Platform rejects a liveness check that declares an external dependency. |
| `GET /health/ready` | The workload is reachable and ready. | Yes: one `GET /readyz` on the workload per check. |

The readiness check is named `game-workload`, `Required`, and is unhealthy unless `/readyz`
answers a 2xx within `ReadinessTimeout`. Any other status, a refused connection, or a timeout is
unhealthy, so a workload that is alive but cannot serve (its store unreachable) makes the edge
not ready. Readiness answers `200` when healthy and `503` otherwise, with a JSON body
`{ "status", "checks": [ { "name", "status", ... } ] }` that names the failed check; the
failure detail (`workload_unreachable`) is included only for loopback callers. The edge starts
and stays alive whether or not the workload is up. The probe never calls a game operation.
Rule: [G1 decisions](../../design/g1/90-decisions.md), 2026-08-08 "The edge is not ready when
the workload is unreachable".

## Errors

The edge produces exactly two errors; every other status a caller sees came from the workload.

| Status | `code` | When |
|---|---|---|
| `503` | `workload_unreachable` | The connection fails, or the workload dies while its response is read, on a buffered route; or fails before its headers on a streamed route. |
| `504` | `workload_timeout` | A buffered forward exceeds `ForwardTimeout`; or a streamed route's headers do not arrive within its `FirstByteTimeout`. |

Body, `Content-Type: application/json; charset=utf-8`:

```json
{ "code": "workload_unreachable", "correlation": "0af7651916cd43dd8448eb211c80319c" }
```

`correlation` is the request's trace id (32 lowercase hex). No exception text or request
content is ever in the body. Neither error is retryable and the edge makes exactly one attempt:
after a `503` or `504` the outcome of an action is unknown, so re-read with a query operation
rather than resubmit ([`EdgeError`](../../design/g1/20-contract.md)). The codes are named in
[G1 decisions](../../design/g1/90-decisions.md), 2026-08-10, and 2026-08-10 "`workload_unreachable`
covers a forward that fails after the headers arrive".

## Streaming routes

Off by default: every route is buffered, the response read whole under `ForwardTimeout`. A
route listed under `GameEdge:StreamingRoutes` is relayed as the workload writes it, each piece
flushed to the caller as it is read (server-sent events, for example).

- **First-byte timeout.** `FirstByteTimeout` runs from the send to the workload's response
  *headers*, the edge's commit point. It is the only edge deadline on a streamed route: after the
  headers there is none, and a stream may outlive `ForwardTimeout`. Before the headers the edge
  answers for itself (`503` or `504` above).
- **Failure after the headers.** The edge has already committed the workload's status, so it
  cannot answer. It aborts the response with no terminating chunk and no byte of its own, so the
  caller's read fails instead of ending cleanly. An access log will show `200`; use the
  telemetry below.
- **Caller disconnect.** Ends the relay and releases the workload connection. It is not an error.
- **Retries.** None.

Rule: [streaming decision, 2026-10-08](../../design/state/decisions/2026-10-08-the-edge-streams-per-route-with-its-own-first-byte-budget-and-aborts-a-broken-stream.md).

## Telemetry

Beyond what Platform's host emits (correlation, request and probe records), the edge records one
thing: a streamed response the workload cut short after its headers. A caller who leaves is
neither logged nor counted.

| Signal | Name | Detail |
|---|---|---|
| Log, `Warning` | event `1101` `EdgeStreamAborted`, category `SubZeroDev.Platform.GameEdge` | The configured prefix, correlation, code (`workload_unreachable`), bytes forwarded, milliseconds since the headers, exception type name. |
| Counter | `subzerodev.edge.stream.aborts` (`{abort}`) on meter `SubZeroDev.Platform` | Tags `route` (the configured prefix) and `code`. |

Neither carries the request's path, query or body, nor an exception message. The framework's own
request and outbound-call logs do at `Information`; if a query can carry a token, see the note in
[`../game-service/README.md`](../game-service/README.md#build-and-start-the-edge).

## Run it locally

Prerequisite: .NET SDK 10.0.x. From the repository root:

```bash
dotnet build workloads/game-edge/SubZeroDev.Platform.GameEdge --configuration Release

cd workloads/game-edge/SubZeroDev.Platform.GameEdge/bin/Release/net10.0
GameEdge__WorkloadBaseAddress=http://127.0.0.1:8080 \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
ASPNETCORE_ENVIRONMENT=Production \
  dotnet SubZeroDev.Platform.GameEdge.dll
```

Run from the built directory: `appsettings.json` resolves against the working directory, and
the two timeouts' defaults live only there. Without `GameEdge__WorkloadBaseAddress` the process
exits at startup with the message above. Any other key is set the same way, for example
`GameEdge__ForwardTimeout=00:00:30`. Starting a workload to point it at is in the
[game-service walkthrough](../game-service/README.md#start-the-workload).

Tests (no workload needed; each starts its own fake one):

```bash
dotnet test workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests
```

## Putting a workload behind it

The edge asks four things of a workload:

1. **Plain HTTP at `WorkloadBaseAddress`.** The edge speaks to the workload over a socket; it
   references none of its code, and no project under `src/` may reference `workloads/`.
2. **`GET /readyz`** answering a 2xx exactly when the workload can serve. Its liveness path is
   never called.
3. **Its own authorization boundary.** The edge authorizes nothing and passes no credential.
   Until G3 rules (#340), a workload cannot expect a caller's cookie or `Authorization` header.
4. **Safe to call exactly once.** The edge never retries, and a `503` or `504` leaves the
   outcome unknown, so the workload's callers must be able to re-read state.

A workload that streams must have its prefixes listed under `GameEdge:StreamingRoutes`, and
its callers must treat a response that ends early as a failure rather than a completion.
