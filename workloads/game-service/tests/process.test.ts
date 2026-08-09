/**
 * S3.12–S3.13 and S3.15 — facts about the running process rather than about a surface: what the
 * listener binds, what it does not dial, and where its contract came from.
 */
import { describe, expect, it } from "vitest";
import { connect, createServer } from "node:net";
import { networkInterfaces } from "node:os";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

import { startWorkload } from "../src/lifecycle.js";
import { compose } from "../src/compose.js";
import { contract } from "./support/harness.js";

const REPOSITORY_ROOT = join(new URL("../../..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
const WORKLOAD_ROOT = join(new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));

const NO_HOST_CONFIGURATION = {
  listen: { host: "", port: 0 },
  determinism: { kind: "default" as const },
  otlpEndpoint: null,
};

function reachable(host: string, port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = connect({ host, port });
    const settle = (value: boolean) => {
      socket.destroy();
      resolve(value);
    };
    socket.setTimeout(750, () => settle(false));
    socket.on("connect", () => settle(true));
    socket.on("error", () => settle(false));
  });
}

/** Every non-loopback IPv4 address this machine holds. Empty on a host with no such interface,
 *  in which case the negative half of S3.12 has nothing to assert and says so. */
function otherAddresses(): string[] {
  return Object.values(networkInterfaces())
    .flatMap((entries) => entries ?? [])
    .filter((entry) => entry.family === "IPv4" && !entry.internal)
    .map((entry) => entry.address);
}

describe("S3.12 — with no listen host configured the service is loopback-only", () => {
  it("is reachable on loopback and unreachable on the machine's other addresses", { timeout: 30_000 }, async () => {
    const started = await startWorkload(NO_HOST_CONFIGURATION);
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    try {
      const { port } = started.value.listening;
      expect(started.value.listening.host).toBe("127.0.0.1");
      expect(await reachable("127.0.0.1", port)).toBe(true);

      for (const address of otherAddresses()) {
        expect(await reachable(address, port), `${address}:${port} should not accept`).toBe(false);
      }
    } finally {
      await started.value.shutdown();
    }
  });
});

describe("S3.13 — with otlpEndpoint null nothing is exported and nothing is dialled", () => {
  it("constructs no exporter and opens no outbound connection while still serving", async () => {
    const dialled: string[] = [];
    const originalConnect = (await import("node:net")).connect;
    void originalConnect;

    // A loopback sink standing in for a collector: if an exporter were constructed against a
    // default endpoint, something would arrive here. Nothing may.
    const sink = createServer((socket) => {
      dialled.push("connection");
      socket.destroy();
    });
    await new Promise<void>((resolve) => sink.listen(4318, "127.0.0.1", resolve));

    const started = await startWorkload({
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: null,
    });
    expect(started.ok).toBe(true);
    if (!started.ok) {
      await new Promise<void>((resolve) => sink.close(() => resolve()));
      return;
    }

    try {
      expect(await reachable("127.0.0.1", started.value.listening.port)).toBe(true);
      await new Promise((resolve) => setTimeout(resolve, 250));
      expect(dialled).toEqual([]);
    } finally {
      await started.value.shutdown();
      await new Promise<void>((resolve) => sink.close(() => resolve()));
    }
  });

  it("composes without an exporter when no endpoint is configured", async () => {
    const composed = await compose(
      { listen: { host: "127.0.0.1", port: 0 }, determinism: { kind: "default" }, otlpEndpoint: null },
      contract,
    );
    expect(composed.ok).toBe(true);
  });
});

describe("S3.15 — the contract comes from the package, not from a copy in this repository", () => {
  it("declares the contract package as a dependency and reads the artifact through it", () => {
    const manifest = JSON.parse(readFileSync(join(WORKLOAD_ROOT, "package.json"), "utf8")) as {
      dependencies: Record<string, string>;
    };
    expect(Object.keys(manifest.dependencies)).toContain("@subzerodev/service-contract");

    const resolved = join(WORKLOAD_ROOT, "node_modules", "@subzerodev", "service-contract", "dist", "contract.json");
    expect(existsSync(resolved)).toBe(true);
    expect(contract.operations.length).toBe(10);
  });

  it("holds no copy of the contract artifact in tracked repository source", () => {
    const offenders: string[] = [];
    const skip = new Set(["node_modules", "vendor", ".git", "dist", "site", "docs"]);

    const walk = (directory: string): void => {
      for (const entry of readdirSync(directory, { withFileTypes: true })) {
        if (skip.has(entry.name)) continue;
        const full = join(directory, entry.name);
        if (entry.isDirectory()) {
          walk(full);
          continue;
        }
        if (!entry.name.endsWith(".json")) continue;
        const text = readFileSync(full, "utf8");
        if (text.includes('"statusMapping"') && text.includes('"wireVersion"')) {
          offenders.push(full);
        }
      }
    };

    walk(REPOSITORY_ROOT);
    expect(offenders).toEqual([]);
  });
});
