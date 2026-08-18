# The hosted Game Engine service

A JSON wire over one engine session store (G1), plus the .NET edge that fronts it. This
document is the fresh-clone story: everything here is a command that can be copied and run,
proven by CI running the same commands (`.github/workflows/build.yml`, `game-service` job).

## Prerequisites

- Node.js >= 24
- .NET SDK 10.0.x

## Install

```bash
cd workloads/game-service
npm install
```

## Start the workload

```bash
cd workloads/game-service
GAME_SERVICE_PORT=8080 npm start
```

Listens on `127.0.0.1:8080` by default (`GAME_SERVICE_HOST` overrides the host; an unset host
is loopback). `Ctrl+C` (`SIGINT`/`SIGTERM`) triggers a graceful shutdown. Liveness is
`GET /livez`; readiness, which only turns healthy once the surface is built and the listener is
bound, is `GET /readyz`.

## Build and start the edge

From the repository root, with the workload already running:

```bash
dotnet build workloads/game-edge/SubZeroDev.Platform.GameEdge --configuration Release

cd workloads/game-edge/SubZeroDev.Platform.GameEdge/bin/Release/net10.0
GameEdge__WorkloadBaseAddress=http://127.0.0.1:8080 \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
ASPNETCORE_ENVIRONMENT=Production \
  dotnet SubZeroDev.Platform.GameEdge.dll
```

Run from the built DLL's own directory, not the repository root — ASP.NET resolves
`appsettings.json` against the process's working directory, and `ForwardTimeout` and
`LivenessTimeout`'s defaults live only there.

Liveness is `GET /health/live` and never touches the workload; readiness is `GET /health/ready`
and probes the workload's own `/livez`. With the workload stopped, the edge stays up: liveness
still answers `200`, readiness answers `503` naming the failed check.

## Run the replay against each

From `workloads/game-service`, with dependencies installed (no server needs to be started by
hand first — each of these spawns and tears down its own workload, and, for the second, its own
edge in front of it):

```bash
npx vitest run tests/replay.test.ts       # Stage 1 -- runInProcess vs. the workload, over HTTP
npx vitest run tests/replay-edge.test.ts  # Stage 2 -- the same replay, addressed at the edge
```

Both compare against the committed golden transcript (`tests/fixtures/golden-transcript.json`)
byte for byte, and Stage 1 also compares the hosted run's shutdown dump against the in-process
run's snapshot, blob for blob. Neither test's replay commands take a target address as an
external input — the harness starts what it needs and reports the same pass/fail a hand-run
session against the two manually-started processes above would.

## Run the two-instance contention proof

Two copies of the workload against one shared PostgreSQL database — the way a real deployment
scales out — proving the same guarantee holds across processes as within one (S6): two players'
simultaneous actions against one session never both win.

From `workloads/game-service`, with dependencies installed:

```bash
docker compose up -d
npx vitest run tests/contention-two-instances.test.ts
```

The test spawns both instances itself, against a fresh schema it creates and drops — no server
needs to be started by hand, and the command takes no target address as an external input.

## Regenerate and publish the contract package

Nothing in this repository authors, edits or regenerates the contract (S3.15) — the row set, the
schemas and the status mapping have exactly one home,
[`SubZeroDev.ServiceContract`](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract).
In a checkout of that repository:

```bash
npm install
npm run build   # tsc, then scripts/generate-contract.ts -- every gate in 20-contract.md runs
                # inside it, and no artifact is written if any gate fails (S2.2)
npm pack        # produces the versioned tarball this repository vendors under
                # workloads/game-service/vendor/
```

That repository's own suite is what proves these commands, not this one's CI: `npm run build`
run twice over an unchanged row set and an unchanged engine version produces a byte-identical
`dist/contract.json` (S2.10), and the published package resolves under its own semantic version
(S2.9). This repository consumes the result unmodified through `loadPublishedContract()`
(`workloads/game-service/vendor/README.md`); there is no copy of the contract here for this
repository's CI to regenerate or compare against.

## Handover notes

Two facts the next effort (G2, G3) inherits rather than discovers:

- **Single-instance is unenforced.** Nothing in G1 stops a second workload instance from being
  started. Each instance holds its own in-memory session store, so a session created against one
  is unknown to the other — it presents as `unknown_session`, not as corruption. What stands in
  for enforcement today is that G1's whole operations story is two processes started by hand; the
  real guard is G2's durable store.
- **The MCP surface bypasses the edge.** The edge routes the JSON wire only; an MCP caller
  addresses the workload directly. That is harmless in G1, where reachability is trusted-local
  and no principal exists — it stops being harmless at G3, where authorization is enforced at the
  edge and a surface that bypasses the edge bypasses authorization.

One more finding, not a gap but worth stating before G2 needs it: the engine writes a mutated
session or save blob to its own in-memory record **before** writing through to the persistence
port supplied to it. G1's `SessionPersistence` is map-backed and total, so it never observes this
ordering — but a persistence implementation that can fail (G2's) will leave the in-memory record
ahead of the store on a write failure, and that is the question G2's persistence design has to
answer.
