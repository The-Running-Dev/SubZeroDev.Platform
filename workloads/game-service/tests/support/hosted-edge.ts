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

async function waitForLive(baseAddress: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
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
 *  front of a genuinely separate workload process `spawnHostedWorkload` also spawns. */
export async function spawnHostedEdge(): Promise<SpawnedHostedEdge> {
  const workload = await spawnHostedWorkload();
  const port = await freePort();
  const dllPath = process.env["GAME_EDGE_DLL"] ?? DEFAULT_DLL;

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    ASPNETCORE_ENVIRONMENT: "Production",
    GameEdge__WorkloadBaseAddress: workload.target.baseAddress,
    GameEdge__ForwardTimeout: "00:00:10",
    GameEdge__LivenessTimeout: "00:00:05",
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
    stderr += chunk.toString("utf8");
  });

  let hasExited = false;
  child.once("exit", () => {
    hasExited = true;
  });

  const baseAddress = `http://127.0.0.1:${port}`;

  try {
    await waitForLive(baseAddress, 30_000);
  } catch (thrown) {
    child.kill("SIGKILL");
    workload.forceKill();
    throw new Error(`${thrown instanceof Error ? thrown.message : String(thrown)}; edge stderr:\n${stderr}`);
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
