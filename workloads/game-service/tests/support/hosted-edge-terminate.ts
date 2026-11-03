/**
 * G3 S3 — the real composition: the .NET edge in Terminate mode under the AuthenticatedTransport
 * profile, in front of the real game-service on a real PostgreSQL schema, with a test issuer whose
 * public keys the edge pins and a tap between edge and workload. Three processes (edge, workload,
 * and the Postgres the caller provides) and no network beyond them: the issuer's keys are configured,
 * never fetched (S3.8).
 */
import { spawn } from "node:child_process";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";

import { freePort } from "./free-port.js";
import { TEST_AUDIENCE, TEST_ISSUER, TEST_KEY } from "./handoff.js";
import { GAME_EDGE_DEFAULT_DLL, GAME_EDGE_FORWARD_TIMEOUT_SECONDS, waitForLive } from "./hosted-edge.js";
import { spawnHostedWorkload } from "./hosted-target.js";
import type { DurableHostedTarget, SpawnedHostedTarget } from "./hosted-target.js";
import { startRecordingProxy } from "./recording-proxy.js";
import type { RecordingProxy } from "./recording-proxy.js";
import { TOKEN_AUDIENCE, TOKEN_ISSUER, createTestIssuer } from "./test-issuer.js";
import type { TestIssuer } from "./test-issuer.js";

export interface TerminateEdge {
  readonly baseAddress: string;
  readonly issuer: TestIssuer;
  readonly tap: RecordingProxy;
  /** Everything the edge wrote to stdout and stderr so far. */
  edgeOutput(): string;
  workloadOutput(): string;
  /** Signals both processes to exit and waits for them, which is what flushes their telemetry
   *  exporters — a SIGKILL (`stop()`) loses whatever was not yet exported. */
  shutdownGracefully(): Promise<void>;
  stop(): Promise<void>;
}

/** The environment that makes the edge a Terminate-mode transport host. The handoff key pair is the
 *  harness's own test pair, so the workload under test (which `spawnHostedWorkload` configures with
 *  that pair's public half) verifies what this edge signs. */
export function terminateEnvironment(issuer: TestIssuer, signingKeyPath: string): Record<string, string> {
  return {
    GameEdge__CredentialMode: "Terminate",
    Platform__CompositionProfile: "AuthenticatedTransport",
    GameEdge__Handoff__Issuer: TEST_ISSUER,
    GameEdge__Handoff__Audience: TEST_AUDIENCE,
    GameEdge__Handoff__KeyId: TEST_KEY.keyId,
    GameEdge__Handoff__SigningKeyPath: signingKeyPath,
    GameEdge__Handoff__Lifetime: "00:00:30",
    Platform__Identity__Bearer__game__Issuer: TOKEN_ISSUER,
    Platform__Identity__Bearer__game__SigningKeys: issuer.jwks,
    Platform__Identity__Bearer__game__Audiences__0: TOKEN_AUDIENCE,
    Platform__Identity__Bearer__game__Algorithms__0: "ES256",
  };
}

export function writeHandoffSigningKey(): string {
  const directory = mkdtempSync(join(tmpdir(), "game-edge-handoff-"));
  const path = join(directory, "handoff-signing.pem");
  writeFileSync(path, TEST_KEY.privateKey.export({ type: "pkcs8", format: "pem" }));
  return path;
}

interface EdgeProcess {
  readonly child: ChildProcessWithoutNullStreams;
  output(): string;
  /** `undefined` while running; the exit code (or `null` for a signal) once gone. */
  exited(): number | null | undefined;
}

/** A leak check reads the whole log, so unlike the replay spawner's tail this keeps up to 8 MiB. */
const OUTPUT_LIMIT = 8_388_608;

function edgeProcess(env: NodeJS.ProcessEnv): EdgeProcess {
  const dllPath = GAME_EDGE_DEFAULT_DLL();
  const child = spawn("dotnet", [dllPath], { cwd: dirname(dllPath), env });
  let output = "";
  let exitCode: number | null | undefined;
  const append = (chunk: Buffer): void => {
    const combined = output + chunk.toString("utf8");
    output = combined.length > OUTPUT_LIMIT ? combined.slice(combined.length - OUTPUT_LIMIT) : combined;
  };
  child.stdout.on("data", append);
  child.stderr.on("data", append);
  child.once("exit", (code) => {
    exitCode = code;
  });
  child.once("error", (thrown: Error) => {
    output += `spawn error: ${thrown.message}\n`;
    exitCode = -1;
  });
  return { child, output: () => output, exited: () => exitCode };
}

function edgeBaseEnvironment(port: number, workloadBaseAddress: string, otlpEndpoint?: string): NodeJS.ProcessEnv {
  return {
    ...process.env,
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    ASPNETCORE_ENVIRONMENT: "Production",
    GameEdge__WorkloadBaseAddress: workloadBaseAddress,
    GameEdge__ForwardTimeout: `00:00:${String(GAME_EDGE_FORWARD_TIMEOUT_SECONDS).padStart(2, "0")}`,
    GameEdge__ReadinessTimeout: "00:00:05",
    ...(otlpEndpoint ? { Platform__Telemetry__OtlpEndpoint: otlpEndpoint } : {}),
  };
}

export async function spawnTerminateEdge(
  durable: DurableHostedTarget,
  otlpEndpoint?: string,
  /** Edge environment applied last, so a test can move one setting (the issuer's address, the clock
   *  tolerance) without restating the rest. */
  overrides: Record<string, string> = {},
): Promise<TerminateEdge> {
  const workload: SpawnedHostedTarget = await spawnHostedWorkload(otlpEndpoint, durable);
  const tap = await startRecordingProxy(workload.target.baseAddress);
  const issuer = createTestIssuer();
  const port = await freePort();

  const edge = edgeProcess({
    ...edgeBaseEnvironment(port, tap.baseAddress, otlpEndpoint),
    ...terminateEnvironment(issuer, writeHandoffSigningKey()),
    ...overrides,
  });
  const baseAddress = `http://127.0.0.1:${port}`;

  const stop = async (): Promise<void> => {
    if (edge.exited() === undefined) edge.child.kill("SIGKILL");
    await workload.forceKill();
    await tap.close();
  };

  const shutdownGracefully = async (): Promise<void> => {
    if (edge.exited() === undefined) {
      const exited = new Promise<void>((resolve) => edge.child.once("exit", () => resolve()));
      edge.child.kill("SIGTERM");
      await Promise.race([exited, new Promise<void>((resolve) => setTimeout(resolve, 15_000))]);
    }
    await workload.target.shutdown();
  };

  try {
    await waitForLive(baseAddress, 30_000, () =>
      edge.exited() === undefined ? null : `edge exited with code ${String(edge.exited())}`);
  } catch (thrown) {
    await stop();
    throw new Error(`${thrown instanceof Error ? thrown.message : String(thrown)}; edge output:\n${edge.output()}`);
  }

  return { baseAddress, issuer, tap, edgeOutput: edge.output, workloadOutput: workload.stderr, shutdownGracefully, stop };
}

export interface Refusal {
  readonly exitCode: number | null | undefined;
  readonly output: string;
}

/** Starts the real edge with `overrides` on top of a valid Terminate configuration (or, with
 *  `base: "passThrough"`, a valid pass-through one) and reports how it ended. A startup refusal is
 *  an exit before the edge ever answers liveness; one still running at the deadline is killed and
 *  reported as `exitCode: undefined`, which no refusal case expects. A `undefined` override removes
 *  the variable. */
export async function startEdgeExpectingExit(
  overrides: Record<string, string | undefined>,
  base: "terminate" | "passThrough" = "terminate",
): Promise<Refusal> {
  const port = await freePort();
  const env: NodeJS.ProcessEnv = {
    ...edgeBaseEnvironment(port, "http://127.0.0.1:1"),
    ...(base === "terminate"
      ? terminateEnvironment(createTestIssuer(), writeHandoffSigningKey())
      : { GameEdge__CredentialMode: "PassThrough" }),
  };
  for (const [name, value] of Object.entries(overrides)) {
    if (value === undefined) delete env[name];
    else env[name] = value;
  }

  const edge = edgeProcess(env);
  const deadline = Date.now() + 20_000;
  while (edge.exited() === undefined && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  const exitCode = edge.exited();
  if (exitCode === undefined) edge.child.kill("SIGKILL");
  return { exitCode, output: edge.output() };
}
