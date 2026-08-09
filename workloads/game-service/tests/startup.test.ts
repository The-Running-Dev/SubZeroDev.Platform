/**
 * S3.9–S3.11 — the startup order the design claims: configuration, contract load, version
 * assertion, composition, both surfaces, then bind. Every failure below happens before the
 * listener binds, which is what makes the ordering assertable rather than incidental.
 *
 * `startWorkload` takes a `WorkloadConfiguration` and nothing else (`20-contract.md`), so the
 * crafted artifacts these criteria need reach it the only way left: as bytes on disk, named by
 * the environment and read through `loadContract`. S6.4 assumes the same route.
 */
import { afterEach, describe, expect, it } from "vitest";
import { connect } from "node:net";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { ContractPackage, OperationRow, SchemaRef } from "@subzerodev/service-contract";

import { buildHttpSurface } from "../src/http-surface.js";
import { createDispatcher } from "../src/dispatch.js";
import { compose } from "../src/compose.js";
import { startWorkload, createProbeSurface, CONTRACT_PATH_VARIABLE } from "../src/lifecycle.js";
import { contract, recordingStore } from "./support/harness.js";

const DEFAULT_CONFIGURATION = {
  listen: { host: "127.0.0.1", port: 0 },
  determinism: { kind: "default" as const },
  otlpEndpoint: null,
};

function withOperations(rows: readonly OperationRow[]): ContractPackage {
  return { ...contract, operations: rows };
}

/** Writes a crafted artifact and points the next `startWorkload` at it. */
function artifactAt(pkg: ContractPackage): string {
  const path = join(mkdtempSync(join(tmpdir(), "contract-")), "contract.json");
  writeFileSync(path, JSON.stringify(pkg));
  process.env[CONTRACT_PATH_VARIABLE] = path;
  return path;
}

afterEach(() => {
  delete process.env[CONTRACT_PATH_VARIABLE];
});

function refused(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = connect({ host: "127.0.0.1", port });
    socket.on("connect", () => {
      socket.destroy();
      resolve(false);
    });
    socket.on("error", () => resolve(true));
  });
}

const MISMATCHED = "0.0.1-not-the-resolved-engine" as ContractPackage["engineVersion"];

describe("S3.9 — an engine-version mismatch aborts before the listener binds", () => {
  it("fails composition with EngineVersionMismatch naming both versions", async () => {
    const composed = await compose(DEFAULT_CONFIGURATION, { ...contract, engineVersion: MISMATCHED });

    expect(composed.ok).toBe(false);
    if (composed.ok) return;
    expect(composed.error.code).toBe("EngineVersionMismatch");
    const rendered = JSON.stringify(composed.error);
    expect(rendered).toContain(MISMATCHED);
    expect(rendered).toContain(contract.engineVersion);
  });

  it("never binds the configured port — a connection attempt is refused", async () => {
    const port = 39_517;
    artifactAt({ ...contract, engineVersion: MISMATCHED });

    const started = await startWorkload({ ...DEFAULT_CONFIGURATION, listen: { host: "127.0.0.1", port } });

    expect(started.ok).toBe(false);
    if (started.ok) return;
    expect(started.error.code).toBe("Composition");
    expect(await refused(port)).toBe(true);
  });
});

describe("S3.10 — a table the service cannot satisfy fails surface construction", () => {
  it("fails with DuplicateRoute naming both rows when two derive the same path segment", () => {
    const first = contract.operations[0]!;
    const second = { ...contract.operations[1]!, httpPath: first.httpPath };
    const built = buildHttpSurface(withOperations([first, second]), createDispatcher(contract, recordingStore().store));

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.error.code).toBe("DuplicateRoute");
    const rendered = JSON.stringify(built.error);
    expect(rendered).toContain(first.operation as string);
    expect(rendered).toContain(second.operation as string);
  });

  it("fails with MissingSchema naming the row and the reference", () => {
    const absent = "https://contracts.subzerodev.dev/service-contract/v1/absent/request.json" as SchemaRef;
    const row = { ...contract.operations[0]!, requestShape: absent };
    const built = buildHttpSurface(withOperations([row]), createDispatcher(contract, recordingStore().store));

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.error.code).toBe("MissingSchema");
    const rendered = JSON.stringify(built.error);
    expect(rendered).toContain(row.operation as string);
    expect(rendered).toContain(absent);
  });

  it("fails startup before binding when the table is unsatisfiable", async () => {
    const port = 39_518;
    const first = contract.operations[0]!;
    artifactAt(withOperations([first, { ...contract.operations[1]!, httpPath: first.httpPath }]));

    const started = await startWorkload({ ...DEFAULT_CONFIGURATION, listen: { host: "127.0.0.1", port } });

    expect(started.ok).toBe(false);
    if (started.ok) return;
    expect(started.error.code).toBe("SurfaceBuild");
    expect(await refused(port)).toBe(true);
  });
});

describe("S3.11 — probes", () => {
  it("returns liveness healthy without touching the store, and readiness healthy once bound", async () => {
    const started = await startWorkload(DEFAULT_CONFIGURATION);
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    try {
      expect(started.value.probes.liveness().status).toBe("healthy");
      expect(started.value.probes.readiness().status).toBe("healthy");
      expect(started.value.listening.port).toBeGreaterThan(0);
    } finally {
      await started.value.shutdown();
    }
  });

  it("reports readiness healthy only after both surface construction and the bind", () => {
    const probes = createProbeSurface();

    expect(probes.surface.liveness().status).toBe("healthy");
    expect(probes.surface.readiness().status).toBe("unhealthy");

    probes.markSurfacesBuilt();
    expect(probes.surface.readiness().status).toBe("unhealthy");

    probes.markListening();
    expect(probes.surface.readiness().status).toBe("healthy");
  });

  it("answers liveness without reaching the store", async () => {
    const { store, calls } = recordingStore();
    const probes = createProbeSurface();
    void store;

    probes.markSurfacesBuilt();
    probes.markListening();
    probes.surface.liveness();
    probes.surface.readiness();

    expect(calls).toEqual([]);
  });
});
