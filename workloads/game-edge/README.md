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
| Forward a caller's own `Authorization`, hop-by-hop headers or `X-Forwarded-*`, or answer a CORS preflight itself | [Credential policy](../../design/10-design.md) (*Control flow* §6); [#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340) |
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
| `GameEdge:CredentialMode` | `Terminate` or `PassThrough` | **yes** | none, deliberately | Who answers for the caller's credential. See [Operating a deployment](#operating-a-deployment). |
| `GameEdge:Handoff:Issuer`, `:Audience`, `:KeyId`, `:SigningKeyPath`, `:Lifetime` | strings, and a `TimeSpan` | all, in `Terminate`; refused in `PassThrough` | none | The assertion the edge signs for the workload: its `iss` and `aud`, the `kid` of the key the workload verifies with, the P-256 private key's PEM path, and how long each assertion lives (at least one second, and no more than the workload's `GAME_SERVICE_HANDOFF_MAX_LIFETIME_SECONDS`). |
| `GameEdge:StreamingRoutes` | list | no | empty (nothing streams) | See below. |
| `GameEdge:StreamingRoutes:<i>:PathPrefix` | string | yes, per entry | none | Begins with `/`; not `/` alone; no trailing `/`; no `?` or `#`. Matched ordinal (case counts) and on segment boundaries: `/console/stream` covers `/console/stream` and `/console/stream/42`, not `/console/streamer`. No prefix may equal, or lie under, another entry's. |
| `GameEdge:StreamingRoutes:<i>:FirstByteTimeout` | `TimeSpan` | yes, per entry | none | Greater than zero and no greater than `ForwardTimeout`. |
| `GameEdge:TrustedProxies` | list of IP addresses | no | empty (nobody is trusted) | Addresses of reverse proxies in front of the edge. When the connecting address is listed, that proxy's `X-Forwarded-For`, `-Proto` and `-Host` reach the workload; otherwise the edge's own values from the connection do. Each entry must parse as an IP address (`GameEdge:TrustedProxies:<i>`). |
| `GameEdge:AbuseLimits:PermitCount` | integer | no | `60` | At least 1. Requests one caller may make in a window. When the `AbuseLimits` section is present, both fields are required. |
| `GameEdge:AbuseLimits:Window` | `TimeSpan` | no | `00:01:00` | At least one second. A caller is the account when one is established and its client address otherwise: the connection's, or the last `X-Forwarded-For` entry when the connection is a listed trusted proxy. The request after the budget is a `429` with `Retry-After` and is never forwarded. Counted per edge instance, in memory, reset on restart. |

**Invalid configuration fails startup**, not the first request. The host throws
`InvalidOperationException` before it listens:

| Breach | Message |
|---|---|
| `WorkloadBaseAddress` missing or not absolute, or either timeout zero or negative | `Configuration section 'GameEdge' is required: WorkloadBaseAddress must be an absolute URI, and ForwardTimeout and ReadinessTimeout must both be positive.` |
| `CredentialMode` missing or not a mode, `Handoff` missing in `Terminate` or present in `PassThrough`, or a `Handoff` field missing | `Configuration section 'GameEdge' is invalid: ` followed by one sentence per breach, each naming its full key (for example `'GameEdge:CredentialMode' is required and must be 'Terminate' or 'PassThrough'.`) |
| In `Terminate`, a provider under `Platform:Identity:Bearer` with a `Discovery` address or without `SigningKeys` | `Configuration section 'Platform:Identity:Bearer' is invalid for 'Terminate' mode: ` followed by one sentence per breach |
| A `TrustedProxies` entry that is not an IP address | `Configuration section 'GameEdge:TrustedProxies' is invalid: 'GameEdge:TrustedProxies:<i>' must be an IP address.` |
| An `AbuseLimits` field missing, malformed or out of range | `Configuration section 'GameEdge:AbuseLimits' is invalid: ` followed by one sentence per breach, each naming its full key (for example `'GameEdge:AbuseLimits:PermitCount' is required and must be a whole number of at least one.`) |
| Any streamed-route rule | `Configuration section 'GameEdge:StreamingRoutes' is invalid: ` followed by one sentence per breach, each naming its full key (for example `'GameEdge:StreamingRoutes:0:PathPrefix' must begin with '/'.`) |

The streamed-route check collects every breach, so one start reports them all. The same keys
work as environment variables with `__` for `:`, for instance
`GameEdge__StreamingRoutes__0__PathPrefix=/console/stream` and
`GameEdge__StreamingRoutes__0__FirstByteTimeout=00:00:02`.

## What crosses the edge

The route is a catch-all (`/{**catchAll}`) for every method and path. Platform's own probe
middleware answers `/health/live` and `/health/ready` before it, so those two paths never
reach the workload.

The edge copies **none** of a caller's headers wholesale. Only the names below cross, and which of
them do depends on `GameEdge:CredentialMode`. The same rules apply on the buffered path and on a
streamed route. The source of truth is [`10-design.md`](../../design/10-design.md), *Control flow* §6.

| Direction | Header | `Terminate` (game-service) | `PassThrough` (Adventures) |
|---|---|---|---|
| Caller to workload | Method, path, query string, body | Verbatim. | Verbatim. |
| | `Content-Type` | Only when there is a body. | Only when there is a body. |
| | `traceparent`, `tracestate` | Written by the edge from the ambient operation scope. | Same. |
| | `Authorization` | Not forwarded. The edge validated it. | Not forwarded. |
| | `SubZeroDev-Principal-Assertion` | The caller's is stripped; the edge sets its own, signed for this request. | Stripped; never set. |
| | `Cookie`, `Origin` | Not forwarded. | Forwarded exactly as sent, together or alone. |
| | `Access-Control-Request-Method`, `-Headers` | Not forwarded. | Forwarded, so the workload can answer a preflight. |
| | `X-Forwarded-For`, `-Proto`, `-Host` | The caller's are dropped; the edge sets them from the connection, or takes a listed `TrustedProxies` proxy's. | Same. |
| | `Forwarded`, hop-by-hop headers, anything unnamed | Not forwarded. | Not forwarded. |
| Workload to caller | Status, `Content-Type`, body | As the workload sent them (body byte for byte, or as it arrives when streamed). | Same. |
| | `Location` | Forwarded; a redirect is never followed. | Forwarded; a redirect is never followed. |
| | `Cache-Control`, `Vary` | Forwarded. | Forwarded. |
| | `Set-Cookie` | Dropped. | Forwarded, every value, byte for byte. |
| | `Access-Control-*` | Dropped; the edge produces none. | Forwarded unchanged; the edge adds none and answers no preflight. |
| | Anything else | Dropped. | Dropped. |

- **Cookies.** The forwarder's HTTP handler keeps no cookie jar (`UseCookies = false`), so a cookie
  the workload sets for one caller is never replayed onto another's request
  ([#339](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/339), fixed). Test:
  `A_cookie_the_workload_sets_for_one_caller_is_never_sent_on_anothers_request`.
- **A terminate-mode preflight is anonymous.** It carries no bearer, so the edge answers it `401`
  like any anonymous request and forwards nothing.
- **Tests.** `HeaderPolicyContract` (the rows both modes share), `PassThroughHeaderTests` and
  `TerminateHeaderTests`, each over a buffered route and a streamed one.

**MCP reaches game-service only through the edge, under the same ownership as HTTP.** `/mcp/*` is
forwarded by the same catch-all, so in `Terminate` mode a tool call needs a credential the edge
established, is refused with `CredentialRequired` otherwise, is counted against the same per-account
budget, and reaches the workload carrying the edge's assertion. The workload refuses any request that
carries no valid assertion, MCP included (`401 assertion_refused`), so a client that addresses game-service
directly gets nothing: keep the workload's port reachable by the edge alone. The edge does nothing
MCP-specific.

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

The edge produces exactly two errors on the forwarding path; every other status a caller sees came from the
workload, from Platform's bearer pipeline (`401 CredentialRejected`, `Terminate` only), or is one of the two
refusals described after the body below, which are answered before anything is forwarded.

| Status | `code` | When |
|---|---|---|
| `503` | `workload_unreachable` | The connection fails, or the workload dies while its response is read, on a buffered route; or fails before its headers on a streamed route. |
| `504` | `workload_timeout` | A buffered forward exceeds `ForwardTimeout`; or a streamed route's headers do not arrive within its `FirstByteTimeout`. |

Body, `Content-Type: application/json; charset=utf-8`:

```json
{ "code": "workload_unreachable", "correlation": "0af7651916cd43dd8448eb211c80319c" }
```

The two refusals have the same body shape. `Terminate` mode answers a caller with no credential `401` with
code `CredentialRequired`, and a caller over its budget gets `429` with code `RateLimited` and a
`Retry-After` of whole seconds. Neither reaches the workload.

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

## Operating a deployment

The topology is a reverse proxy that keeps TLS, one edge per workload, and the workload behind it. The
proxy's route for the workload's host is the only thing a cutover or a rollback changes.

### The two modes

| | `Terminate` (game-service) | `PassThrough` (Adventures) |
|---|---|---|
| Set | `GameEdge__CredentialMode=Terminate` | `GameEdge__CredentialMode=PassThrough` |
| Who authenticates | The edge, from a bearer token, against the issuer's pinned keys. | The workload, from its own cookie. |
| Also required | `GameEdge__Handoff__*`, and `Platform__Identity__Bearer__<provider>__*` pinning the issuer; the `AuthenticatedTransport` composition profile (`Platform:CompositionProfile`). | Nothing. `Handoff` must be absent. |
| Anonymous caller | `401 CredentialRequired`, nothing forwarded. | Forwarded; the workload decides. |
| Workload needs | `GAME_SERVICE_HANDOFF_KEYS_DIR`, `_ISSUER` and `_AUDIENCE` (and optionally `_CLOCK_TOLERANCE_SECONDS`, default 30, and `_MAX_LIFETIME_SECONDS`, default 60). | No edge setting. Adventures needs no handoff. |

### The handoff key and its rotation

The edge signs each request's assertion (ES256, P-256) with the private key at
`GameEdge:Handoff:SigningKeyPath`; game-service verifies it with the public key named
`<KeyId>.pem` in `GAME_SERVICE_HANDOFF_KEYS_DIR`, and several may be present at once. Rotate in this order,
because the workload must know a key before the edge signs with it:

1. Put the new public key, `<new key id>.pem`, in game-service's key directory and restart it.
2. Point the edge at the new pair (`KeyId`, `SigningKeyPath`) and restart it.
3. After one `Lifetime` has passed, remove `<old key id>.pem`.

Switching the edge first makes every request `401 assertion_refused` until the workload catches up.
`Handoff:Lifetime` must not exceed the workload's `GAME_SERVICE_HANDOFF_MAX_LIFETIME_SECONDS`, or the
workload refuses every assertion. There is no setting that turns the check off.

### The issuer, and what the edge fetches

The edge fetches nothing from the issuer. It holds the issuer's public keys itself, as a JWKS document in
`Platform:Identity:Bearer:<provider>:SigningKeys`, alongside `Issuer`, `Audiences` and `Algorithms`, and it
refuses to start with a `Discovery` address on any provider. To rotate an issuer key, add the new key to
`SigningKeys` (keep the old one) and redeploy the edge **before the issuer signs with the new key**. A
token signed by a key the edge has not been given is `401 CredentialRejected`, for every player, until it
has. Remove the old key once no token signed with it can still be presented. Treat the set as read at
startup: change it by redeploying the edge.

### Limits

`GameEdge:AbuseLimits` bounds each caller (the account when established, otherwise the client address).
The counter is per edge instance and in memory: running more than one instance multiplies the budget by the
instance count, and a restart resets it. Behind a reverse proxy, list the proxy's address in
`GameEdge:TrustedProxies`. Without it every player arrives from the proxy's address and shares one budget.

### Verifying a deployment

Through the proxy, at the host players use:

1. `GET /health/live` is `200`, and `GET /health/ready` is `200`: the edge is up and the workload answers
   `/readyz`.
2. `Terminate`: a request with no `Authorization` is `401 CredentialRequired`; one with a bad token is
   `401 CredentialRejected`; one with a valid token for the registered client returns the workload's answer.
   A request sent to game-service directly, without the assertion, is `401 assertion_refused`.
3. `PassThrough`: an API read returns Adventures' own answer, and a sign-in sets its session cookie
   unchanged.

**CI is not this check.** The `g3-composition` job proves the code against a test issuer, throwaway keys and
loopback addresses. It touches none of your host, issuer, registered client, keys or proxy, and a green run
is not a statement about them. Run the list above against the live deployment.

### Rolling back Adventures

Adventures was reachable directly before the edge, and rollback restores exactly that:

1. In the reverse proxy, point the API host's route back at Adventures' own address, the one it used before
   the cutover. The host, TLS and Adventures itself do not change, so players keep their cookies and the OIDC
   `redirect_uri` stays valid.
2. Reload the proxy, then `GET /api/me` at the public host: `200`, and a signed-in player is still signed in.
3. Confirm the edge sees no new requests. It holds no state, so it can be stopped or left running.

The procedure is rehearsed in `workloads/adventures-proof/tests/cutover-rollback.test.ts` against a route
table standing in for the proxy: a session started before the cutover survives it, a sign-in completes on
the cutover route, the edge is taken down, and the switch back restores the signed-in player and a write.

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
3. **Its own authorization boundary.** The edge never forwards an `Authorization` header. In
   `PassThrough` the workload receives the caller's `Cookie` and `Origin` and answers for them; in
   `Terminate` it receives only the signed assertion and verifies it.
4. **Safe to call exactly once.** The edge never retries, and a `503` or `504` leaves the
   outcome unknown, so the workload's callers must be able to re-read state.

A workload that streams must have its prefixes listed under `GameEdge:StreamingRoutes`, and
its callers must treat a response that ends early as a failure rather than a completion.
