/**
 * S7.8 — spawns a real workload process and a real .NET edge process in front of it, and implements
 * `HostedTarget` addressed at the edge: `baseAddress` is the edge, but `shutdown` and `readDump`
 * still address the workload directly, on the same terms `20-contract.md`'s `HostedTarget` note
 * states — the dump is a file the workload writes, never a value read out of the edge's memory.
 */
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createServer } from "node:net";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";

import { spawnHostedWorkload } from "./hosted-target.js";
import type { HostedTarget, Outcome, ShutdownError } from "../../src/types.js";

const DEFAULT_CONFIGURATION = process.env["GAME_EDGE_CONFIGURATION"] ?? "Debug";
const DEFAULT_DLL = fileURLToPath(
  new URL(
    `../../../game-edge/SubZeroDev.Platform.GameEdge/bin/${DEFAULT_CONFIGURATION}/net10.0/`
      + "SubZeroDev.Platform.GameEdge.dll",
    import.meta.url,
  ),
);

export interface SpawnedHostedEdge {
  readonly target: HostedTarget;
  /** Only for a test that needs to abandon both processes without a clean shutdown. */
  forceKill(): void;
}

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close();
        reject(new Error("could not allocate a free port"));
        return;
      }
      const port = address.port;
      server.close((closeError) => (closeError ? reject(closeError) : resolve(port)));
    });
  });
}

/** Keeps the last `TAIL_LIMIT` characters of a child's output. Bounded because the edge writes
 *  every log line to its own stdout and the whole point of reading it is to keep the pipe drained,
 *  not to hold the transcript. */
const TAIL_LIMIT = 16_384;

function appendTail(existing: string, chunk: string): string {
  const combined = existing + chunk;
  return combined.length > TAIL_LIMIT ? combined.slice(combined.length - TAIL_LIMIT) : combined;
}

/** Polls until the edge answers liveness, giving up early when the child is already gone — a child
 *  that exited immediately (a missing dll, a rejected setting) is never going to answer, and waiting
 *  the full budget for it reports a timeout in place of the real cause. */
async function waitForLive(
  baseAddress: string,
  timeoutMs: number,
  deadChild: () => string | null,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    const dead = deadChild();
    if (dead !== null) throw new Error(`edge process is gone before it became live: ${dead}`);

    try {
      const response = await fetch(`${baseAddress}/health/live`);
      if (response.status === 200) return;
      lastError = new Error(`/health/live returned ${response.status}`);
    } catch (thrown) {
      lastError = thrown;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`edge did not become live within ${timeoutMs}ms: ${String(lastError)}`);
}

/** Spawns the edge as a genuine child process — `dotnet <built dll>` over a real bound socket, in
 *  front of a genuinely separate workload process `spawnHostedWorkload` also spawns. `otlpEndpoint`,
 *  when given, points both processes at the same collector — S8's own hook; every other caller
 *  omits it and gets today's no-telemetry behaviour on both sides unchanged. */
export async function spawnHostedEdge(otlpEndpoint?: string): Promise<SpawnedHostedEdge> {
  const workload = await spawnHostedWorkload(otlpEndpoint);
  const port = await freePort();
  const dllPath = process.env["GAME_EDGE_DLL"] ?? DEFAULT_DLL;

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    ASPNETCORE_ENVIRONMENT: "Production",
    GameEdge__WorkloadBaseAddress: workload.target.baseAddress,
    GameEdge__ForwardTimeout: "00:00:10",
    GameEdge__LivenessTimeout: "00:00:05",
    // Read by `AddPlatformObservability` (`PlatformObservabilityExtensions.ResolveIdentity`) when
    // there is no `PlatformOptions` singleton already registered ahead of it — the same section
    // `AddPlatformWebHost` itself reads.
    ...(otlpEndpoint ? { Platform__Telemetry__OtlpEndpoint: otlpEndpoint } : {}),
  };

  // The content root ASP.NET resolves appsettings.json against defaults to the process's working
  // directory, not the assembly's — `dotnet <dll>` alone leaves it wherever the caller happens to
  // be running from.
  const child: ChildProcessWithoutNullStreams = spawn("dotnet", [dllPath], {
    cwd: dirname(dllPath),
    env,
  });

  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr = appendTail(stderr, chunk.toString("utf8"));
  });

  // Read, not ignored. The edge writes its whole log — startup, every request, every outbound call —
  // to stdout, roughly 70 KB for this fixture's ten steps, and an unread pipe stops draining at the
  // operating system's 64 KB buffer: the child then blocks inside its own logger. Reading it also
  // puts the edge's own account of a failed startup into the error below, where stderr is empty.
  let stdout = "";
  child.stdout.on("data", (chunk: Buffer) => {
    stdout = appendTail(stdout, chunk.toString("utf8"));
  });

  let hasExited = false;
  child.once("exit", () => {
    hasExited = true;
  });

  // Without a listener Node escalates a spawn failure — `dotnet` not on PATH is the likely one — to
  // an uncaught exception, which takes the whole test worker down instead of failing this call.
  let spawnError: string | null = null;
  child.once("error", (thrown: Error) => {
    spawnError = thrown.message;
    hasExited = true;
  });

  const baseAddress = `http://127.0.0.1:${port}`;

  const deadChild = (): string | null => {
    if (spawnError !== null) return `could not spawn 'dotnet ${dllPath}': ${spawnError}`;
    if (hasExited) return `edge exited with code ${String(child.exitCode)}`;
    return null;
  };

  try {
    await waitForLive(baseAddress, 30_000, deadChild);
  } catch (thrown) {
    if (!hasExited) child.kill("SIGKILL");
    workload.forceKill();
    throw new Error(
      `${thrown instanceof Error ? thrown.message : String(thrown)}`
        + `; edge stderr:\n${stderr}\nedge stdout (last ${TAIL_LIMIT} chars):\n${stdout}`,
    );
  }

  const target: HostedTarget = {
    baseAddress,

    async shutdown(): Promise<Outcome<void, ShutdownError>> {
      // The dump comes from the workload; the edge itself has nothing to flush, so it is
      // terminated once the workload's own graceful shutdown (which writes the dump) completes.
      const result = await workload.target.shutdown();
      if (!hasExited) {
        child.kill("SIGTERM");
      }
      return result;
    },

    async readDump() {
      return workload.target.readDump();
    },
  };

  return {
    target,
    forceKill(): void {
      if (!hasExited) child.kill("SIGKILL");
      workload.forceKill();
    },
  };
}
