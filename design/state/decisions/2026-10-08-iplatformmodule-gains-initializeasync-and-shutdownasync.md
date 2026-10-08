# decision/2026-10-08-iplatformmodule-gains-initializeasync-and-shutdownasync
Date: 2026-10-08
Anchor: 2026-10-08 — #63: `IPlatformModule` gains `InitializeAsync` and `ShutdownAsync`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #63: `IPlatformModule` gains `InitializeAsync` and `ShutdownAsync`"

## Claim
Context — `IPlatformModule` had only `Register`. Modules that need start-time work each register a hosted service, ordered by registration rather than the graph.

Chosen — two default interface members, run by a Hosting lifecycle service in `StartAsync` (after every `StartingAsync` check, before ticks and the listener). Initialization is topological and sequential. Each call gets a fresh scope's provider. Shutdown runs in reverse, exactly once per initialized module, including after a later startup failure. An initialization throw is `HostStartupError.ModuleInitialization` after unwinding. Migrate mode runs no hook.

Rejected — abstract members (break every module); a separate interface (the ruling says on `IPlatformModule`); concurrent initialization; the root provider; migrating Licensing, Mcp and Identity's hosted services now.

Reversibility — expensive — every module implements the interface.
