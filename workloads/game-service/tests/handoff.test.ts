/**
 * G3 S1.1–S1.7 — the handoff gate. A workload refuses to start without a verification key, answers
 * every non-probe request that carries no verifiable assertion with `401 assertion_refused` before
 * Dispatch or the store sees it, logs the refusal's reason and nothing else, and serves a request
 * whose assertion verifies exactly as G2 did. S1.8 is the rest of the suite: G2's replay, contention
 * and byte-identity proofs, now carrying test-key assertions.
 */
import { spawn } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import pg from "pg";
import { afterEach, describe, expect, it, vi } from "vitest";

import { compose } from "../src/compose.js";
import { createDispatcher } from "../src/dispatch.js";
import { HANDOFF_KEYS_DIRECTORY_VARIABLE } from "../src/handoff.js";
import { buildHttpSurface } from "../src/http-surface.js";
import { startWorkload } from "../src/lifecycle.js";
import type { HandoffRefusal, StorageProfile, WorkloadConfiguration, WorkloadProcess } from "../src/types.js";
import { configurationFor, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import {
  ASSERTION_HEADER,
  claimsFor,
  handoffEnvironment,
  mintTestAssertion,
  signCompact,
  signedFetch,
  STRANGER_KEY,
  TEST_HANDOFF,
  TEST_KEY,
  TEST_OWNER,
} from "./support/handoff.js";
import type { AssertionClaims } from "./support/handoff.js";
import { bodyText, contract, wireRequest } from "./support/harness.js";
import { startOtlpSink } from "./support/otlp-sink.js";
import type { OtlpSink } from "./support/otlp-sink.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const TSX_CLI = fileURLToPath(new URL("../node_modules/tsx/dist/cli.mjs", import.meta.url));
const MAIN_ENTRY = fileURLToPath(new URL("../src/main.ts", import.meta.url));
const REPO_ROOT = fileURLToPath(new URL("..", import.meta.url));

const CREATE_SESSION = "/v1/create-session";
const CREATE_SESSION_BODY = JSON.stringify({ campaignId: CAMPAIGN_ID });
const FIXED_INSTANT = "2026-01-01T00:00:00.000Z";

function configuration(overrides: Partial<WorkloadConfiguration> = {}): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    handoff: TEST_HANDOFF,
    storage: { kind: "in-memory" },
    ...overrides,
  };
}

function replayConfiguration(): WorkloadConfiguration {
  const dumpPath = join(mkdtempSync(join(tmpdir(), "handoff-dump-")), "dump.json");
  return configuration({ determinism: { kind: "replay", fixedInstant: FIXED_INSTANT, dumpPath } });
}

let workload: WorkloadProcess | undefined;
let sink: OtlpSink | undefined;
let schema: TestSchema | undefined;

afterEach(async () => {
  vi.restoreAllMocks();
  await workload?.shutdown();
  await sink?.close();
  await schema?.drop();
  workload = undefined;
  sink = undefined;
  schema = undefined;
});

async function start(config: WorkloadConfiguration = configuration()): Promise<string> {
  const started = await startWorkload(config);
  if (!started.ok) throw new Error(`startWorkload failed: ${JSON.stringify(started.error)}`);
  workload = started.value;
  return `http://127.0.0.1:${started.value.listening.port}`;
}

function post(base: string, path: string, assertion?: string, body: string = CREATE_SESSION_BODY): Promise<Response> {
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (assertion !== undefined) headers[ASSERTION_HEADER] = assertion;
  return fetch(`${base}${path}`, { method: "POST", headers, body });
}

/** The refusal's whole contract on the wire: `401`, and a body that is the code and the correlation
 *  and nothing else — no reason, no echo of anything the caller sent. */
async function expectRefused(response: Response): Promise<string> {
  expect(response.status).toBe(401);
  const correlation = response.headers.get("x-correlation-id");
  expect(correlation).toMatch(/^[0-9a-f]{32}$/);
  const text = await response.text();
  expect(JSON.parse(text)).toStrictEqual({ code: "assertion_refused", correlation });
  return text;
}

function spawnMain(env: Record<string, string | undefined>): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const merged: Record<string, string> = { ...(process.env as Record<string, string>), ...handoffEnvironment() };
  for (const [key, value] of Object.entries(env)) {
    if (value === undefined) delete merged[key];
    else merged[key] = value;
  }
  // The in-memory profile, so nothing but the handoff settings can be what refuses the start.
  delete merged["GAME_SERVICE_DB_CONNECTION_STRING"];
  merged["GAME_SERVICE_PORT"] = "0";
  const child = spawn(process.execPath, [TSX_CLI, MAIN_ENTRY], { cwd: REPO_ROOT, env: merged });
  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk: Buffer) => (stdout += chunk.toString()));
  child.stderr.on("data", (chunk: Buffer) => (stderr += chunk.toString()));
  return new Promise((resolve) => {
    const timer = setTimeout(() => child.kill(), 20_000);
    child.on("exit", (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

describe("S1.1 — no verification key, no start", () => {
  it.each([
    ["unset", undefined],
    ["an empty directory", mkdtempSync(join(tmpdir(), "handoff-empty-"))],
  ])("main.ts with the key directory %s exits non-zero before listening, naming the setting", async (_label, directory) => {
    const exited = await spawnMain({ [HANDOFF_KEYS_DIRECTORY_VARIABLE]: directory });
    expect(exited.code, exited.stderr).not.toBe(0);
    expect(exited.code).not.toBeNull();
    expect(exited.stderr).toContain(HANDOFF_KEYS_DIRECTORY_VARIABLE);
    expect(exited.stdout).not.toMatch(/listening/i);
  }, 30_000);

  it("startWorkload with an empty key set refuses before binding, naming handoff.keys", async () => {
    const started = await startWorkload(configuration({ handoff: { ...TEST_HANDOFF, keys: new Map() } }));
    expect(started.ok).toBe(false);
    if (started.ok) {
      workload = started.value;
      return;
    }
    expect(started.error).toStrictEqual({ code: "ConfigurationInvalid", setting: "handoff.keys" });
  });
});

describe("S1.2 — an unsigned operation request reaches no store", () => {
  it("refuses every operation route with 401, and runs no statement against the durable store", async () => {
    schema = await createTestSchema();
    const storage: StorageProfile = { kind: "durable", store: configurationFor(schema.schema) };
    const base = await start(configuration({ storage }));

    const statements = vi.spyOn(pg.Client.prototype, "query");
    for (const row of contract.operations) {
      await expectRefused(await post(base, `/v1/${row.httpPath as string}`));
    }
    expect(statements).not.toHaveBeenCalled();

    // The counter does count: the same request, signed, reaches the store.
    const served = await signedFetch(`${base}${CREATE_SESSION}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: CREATE_SESSION_BODY,
    });
    expect(served.status).toBe(200);
    expect(statements).toHaveBeenCalled();
  });
});

describe("S1.3 — each refusal answers the same, and logs its own reason", () => {
  const request = { method: "POST", pathAndQuery: CREATE_SESSION, assertion: undefined };
  const header = { alg: "ES256", kid: TEST_KEY.keyId, typ: "szd-principal+jwt" };
  const minted = (overrides: Partial<AssertionClaims> = {}, now: Date = new Date()): string =>
    mintTestAssertion(TEST_KEY, TEST_OWNER, request, now, overrides);
  const shifted = (seconds: number): Date => new Date(Date.now() + seconds * 1000);

  const cases: ReadonlyArray<{ name: string; reason: HandoffRefusal; path?: string; assertion: () => string | undefined }> = [
    { name: "missing", reason: "missing", assertion: () => undefined },
    { name: "malformed", reason: "malformed", assertion: () => "not-a-compact-jws" },
    {
      name: "unknown_key",
      reason: "unknown_key",
      assertion: () => signCompact(TEST_KEY, { ...header, kid: "no-such-key" }, claimsFor(TEST_OWNER, request, new Date())),
    },
    {
      name: "bad_signature",
      reason: "bad_signature",
      assertion: () => mintTestAssertion(STRANGER_KEY, TEST_OWNER, request, new Date()),
    },
    {
      name: "wrong_algorithm",
      reason: "wrong_algorithm",
      assertion: () => signCompact(TEST_KEY, { ...header, alg: "ES384" }, claimsFor(TEST_OWNER, request, new Date())),
    },
    { name: "wrong_issuer", reason: "wrong_issuer", assertion: () => minted({ iss: "https://elsewhere.test" }) },
    { name: "wrong_audience", reason: "wrong_audience", assertion: () => minted({ aud: "another-service" }) },
    { name: "expired", reason: "expired", assertion: () => minted({}, shifted(-120)) },
    { name: "not_yet_valid", reason: "not_yet_valid", assertion: () => minted({}, shifted(120)) },
    {
      name: "lifetime_exceeded",
      reason: "lifetime_exceeded",
      assertion: () => {
        const iat = Math.floor(Date.now() / 1000);
        return minted({ iat, exp: iat + TEST_HANDOFF.maxLifetimeSeconds + 1 });
      },
    },
    { name: "request_mismatch (htm)", reason: "request_mismatch", assertion: () => minted({ htm: "GET" }) },
    { name: "request_mismatch (path)", reason: "request_mismatch", assertion: () => minted({ htu: "/v1/save" }) },
    {
      name: "request_mismatch (query)",
      reason: "request_mismatch",
      path: `${CREATE_SESSION}?campaign=other`,
      assertion: () => minted({ htu: CREATE_SESSION }),
    },
    { name: "unsupported_kind", reason: "unsupported_kind", assertion: () => minted({ pkind: "Service" }) },
    { name: "empty_owner (pis)", reason: "empty_owner", assertion: () => minted({ pis: "" }) },
    { name: "empty_owner (psub)", reason: "empty_owner", assertion: () => minted({ psub: "" }) },
  ];

  it.each(cases)("$name — 401 assertion_refused, the log names $reason", async ({ reason, path, assertion }) => {
    const base = await start();
    const logged = vi.spyOn(console, "error").mockImplementation(() => undefined);

    const response = await post(base, path ?? CREATE_SESSION, assertion());
    const text = await expectRefused(response);
    const correlation = response.headers.get("x-correlation-id")!;

    expect(logged).toHaveBeenCalledWith(`[game-service] handoff refused: ${reason} (correlation ${correlation})`);
    expect(text).not.toContain(reason);
  });

  it("covers every HandoffRefusal value", () => {
    const every: Record<HandoffRefusal, true> = {
      missing: true,
      malformed: true,
      unknown_key: true,
      bad_signature: true,
      wrong_algorithm: true,
      wrong_issuer: true,
      wrong_audience: true,
      expired: true,
      not_yet_valid: true,
      lifetime_exceeded: true,
      request_mismatch: true,
      unsupported_kind: true,
      empty_owner: true,
    };
    expect(new Set(cases.map((c) => c.reason))).toStrictEqual(new Set(Object.keys(every)));
  });
});

describe("S1.4 — a valid assertion passes, and the answer is G2's", () => {
  it("returns the byte-identical response the ungated surface returns for the same input", async () => {
    const base = await start(replayConfiguration());
    const gated = await signedFetch(`${base}${CREATE_SESSION}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: CREATE_SESSION_BODY,
    });
    expect(gated.status).toBe(200);
    const gatedText = await gated.text();

    // The same configuration composed in-process, with no transport and so no gate at all.
    const composed = await compose(replayConfiguration(), contract);
    if (!composed.ok) throw new Error(`compose failed: ${JSON.stringify(composed.error)}`);
    try {
      const surface = buildHttpSurface(
        contract,
        createDispatcher(contract, composed.value.stores, composed.value.lifecycle),
      );
      if (!surface.ok) throw new Error(`buildHttpSurface failed: ${JSON.stringify(surface.error)}`);
      const ungated = await surface.value.handle(wireRequest(CREATE_SESSION, { campaignId: CAMPAIGN_ID }));
      expect(ungated.status).toBe(200);
      expect(gatedText).toBe(bodyText(ungated));
    } finally {
      await composed.value.close();
    }
  });
});

describe("S1.5 — the probes need no assertion", () => {
  it.each(["/livez", "/readyz"])("%s answers 200 healthy without one", async (path) => {
    const base = await start();
    const response = await fetch(`${base}${path}`);
    expect(response.status).toBe(200);
    expect(await response.json()).toStrictEqual({ status: "healthy" });
  });
});

describe("S1.6 — the MCP transport is gated too", () => {
  it.each([
    ["/mcp/list-tools", ""],
    ["/mcp/call-tool", JSON.stringify({ name: "start_game", arguments: { campaignId: CAMPAIGN_ID } })],
  ])("%s with no assertion is 401 assertion_refused", async (path, body) => {
    const base = await start();
    await expectRefused(await post(base, path, undefined, body));
  });
});

describe("S1.7 — the assertion reaches no log, span or error body", () => {
  it("is absent from every captured line, export and response of S1.2–S1.6's refusals", async () => {
    sink = await startOtlpSink();
    const base = await start(configuration({ otlpEndpoint: sink.url }));

    const lines: string[] = [];
    for (const method of ["log", "info", "warn", "error", "debug"] as const) {
      vi.spyOn(console, method).mockImplementation((...args: unknown[]) => {
        lines.push(args.map(String).join(" "));
      });
    }

    const presented: string[] = [];
    const bodies: string[] = [];
    const now = new Date();
    const request = { method: "POST", pathAndQuery: CREATE_SESSION, assertion: undefined };
    const refusedAssertions = [
      mintTestAssertion(STRANGER_KEY, TEST_OWNER, request, now),
      mintTestAssertion(TEST_KEY, TEST_OWNER, request, now, { aud: "another-service" }),
      mintTestAssertion(TEST_KEY, TEST_OWNER, request, now, { htu: "/v1/save" }),
      mintTestAssertion(TEST_KEY, TEST_OWNER, request, now, { psub: "" }),
    ];
    for (const assertion of refusedAssertions) {
      presented.push(assertion);
      const response = await post(base, CREATE_SESSION, assertion);
      bodies.push(await expectRefused(response));
    }
    for (const path of ["/mcp/list-tools", "/mcp/call-tool"]) {
      const assertion = mintTestAssertion(TEST_KEY, TEST_OWNER, { method: "GET", pathAndQuery: path, assertion: undefined }, now);
      presented.push(assertion);
      const response = await post(base, path, assertion, "{}");
      bodies.push(await expectRefused(response));
    }
    // And one that passes, so a span exported for an accepted request is searched too.
    const accepted = mintTestAssertion(TEST_KEY, TEST_OWNER, request, new Date());
    presented.push(accepted);
    const served = await post(base, CREATE_SESSION, accepted);
    expect(served.status).toBe(200);
    bodies.push(await served.text());

    const outcome = await workload!.shutdown();
    workload = undefined;
    expect(outcome.ok).toBe(true);
    expect(sink.bodies.length).toBeGreaterThan(0);
    expect(lines.some((line) => line.includes("handoff refused"))).toBe(true);

    const captured = [...lines, ...sink.bodies, ...bodies];
    for (const assertion of presented) {
      const [, , signature] = assertion.split(".");
      for (const text of captured) {
        expect(text).not.toContain(assertion);
        expect(text).not.toContain(signature!);
      }
    }
  });
});
