/**
 * G3 S7 — the real game edge, built and run as the deployment runs it: `dotnet <dll>` over a bound
 * socket, in `PassThrough` credential mode, in front of a workload. It is started on a port chosen
 * before Adventures, because Adventures builds its OIDC `redirect_uri` from the address a player's
 * browser reaches — which here is the edge.
 */
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { startChild, waitUntilReady } from "./child.js";

const configuration = process.env["GAME_EDGE_CONFIGURATION"] ?? "Debug";
const defaultDll = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
  "..",
  "game-edge",
  "SubZeroDev.Platform.GameEdge",
  "bin",
  configuration,
  "net10.0",
  "SubZeroDev.Platform.GameEdge.dll",
);

export function edgeDll(): string {
  return process.env["GAME_EDGE_DLL"] ?? defaultDll;
}

export interface RunningEdge {
  readonly baseAddress: string;
  stop(): Promise<void>;
}

/** Starts the edge on `port`, forwarding to `workloadBaseAddress`, and waits for `/health/live`. */
export async function startEdge(port: number, workloadBaseAddress: string): Promise<RunningEdge> {
  const dll = edgeDll();
  const baseAddress = `http://127.0.0.1:${port}`;
  const child = startChild("dotnet", [dll], {
    // The content root `appsettings.json` resolves against is the working directory, not the assembly's.
    cwd: dirname(dll),
    label: "game edge",
    env: {
      ...process.env,
      ASPNETCORE_URLS: baseAddress,
      ASPNETCORE_ENVIRONMENT: "Production",
      GameEdge__CredentialMode: "PassThrough",
      GameEdge__WorkloadBaseAddress: workloadBaseAddress,
      GameEdge__ForwardTimeout: "00:00:20",
      GameEdge__ReadinessTimeout: "00:00:05",
    },
  });

  try {
    await waitUntilReady(child, "game edge", 30_000, async () => (await fetch(`${baseAddress}/health/live`)).status === 200);
  } catch (thrown) {
    await child.stop();
    throw thrown;
  }
  return { baseAddress, stop: () => child.stop() };
}
