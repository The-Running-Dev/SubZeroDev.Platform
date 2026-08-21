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
`ReadinessTimeout`'s defaults live only there.

Liveness is `GET /health/live` and never touches the workload; readiness is `GET /health/ready`
and probes the workload's own `/readyz` — so a workload that is alive but unable to serve (its
store unreachable) makes the edge report not-ready too. With the workload stopped, the edge stays
up: liveness still answers `200`, readiness answers `503` naming the failed check.

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

## Provision the durable store and bring the schema to head

Everything below this point needs a real PostgreSQL database — the committed compose file
provisions one, pinned to `UTF8` encoding and an explicit initdb locale, and starts nothing else
(S3.15). `npm run migrate` is the fresh-clone entry point for `migrateToHead` (`20-contract.md`,
"Migrations — workload") — the same call every proof below makes for its own schema, run here
against the default (`public`) one for an operator's own use:

```bash
cd workloads/game-service
docker compose up -d
npm run migrate
```

Run twice in a row, the second run is a no-op (S3.16). `GAME_SERVICE_DB_SCHEMA` targets a schema
other than `public`; `GAME_SERVICE_DB_CONNECTION_STRING` overrides the connection string the
compose file's own defaults resolve to.

## Start the workload against the durable store

`npm run migrate` above is an operator's own explicit control over when a schema moves to head;
the workload does not need it run first. `GAME_SERVICE_STORAGE=durable` plus
`GAME_SERVICE_DB_CONNECTION_STRING` are enough — unlike `npm run migrate`'s own connection string,
this one has no default, so a durable start never connects to a database it was not explicitly
told about. The process brings its own schema to head before it reports ready, the same call the
command above makes, retried under a startup backoff if the database is not reachable yet (S12):

```bash
cd workloads/game-service
docker compose up -d
GAME_SERVICE_PORT=8080 \
GAME_SERVICE_STORAGE=durable \
GAME_SERVICE_DB_CONNECTION_STRING=postgresql://game_service:game_service@127.0.0.1:5432/game_service \
  npm start
```

Missing `GAME_SERVICE_DB_CONNECTION_STRING` while `GAME_SERVICE_STORAGE=durable` is set fails
startup immediately, naming it — the process never degrades silently to the in-memory profile.
`GAME_SERVICE_DB_SCHEMA` targets a schema other than `public`. Readiness (`GET /readyz`) turns
healthy once the schema is at head and the store answers; started against an unreachable database
it stays live and not-ready, and becomes ready without a restart once the database is reachable.

## Run the one-instance contention proof

One process against the durable store, composed the same way `compose()`'s durable branch is
(S6): two players' simultaneous actions against one session stop silently overwriting each
other — exactly one succeeds, and the other is told plainly to re-read and decide.

From `workloads/game-service`, with dependencies installed and the store provisioned:

```bash
npx vitest run tests/contention-one-instance.test.ts
```

The test composes its own durable-backed instance against a fresh schema it creates and drops —
no server needs to be started by hand.

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

## Run the durable replay

The byte-identity proof G1 established, held to hold when the game is stored in a real database
instead of memory (S8): the same ten-step fixture, run once in-process and once against a freshly
created durable schema, produces byte-identical ordered blob sets and matches the committed
golden transcript.

From `workloads/game-service`, with dependencies installed and the store provisioned:

```bash
npx vitest run tests/durable-replay.test.ts
```

The test creates and drops its own schema and runs at the production lifecycle defaults, so no
step observes a session or save as expired mid-run.

## Run the port-conformance suite

The identical assertion set, run once against the workload's own map-backed in-memory
implementation and once against the durable store, so the durable store's conformance to
`SessionPersistence` and `ProfileStore` is checked rather than assumed (S9).

From `workloads/game-service`, with dependencies installed and the store provisioned:

```bash
npx vitest run tests/conformance.test.ts
```

Both targets pass the shared assertion set except `profiles.save`, the one method the suite
declares conformant conditionally rather than identically — the durable store's merge is
additive where the engine's in-memory one replaces, and what stands in for identical behaviour
there is a property asserted directly against the engine's own caller.

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

## Handover notes (G2)

Three facts every proof in this effort depends on:

- **No brief conflict remains.** Both this note once carried are closed by amendments to
  [`design/g2/00-brief.md`](../../design/g2/00-brief.md) (`20-contract.md`, Unresolved 2): the tenant
  conflict on 2026-08-12, which now permits the implicit tenant in every key and statement, and
  the save-lifecycle conflict on 2026-08-20, which admits saves to the lifecycle scope on their
  own clock. The `save` table's 365-day absolute lifecycle, its `expires_at` column and the sweep
  that hard-deletes past the retention horizon all stand as built.
- **The engine has ratified `concurrent_modification` under the name and brand this contract
  assumes** (`SESSION_PERSISTENCE_CONFLICT`, `SessionPersistenceConflict`), and the vendored
  engine `0.8.0` ships both — so every proof here rests on a published artifact rather than on a
  pending pull request.
- **A session's idle TTL advances only on an accepted write, never on a read.** `expires_at` is
  recomputed from the database clock on every accepted write and left untouched by every read, so
  a session read continuously for its whole TTL still expires (`20-contract.md`, Open question 8;
  invariant 59). Refreshing on read was rejected because it would put every query operation inside
  the compare-and-swap's blast radius.
