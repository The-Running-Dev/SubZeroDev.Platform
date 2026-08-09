# Slices — one session, over the wire, then the edge (G1)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a signature the contract
does not carry, it stops and asks for a contract amendment rather than inventing one.

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code
that satisfies them. **Three repositories are in scope**, because G1's boundary crosses all three —
`SubZeroDev.GameEngine` (the seam and the coverage column), `SubZeroDev.ServiceContract` (the
contract package and its generator), and this one (the workload, the edge, the proof). A slice
states which repository it lands in; a slice spanning two states what each side receives.

**The ordering is the design's risk ordering.** The effort exists to answer one question — whether a
game played over a wire is the same game, byte for byte — and [S5](#s5--the-byte-identity-proof)
answers it. S1 to S4 exist only to reach S5, and each is the smallest thing that gets there: without
the engine seam three operations return a fresh random id every run, without the contract package
there is nothing to build a surface from, without the wire there is nothing to replay, and without
the shutdown snapshot there is nothing to compare. Everything after S5 is the second surface, the
second hop, and the evidence.

**Each heading carries a `**Status:**` line** — `shipped`, `in progress`, or `queued` — as the first
line of its body, never inside the heading itself: Docusaurus derives anchors from heading text, and
a marker there would break every inbound link. `build/Test-SliceStatusMarkers.ps1` reads this file in
CI and enforces exactly one `in progress` slice while any slice is `queued`, so **S1 carries the
marker from the moment this document exists** — it means *current*, not *underway*. A slice sets its
own marker to `shipped` in the same change that satisfies it and sets the next one to `in progress`.
This is a second place done-ness is recorded, alongside the slice's tracking issue; where they
disagree, say so per `AGENTS.md` *Tracking work* rather than editing either to match.

## Decisions that must be taken before the slice that needs them starts

Neither the design nor the contract settles these, and none is a slice's to settle silently.
[`10-design.md`](10-design.md)'s open questions and [`20-contract.md`](20-contract.md#unresolved)'s
unresolved items are listed here against the first slice that cannot proceed without an answer. Each
gets a `90-decisions.md` entry from that slice.

| Question | Needed before |
|---|---|
| ~~Design Q1 — which engine version G1 pins~~ | **Resolved 2026-08-08.** S1 cuts from engine `main`; G1 pins ten operations. See [`90-decisions.md`](90-decisions.md) |
| Design Q3 — whether ServiceContract's "depends on nothing" governs the published artifact or also the generator's build inputs | **S2**. It is a cross-repository rule edit |
| Contract Unresolved 3 — the JSON Schema dialect the generated set declares | **S2**. It fixes which validator the workload can use |
| Contract Unresolved 4 — the contract package's published name and registry | **S2**. It is the first line of the workload's dependency declaration |
| Contract Unresolved 2 — the workload's generic internal-failure code | **S3**. It is a wire-visible string |
| Design Q5 — whether the shutdown serialization dump is inside the permanent non-goal | **S4**. If it is, S4 does not exist and S5's comparison A narrows |
| ~~Design Q2 — the shape of the hosted column, and whether a blank row is acceptable there~~ | **Resolved 2026-08-08 by Q1.** It is the fifth column, with ten ticks and no blank row |
| Design Q4 — whether comparison B is in-process against hosted, or two hosted runs | **S5**. One golden transcript satisfies both readings; what the criterion is understood to assert is not the same question |
| Contract Unresolved 2 — the edge's unreachable and timed-out codes | **S7**. Two more wire-visible strings |
| Design Q6 — whether the edge covers the MCP surface | **S7**. Leaving it open hands G3 a surface that bypasses the authorization point |

---

## S1 — Session and save ids the host can supply
**Status:** shipped

Delivers: anyone composing the game engine can hand it the thing that names new sessions and saves,
so a run played twice from the same starting point names them identically both times. Anyone who
hands it nothing sees exactly what they see today — new names, unpredictable on purpose.

**This is the slice that unblocks the whole effort**, and it lands in another repository first
because a published engine version is what everything after it pins. Three of the table's operations
return a freshly-minted random id in every run; until the host can supply that minting, the proof
this effort exists to build cannot be written.

Repository: **`SubZeroDev.GameEngine`**.

Touches:
- **The session layer's composition root** — `RecordIdSource` declared and accepted as an optional
  member alongside the clock, persistence and profile ports
- **The session store** — the module-local id minting, which now calls the supplied source when one
  is present
- **The engine's own suite** — the assertions below
- **The engine's release** — a published version carrying the seam in its type declarations, **cut
  from the engine's `main`**, so it carries the already-merged `previewAction` and G1 pins a
  ten-operation engine (decided 2026-08-08; see [`90-decisions.md`](90-decisions.md))

Depends on: none.

Acceptance:
- **S1.1** Composing the session layer with no `RecordIdSource`, two `createSession` calls return two
  different session ids and two `saveGame` calls return two different save ids, each in the format
  the engine mints today. Present behaviour is unchanged by omission.
- **S1.2** Composing with a `RecordIdSource` whose `newSessionId` and `newSaveId` count from zero on
  independent counters, two separate runs of the identical call sequence return the identical session
  ids and the identical save ids, in the identical order.
- **S1.3** For one arc, one seed and one choice list, `serialize()` produces the same bytes whether a
  `RecordIdSource` was supplied or not. The seam cannot change game state, and this is the assertion
  that says so rather than the argument that says so.
- **S1.4** `newSessionId` is called exactly once per session created and `newSaveId` exactly once per
  save written — asserted with a counting source. No other engine path consumes the source.
- **S1.5** A released engine version carries `RecordIdSource` in its published type declarations, and
  a consumer resolving that version from the registry can supply one without reaching into the
  engine's internals. It is cut from `main`, so its exported `SessionStore` declares **ten**
  operations — the count S2's arity gate is asserted against.

Out of scope: **authoring** `previewAction` or any other change to engine behaviour — the brief's
non-goal carries one carve-out and this is it; the operation ships in this release because it is
already merged, and S1 neither writes nor modifies it. Widening the existing `IdSource`, which
governs `gameId` and `seed` and is a different category; the coverage-checklist column, which is S5's
PR because S5 is what produces its evidence; exporting a counting `RecordIdSource` implementation,
which the workload composes for itself in S4.

---

## S2 — The contract has a home, and a build that refuses to publish a lie
**Status:** shipped

Delivers: the hosted service's contract stops being a document and becomes an artifact. One reviewed
table of operations produces a versioned package anything can consume, and the build that produces it
refuses when the table and the engine disagree — so a contract that describes an engine nobody is
running cannot be published in the first place.

Repository: **`SubZeroDev.ServiceContract`**, with one file moved out of this repository's
`docs/docs/` — ADR-005 already moved it out of `SubZeroDev.GameEngine` — and inbound links updated
in this repository and the engine's in the same change.

Touches:
- **The authored row set** — `AuthoredRow` values, one per exported `SessionStore` method: the
  operation id, the store method, the MCP tool name, the narrowings, the reachable errors
- **The status mapping** — `StatusMapping`, `StatusMappingEntry`, covering every declared
  `SessionStoreErrorCode` plus the three transport codes
- **The generator** — `generate`, `GenerationInput`, and every gate: arity, error coverage, closed
  response schemas, no envelope-reachable schema, no determinism profile in a row, no unknown
  narrowed field, no duplicate id or tool name, `httpPath` equal to `operation`
- **The emitted artifact** — `ContractPackage`, `OperationRow`, `JsonSchemaDocument` with its `$id`
  and dialect, `SchemaRef`
- **`GenerationError`** — all nine variants
- **The publish path** — the package published under its own semantic version, resolvable by a
  consumer that pins it
- **`mcp-tool-contract.md`** — moved here from this repository's `docs/docs/`, with that copy
  retired and its inbound links (`docs/docs/index.md`, `docs/docs/game-engine-as-a-service.md`)
  repointed; the engine's `design/10-design.md` link updated so the generated `09-clients.md`
  points at its new home

Depends on: S1.

Acceptance:
- **S2.1** `generate` over the authored rows emits a `ContractPackage` whose `operations` count equals
  the exported `SessionStore`'s method count at the pinned engine version, and whose `engineVersion`
  equals the version the schemas were projected from.
- **S2.2** Deleting one row fails generation with `ArityMismatch` naming the uncovered method; adding
  a row whose `storeMethod` the engine does not declare fails with `ArityMismatch` naming the row.
  **No artifact is written on either** — the output directory is byte-identical before and after.
- **S2.3** Deleting one entry from the status mapping fails with `ErrorCodeUncovered` naming the code.
  Adding an entry for a code that is neither a declared engine code nor a member of
  `TransportErrorCode` fails the same gate.
- **S2.4** A response schema emitted without `additionalProperties: false` at any object level fails
  with `ResponseSchemaOpen` naming the schema; a response shape resolving to the engine's envelope
  type fails with `EnvelopeReachable`. **These two are the permanent non-goal's static gate.** A
  request schema open at any object level fails with `RequestSchemaOpen` on the same terms — an open
  request schema would make a request narrowing reversible from the wire.
- **S2.5** A `NarrowedField` naming a member the engine's declaration does not have fails with
  `NarrowingUnknownField` naming the row and the member; two rows sharing an `OperationId` or an
  `McpToolName` fail with `DuplicateOperationId` naming both; a row carrying the determinism profile
  in any member fails with `DeterminismProfileInRow`.
- **S2.6** In the emitted artifact, every row's `httpPath` equals its `operation` verbatim, and every
  `requestShape` and `responseShape` resolves to a document present in the same artifact's `schemas`.
- **S2.7** With the engine package already restored and all outbound network blocked, generation
  completes and every emitted `$id` and `$ref` is left unresolved — **nothing is fetched**. With the
  engine package unresolvable, generation fails with `EngineResolutionFailed` naming the package and
  the registry, and does not retry.
- **S2.8** Every emitted schema declares the same `$schema` dialect, and the validator chosen for the
  workload loads all of them and rejects a payload with an added member on a closed response schema —
  proving `additionalProperties: false` composes the way the gate assumes it does.
- **S2.9** The package is published under a semantic version and a consumer pinning that version
  resolves it and reads its `operations` without any other input. Republishing the same version is
  refused by the registry rather than overwriting.
- **S2.10** Running the generator twice over an unchanged row set and an unchanged engine produces
  byte-identical artifacts.
- **S2.11** `mcp-tool-contract.md` lives in `SubZeroDev.ServiceContract` and nowhere else: the
  Platform copy at `docs/docs/mcp-tool-contract.md` is retired, Platform's inbound links
  (`docs/docs/index.md`, `docs/docs/game-engine-as-a-service.md`) point at the new home, and the
  engine's generated `09-clients.md` links to it there. No dead link is left behind, and each
  repository's edits are one change set.

Out of scope: the workload consuming the package — that is S3, and the criterion "the workload reads
the contract from ServiceContract, not a local copy" is asserted there; a hand-written schema of any
kind, however temporary; a .NET distribution of the artifact, which the edge deliberately does not
need and G3 pays for; a second `wireVersion`; anything that dereferences a `$id` at build time to
"check the URL works".

---

## S3 — The game is playable over HTTP, and asking wrongly has an answer
**Status:** shipped

Delivers: an operator can start the game service and play a whole game over the network — start a
session, make choices, ask what the scene looks like, save it and load it back. Every operation the
engine offers is reachable, and every way of asking for one incorrectly comes back with a stated,
specific answer instead of a shrug.

**This slice is deliberately not split into "the happy path" and "the errors".** Shipping the routes
without their defined answers produces a wire whose behaviour under misuse is undefined, which the
brief names as the thing that is not a wire — and G2's persistence and G3's principals inherit
whatever is chosen here. It is one request/response cycle, and it lands whole.

Repository: **this one**, under `workloads/game-service/`.

Touches:
- **Contract module** — `loadContract`, `findRow`, `statusFor`, `ContractLoadError`
- **Composition** — `compose`, `ComposedWorkload`, `CompositionError`, the engine instance, the
  content registry, the map-backed `SessionPersistence` and `ProfileStore`
- **Dispatch** — `createDispatcher`, `Dispatcher`, `DispatchOutcome`, the `SessionStoreError` catch
  at the boundary
- **HTTP surface** — `buildHttpSurface`, `HttpSurface`, `validateRequest`, `validateResponse`,
  `canonicalEncode`, `ValidatedArguments`, `WireRequest`, `WireResponse`, `WireErrorBody`,
  `SurfaceBuildError`, `EncodingError`, `ValidationFailure`
- **Probes and lifecycle** — `startWorkload`, `WorkloadProcess`, `ProbeSurface`, `ProbeResult`,
  `WorkloadConfiguration`, `ListenEndpoint`, `StartupError`
- **The result type** — `Outcome<T, E>`
- **build/** — the gate failing any project under `src/` or `samples/` that references `workloads/`
- **CI** — the workload's suite, from a fresh clone

Depends on: S2.

Acceptance:
- **S3.1** With the service started, `POST /v1/create-session` with a valid body returns `200` and a
  body whose object members are ascending by code unit with no insignificant whitespace. A subsequent
  `submit-action` against the returned session id returns `200`, and a query operation returns the
  scene that action produced. **The whole table is routed** — every row has a live path.
- **S3.2** Every response body validates against its row's closed response schema. A row whose
  handler returns an added member fails validation and the request becomes a `500`; **the unvalidated
  body is never returned.**
- **S3.3** `POST /v2/create-session` returns `404` with `{"code":"unsupported_version",...}`;
  `POST /v1/not-an-operation` returns `404` with `{"code":"unknown_operation",...}`. Same status,
  different code, and no other member in either body.
- **S3.4** A body missing a required member returns `400` with `malformed_payload`, and **the store
  is never called** — asserted against a store whose invocations are recorded. The validation detail
  does not appear in the response.
- **S3.5** `submit-action` against an unknown session id returns `404` carrying `unknown_session`
  **verbatim**; an unknown save returns `404` with `unknown_save`; an unknown campaign returns `404`
  with `unknown_campaign`; `invalid_state`, `unknown_kind`, `save_requires_migration` and
  `migration_failed` each return `409` carrying their own code. No code is paraphrased or normalized.
- **S3.6** An action the game rejects — an unknown action id, an unmet requirement — returns **`200`**
  carrying the store's unsuccessful result. No game verdict produces a 4xx.
- **S3.7** Every response, success or failure, carries the correlation. A request with a well-formed
  `traceparent` carries that trace-id as its correlation; a request with `traceparent:
  not-a-traceparent` returns the same `200` with a fresh 32-hex correlation and **never** a `400`.
- **S3.8** A handler that throws returns `500` whose body has exactly two members, `code` and
  `correlation`. No exception text, no stack trace, no payload content anywhere in the response.
- **S3.9** Started against a contract whose `engineVersion` differs from the resolved engine
  package's, the process exits non-zero with `EngineVersionMismatch` naming both versions, and a
  connection attempt to the configured port is refused — **the listener never bound.**
- **S3.10** A contract whose rows derive two identical path segments fails startup with
  `DuplicateRoute` naming both rows, before binding; a row referencing a `SchemaRef` absent from the
  artifact's schema set fails with `MissingSchema` naming the row and the reference.
- **S3.11** Liveness returns healthy without touching the store. Readiness returns healthy only after
  both surface construction and the listener bind have completed.
- **S3.12** With no listen host configured, the service is reachable on loopback and unreachable on
  the machine's other addresses.
- **S3.13** With `otlpEndpoint` null, no exporter is constructed and no outbound connection is
  attempted — asserted with outbound network blocked, the service still serving.
- **S3.14** A project under `src/` or `samples/` referencing anything under `workloads/` fails the
  build with a named error. The gate is exercised by introducing the reference deliberately and
  observing the failure, then removing it.
- **S3.15** The workload's suite runs in CI from a fresh clone, and the workload resolves the contract
  package from the registry — **there is no copy of the contract in this repository.**

Out of scope: the MCP surface (S6) — one surface at a time, and the second one is what proves the
table is the only source; the determinism profile, the counting sources and the dump (S4); the replay
fixture and either comparison (S5); trace export and the collector (S8); anything the edge does (S7);
compare-and-swap, eviction, quotas and expiry, each a binding non-goal.

---

## S4 — The service can be asked to record what the game looked like when it stopped
**Status:** in progress

Delivers: an operator can start the service in a mode that plays the same way every time and, when it
is stopped cleanly, writes to a file of their choosing exactly what the game had become — and nothing
about the service or the machine it ran on. Started the ordinary way, it writes nothing at all and
there is nowhere for it to write to.

Repository: **this one**.

Touches:
- **Configuration** — `DeterminismProfile`, `DefaultDeterminismProfile`, `ReplayDeterminismProfile`
- **Composition** — the counting `IdSource`, a counting implementation of the engine's
  `RecordIdSource`, the fixed clock, `StoreSerializationHandle`, `StoredBlob`,
  `StoreSerializationSnapshot`, `writeDeterminismDump`, `DeterminismDump`, `CompositionError`
- **Lifecycle** — `shutdown` writing the dump before the listener stops accepting, `ShutdownError`
- **Harness support** — `readDeterminismDump`, `DumpReadError`
- **A dependency-direction test** — the HTTP surface's module graph against
  `StoreSerializationHandle`

Depends on: S3.

Acceptance:
- **S4.1** Started with the replay profile and a dump path, played through two sessions and one save,
  then shut down gracefully: the file at that path is canonical JSON carrying `sessions` and `saves`
  keyed by id, members ascending by code unit, each value the engine's canonical serialization.
- **S4.2** That file contains **no** `createdAt`, `updatedAt`, attempt counter, `audience`,
  `profileId` or `savedAtSeq` — no host-owned record field of any kind, asserted member by member
  against a run that produced non-default values for each.
- **S4.3** Started with the default profile, no file is written at any path and the configuration
  carries no dump path to write to. `writeDeterminismDump` cannot be called with the default profile
  because it does not typecheck, and a test asserts the absence of the file after a graceful shutdown.
- **S4.4** Under the replay profile, two separate runs of the identical request sequence return the
  identical session ids and the identical save ids. Under the default profile, the same sequence
  returns different ids in the two runs.
- **S4.5** Under the replay profile, the clock reports `fixedInstant` on every call for the whole
  run, asserted at the composition seam.
- **S4.6** With the dump path unwritable, shutdown exits non-zero with `DumpWriteFailed` naming the
  path, and **no empty or partial file is left behind** for a later reader to mistake for an empty
  store.
- **S4.7** `readDeterminismDump` over an absent file returns `DumpAbsent` and over a truncated file
  returns `DumpMalformed`. Neither returns an empty snapshot.
- **S4.8** A request carrying a member named for the determinism profile is rejected as
  `malformed_payload` and the store is never called — request schemas are closed, so there is no
  path from a caller to the profile. A run containing that rejected request produces the identical
  ids and the identical dump as the same run without it.
- **S4.9** Adding an import of `StoreSerializationHandle` to the HTTP surface's module graph fails the
  dependency-direction test. The failure is observed deliberately before the import is removed.

Out of scope: the fixture, the golden transcript and either comparison (S5); any endpoint, route,
tool or header that returns or names the serialization — the one permanently non-negotiable non-goal;
comparing the dump against anything, which is the next slice's whole subject.

---

## S5 — The byte-identity proof
**Status:** queued

Delivers: the question this effort exists to answer gets an answer that anyone can re-run — a game
played across the network is the same game, byte for byte, as the same game played in-process. And
the check is known to be checking something, because it has been deliberately made to fail.

**This is the effort's pivotal slice.** Every slice before it is a prerequisite; every slice after it
is a second surface, a second hop, or the evidence around it.

Repository: **this one**, plus a PR opened against **`SubZeroDev.GameEngine`**.

Touches:
- **The committed fixture** — `ReplayFixture`, `ReplayStep`, with literal ids, covering every row
- **The committed golden transcript** — the canonically-encoded responses, in order
- **The harness** — `runInProcess`, `runHosted`, `HostedTarget`, `RunResult`, `Transcript`,
  `compareSerializations`, `compareTranscripts`, `ComparisonResult`, `Divergence`, `ReplayError`
- **CI** — both runs and both comparisons, from a fresh clone
- **`SubZeroDev.GameEngine`** — a PR adding the hosted transport's column to the API coverage
  checklist in the engine's design source, from which `09-clients.md` is generated

Depends on: S4.

Acceptance:
- **S5.1** The set of operations the fixture's steps name equals the set of operations the table
  declares. Removing the only step naming one operation fails the suite with `CoverageIncomplete`
  naming that operation on the side it is missing from.
- **S5.2** **Comparison A:** the hosted run's dump equals the in-process run's snapshot, blob for
  blob, byte for byte. A mismatch reports the first divergence with a locator identifying the record.
- **S5.3** **Comparison B:** the hosted run's transcript equals the committed golden transcript byte
  for byte, and the in-process run's transcript equals the same file. One artifact carries both
  claims.
- **S5.4** The two comparisons are asserted separately: a failure of one is distinguishable in the
  suite's output from a failure of the other, and passing one does not report the other as passed.
- **S5.5** **Perturbation 1:** a run with two of the fixture's steps transposed fails comparison A.
  Asserted as a test, not demonstrated once.
- **S5.6** **Perturbation 2:** a run with one member of one response substituted fails comparison B.
  Same standard.
- **S5.7** The hosted run is a real operating-system process with a bound socket, addressed over that
  socket. Each response is fully read before the next request is sent, and the harness exposes no
  concurrency option to turn that off.
- **S5.8** No entry anywhere in either transcript contains a canonical serialization — the dynamic
  half of the projection-boundary gate, asserted over the whole transcript rather than per row.
- **S5.9** `save-game`'s transcript entry is the narrowed `{ saveId }` in **both** runs, which is what
  says run 1 drove the same `Dispatcher` the surfaces use rather than the store directly.
- **S5.10** A passing run leaves the golden transcript's bytes unchanged: the working tree is clean
  after a green suite. Regeneration is an explicit act, reviewed as a diff.
- **S5.11** Both runs and both comparisons execute in CI from a fresh clone, with no artifact carried
  in from a previous run.
- **S5.12** A PR is open against `SubZeroDev.GameEngine` adding the hosted transport's column — the
  **fifth** — to the coverage checklist, with one tick per operation the replay exercised. Because
  G1 pins the ten-operation release and the fixture covers every row (S5.1), the column is complete:
  ten ticks, no blank. A blank would mean S5.1 failed.

Out of scope: any endpoint serving the serialization, however named; an ignore-list, a normalization
or an options parameter on either comparison — a byte-identity suite that can be told what to skip
stops comparing anything; per-run golden files; running the fixture through MCP (S6) or through the
edge (S7).

---

## S6 — The same game, through an assistant
**Status:** queued

Delivers: an assistant that speaks MCP can play the same game, in the same service, against the same
sessions a network caller sees — and there is now a test that proves neither surface has a mind of
its own, because deleting one operation from the table takes it out of both at once.

Repository: **this one**.

Touches:
- **MCP surface** — `buildMcpSurface`, `McpSurface`, `McpToolDescriptor`, `McpToolOutcome`, the MCP
  HTTP transport served by the workload process
- **Startup** — the MCP tool list built from the same in-memory row set before the listener binds
- **The dependency-direction test** — extended to the MCP surface's module graph
- **A table-is-the-only-source test** — one row removed, both surfaces observed

Depends on: S3.

Acceptance:
- **S6.1** `listTools()` returns exactly as many descriptors as the table has rows, and the descriptor
  names correspond one-to-one with the rows' `mcpTool` values. Checkable by counting.
- **S6.2** A session created over the JSON wire is addressable by an MCP tool call in the same
  process, and the reverse. **One store**, asserted rather than assumed.
- **S6.3** One operation end to end through MCP: a tool call creates a session and a second tool call
  submits an action against it, each returning a canonically-encoded result identical to the JSON
  wire's for the same arguments.
- **S6.4** With one row removed from the table and the service restarted, the corresponding HTTP path
  returns `404` with `unknown_operation` **and** the tool is absent from `listTools()`. One change,
  both surfaces, no second edit — this is the test that the table is the only source. The row-removed
  artifact is constructed by the test and loaded through `loadContract` — the generator refuses to
  emit one (S2.2), and startup asserts the engine version, not arity, so the crafted artifact starts.
- **S6.5** A tool call whose arguments fail the row's request schema returns an error outcome carrying
  `malformed_payload`, and the store is never called.
- **S6.6** An engine error raised through a tool call carries the same code verbatim as the JSON wire
  returns for the same input, and a rejected action is a successful tool result carrying the store's
  unsuccessful result — no MCP-specific error vocabulary.
- **S6.7** Two rows carrying the same `mcpTool` fail startup with `DuplicateToolName` naming both,
  before the listener binds.
- **S6.8** The MCP surface's module graph does not reach `StoreSerializationHandle`, asserted by the
  same dependency-direction test that covers the HTTP surface.

Out of scope: MCP through the edge — a stated gap that is harmless while reachability is
trusted-local, and G3's to close; a separate stdio MCP process, which would compose a second store;
any tool that is not a row, any richer view for MCP, any per-surface narrowing.

---

## S7 — The edge in front, and an honest answer when the service behind it is gone
**Status:** queued

Delivers: an operator can put the .NET edge in front of the game service and play through it with no
difference the caller can detect. When the service behind it is stopped, the edge stays up, says
plainly that it is not ready, and names what is wrong instead of failing silently or pretending.

Repository: **this one**, under `workloads/` beside the Node workload.

Touches:
- **The edge host** — `AddPlatformWebHost()` and nothing else Platform-shaped
- **Options and forwarding** — `GameEdgeOptions`, `ForwardedRequest`, `ForwardedResponse`,
  `IGameWorkloadForwarder`, `GameEdgeEndpointExtensions.MapGameWorkloadForwarding`
- **Readiness** — `IGameWorkloadProbe`, `GameWorkloadReadinessCheck`
- **Errors** — `EdgeError` with its two variants
- **The harness** — `HostedTarget` addressed at the edge, with `shutdown` and `readDump` still
  addressing the workload

Depends on: S5.

Acceptance:
- **S7.1** The edge's `Program.cs` contains `AddPlatformWebHost()` as its only Platform-shaped
  registration call. The forwarding route and the readiness check are registered the way any
  application registers a route and a service; there is no `AddGameEdge`.
- **S7.2** With the workload running, a request to the edge returns the workload's status code and its
  body **byte for byte**, and the path and query reach the workload unaltered — including a path
  segment the edge has never heard of, which it forwards rather than rejects.
- **S7.3** With the workload stopped, the edge's liveness returns `200` and its readiness returns
  `503` with a body naming the failed check. The edge started successfully while the workload was
  already down.
- **S7.4** The readiness check reports `Kind = Readiness`, `Criticality = Required` and
  `TouchesExternalDependency = true`. Registering the same check as liveness aborts startup with
  Platform's existing `ExternalDependencyInLivenessCheck`.
- **S7.5** With the workload stopped, a forwarded request returns `503` carrying the correlation, and
  the edge makes **exactly one** attempt — asserted with a counting stub, not inferred from timing.
- **S7.6** Against a workload that accepts the connection and never answers, the edge returns `504`
  after `ForwardTimeout`, carrying the correlation, having made exactly one attempt.
- **S7.7** The edge's readiness check probes the workload's liveness endpoint and no game operation:
  after readiness has run any number of times, the workload's session count is zero.
- **S7.8** The same replay, with the client addressed at the edge, passes comparison A and comparison
  B — the dump still read from the workload, the golden transcript still the one Stage 1 asserts
  against.
- **S7.9** Stage 1's single-hop replay is still in the suite and still green after the edge lands.
  Both run in CI.

Out of scope: authorization, ownership checks and anything that reads a principal — G3, and a widened
edge is G3 pulled into G1; persistence, caching, retries and rate limiting; routing the MCP surface;
the edge consuming the contract artifact and routing per operation, which needs a distribution channel
G1 deliberately does not build; a client-side span for the hop.

---

## S8 — One trace across two languages
**Status:** queued

Delivers: an operator looking at their telemetry sees a single request crossing from the .NET edge
into the Node service as one trace, with one identifier running through both — so a question about a
slow or failed call has one thread to pull rather than two systems to correlate by hand.

Repository: **this one**.

Touches:
- **The edge** — the outbound `traceparent` written from the ambient operation scope's `TraceContext`
- **The workload** — inbound trace adoption, `RequestContext`, the OTLP exporter constructed only
  when an endpoint is configured
- **CI** — an OTLP sink on loopback, both processes exporting to it, assertions over the collected
  spans, and the existing outbound-port block still in force

Depends on: S7.

Acceptance:
- **S8.1** One request through the edge produces spans from both processes in the collector sharing
  one trace-id.
- **S8.2** The workload's span's parent is the edge's span for that request — the relationship is
  asserted, not inferred from a shared id.
- **S8.3** The correlation on the edge's response equals that trace-id, and equals the correlation the
  workload recorded for the same request. One greppable value spans both processes.
- **S8.4** A request arriving at the edge with a well-formed `traceparent` has its trace adopted
  end to end; one arriving with `traceparent: not-a-traceparent` returns the same `200` under a fresh
  root trace, at both hops.
- **S8.5** With no OTLP endpoint configured on either process, neither constructs an exporter and
  neither attempts an outbound connection — asserted with outbound network blocked, both processes
  still serving and the replay still green.
- **S8.6** The CI job runs the collector on loopback, asserts over collected spans rather than over a
  propagated header alone, and still fails if either process opens an outbound OTLP connection.

Out of scope: metrics, dashboards, log aggregation, alerting — one trace is Stage 2's evidence, not
the first of a set; a client-side span for the edge's hop, which Platform's deliberate omission of
HttpClient instrumentation rules out and which performance being a non-goal makes affordable; any
collector reachable over the network.

---

## S9 — A fresh clone can re-run everything this effort proved
**Status:** queued

Delivers: someone arriving at the repository with nothing but a clone can start both processes,
replay the byte-identity proof, and regenerate the contract, by following what is written — and the
build proves the instructions still work, so the next effort begins by re-running this one's proof
rather than by reconstructing it.

Repository: **this one**.

Touches:
- **`workloads/game-service/` documentation** — starting both processes, replaying the proof,
  regenerating the contract, each as a command that can be copied and run
- **CI** — a job that runs the documented commands themselves
- **Handover notes** — the two facts G2 and G3 inherit

Depends on: S5, S7, S8.

Acceptance:
- **S9.1** The documentation states, as runnable commands: how to start the workload, how to start the
  edge, how to run the replay against each, and how to regenerate and publish the contract package.
- **S9.2** CI executes those documented commands rather than a private script, and the job fails if a
  documented command does not exist or does not run.
- **S9.3** Following the regeneration instructions against an unchanged engine and an unchanged row
  set produces an artifact byte-identical to the published one.
- **S9.4** The documentation states the two facts the next effort inherits rather than discovers:
  **single-instance is unenforced** — a second workload instance holds its own memory and presents as
  `unknown_session` — and **the MCP surface bypasses the edge**, which is harmless only while
  reachability is trusted-local.
- **S9.5** The documentation states where the engine writes the mutated blob before writing through
  to persistence, which is the finding G2 must answer when its persistence can fail.

Out of scope: a human-facing interface of any kind — no front end, no playground, no operator console;
deployment machinery, container images and process supervision, which two hand-started processes are
the whole of; the public site's roadmap rendering, which is an L-track question tracked as issue #80;
the human-facing guide, which is `/make-human-docs`'s output and not a slice's.
