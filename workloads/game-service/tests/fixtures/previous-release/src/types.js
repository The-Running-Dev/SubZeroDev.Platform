export function ok(value) {
    return { ok: true, value };
}
export function err(error) {
    return { ok: false, error };
}
/** The production defaults — 30 days, 365 days, 30 days — and the one set of bounds every
 *  `DurableStoreConfiguration` in this codebase is built from, proof and test runs included, so
 *  a run is never bound by its own TTLs without also being the values `main.ts`'s durable profile
 *  ships. Comfortably clear of `ASSUMED_FORWARD_TIMEOUT_SECONDS` for the `retentionHorizonSeconds`
 *  check `compose()` performs (`compose.ts`). The retention horizon deliberately does not equal
 *  the save TTL (`design/90-decisions.md`, "The retention horizon default no longer equals the
 *  save TTL") — retention is how long a swept-past row's id
 *  stops being distinguishable from one that never existed, not how long a save is kept. */
export const DEFAULT_LIFECYCLE_BOUNDS = {
    sessionIdleTtlSeconds: 2_592_000,
    saveTtlSeconds: 31_536_000,
    retentionHorizonSeconds: 2_592_000,
    sweepIntervalSeconds: 3600,
    sweepStatementTimeoutMs: 5000,
};
/** The stated, un-tuned pool size and connect timeout every `StoreConnection` this workload
 *  builds uses — the proof harness's own run-scoped schemas (`harness.ts`'s `createRunSchema`),
 *  the fresh-clone migration entry point (`scripts/migrate.ts`), and the durable process entry
 *  point (`main.ts`) alike. One number moved here, once, rather than duplicated at each of those
 *  three module boundaries — `design/90-decisions.md`, "`DEFAULT_STORE_POOL_SIZE`/
 *  `DEFAULT_STORE_CONNECT_TIMEOUT_MS` move from `harness.ts` to `types.ts`": `scripts/migrate.ts`
 *  reaching into the proof harness for these was the dependency direction that contradicted
 *  "nothing depends on a harness". */
export const DEFAULT_STORE_POOL_SIZE = 5;
export const DEFAULT_STORE_CONNECT_TIMEOUT_MS = 5000;
/** `GAME_SERVICE_DETERMINISM=replay` plus both companion variables selects the replay profile;
 *  anything else — unset, any other value, or either companion variable missing — is the default
 *  profile. Read the same way by both process entry points this workload has, `main.ts` and
 *  `harness-entrypoint.ts`, for the reason `durableStorageProfileFromEnv` below is: one reader per
 *  profile, not one per entry point. Without it the replay profile S4 delivers would be reachable
 *  only from a programmatic `startWorkload` caller, never from a process an operator starts. */
export function determinismProfileFromEnv(env) {
    if (env["GAME_SERVICE_DETERMINISM"] !== "replay") {
        return { kind: "default" };
    }
    const fixedInstant = env["GAME_SERVICE_FIXED_INSTANT"];
    const dumpPath = env["GAME_SERVICE_DUMP_PATH"];
    if (!fixedInstant || !dumpPath) {
        return { kind: "default" };
    }
    return { kind: "replay", fixedInstant, dumpPath };
}
/** `GAME_SERVICE_STORAGE=durable` plus its companion variables, read the same way by every
 *  process entry point this workload has — `main.ts`'s own process and the proof harness's own
 *  entry point (`harness-entrypoint.ts`) alike — so the env var names and the `StorageProfile`
 *  they build live in one place rather than two independently-maintained copies.
 *  `requireSchema` is the one difference between those two callers: a real operator's durable
 *  process defaults a missing schema to `public`, while the proof harness's own discipline (S8) is
 *  that a schema is never implicit, because the isolation a per-run schema gives every proof
 *  depends on one always being named.
 *
 *  `GAME_SERVICE_READ_WRITE_PAUSE_MS` is the perturbation seam, and it defaults to `0` — including
 *  when it is set to anything that is not a non-negative integer, so a typo cannot arm a
 *  diagnostic. It is readable here rather than only from a programmatic caller because S7's two
 *  instances are now real child processes and the pause is the only thing that tells them apart
 *  (`design/90-decisions.md`, 2026-08-21, "The two-instance proof spawns real processes"); this is
 *  the same shape `GAME_SERVICE_DETERMINISM` already takes, and invariant 87 — the seam is inert at
 *  `0`, and `0` is what every configuration but a perturbed harness's carries — is unchanged by it. */
export function durableStorageProfileFromEnv(env, options) {
    if (env["GAME_SERVICE_STORAGE"] !== "durable") {
        return ok({ kind: "in-memory" });
    }
    const connectionString = env["GAME_SERVICE_DB_CONNECTION_STRING"];
    if (!connectionString) {
        return err({ setting: "GAME_SERVICE_DB_CONNECTION_STRING" });
    }
    const schema = env["GAME_SERVICE_DB_SCHEMA"];
    if (options.requireSchema && !schema) {
        return err({ setting: "GAME_SERVICE_DB_SCHEMA" });
    }
    const pause = Number.parseInt(env["GAME_SERVICE_READ_WRITE_PAUSE_MS"] ?? "", 10);
    return ok({
        kind: "durable",
        store: {
            connection: {
                connectionString,
                poolSize: DEFAULT_STORE_POOL_SIZE,
                connectTimeoutMs: DEFAULT_STORE_CONNECT_TIMEOUT_MS,
                schema: schema ? schema : null,
            },
            bounds: DEFAULT_LIFECYCLE_BOUNDS,
            readWritePauseMs: Number.isInteger(pause) && pause >= 0 ? pause : 0,
        },
    });
}
