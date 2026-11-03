/**
 * Shared child-process handling for the two processes this proof starts: a spawned child whose
 * stdout and stderr are always drained (an unread pipe fills at the operating system's buffer and
 * blocks the child inside its own logger), whose last output is kept for a failure message, and
 * which can be told from "still starting" to "already gone".
 */
import { spawn } from "node:child_process";
import type { ChildProcess } from "node:child_process";

/** Keeps the last `TAIL_LIMIT` characters of a child's output. */
const TAIL_LIMIT = 16_384;

export function appendTail(existing: string, chunk: string): string {
  const combined = existing + chunk;
  return combined.length > TAIL_LIMIT ? combined.slice(combined.length - TAIL_LIMIT) : combined;
}

export interface ManagedChild {
  readonly process: ChildProcess;
  /** The last output of the child, both streams. */
  output(): string;
  /** `null` while the child runs; otherwise why it is gone. */
  gone(): string | null;
  /** Stops the child and waits for it to be gone. */
  stop(): Promise<void>;
}

export function startChild(
  command: string,
  args: readonly string[],
  options: { cwd: string; env: NodeJS.ProcessEnv; label: string },
): ManagedChild {
  const child = spawn(command, [...args], { cwd: options.cwd, env: options.env, stdio: ["ignore", "pipe", "pipe"] });
  let tail = "";
  const keep = (chunk: Buffer): void => {
    tail = appendTail(tail, chunk.toString("utf8"));
  };
  child.stdout?.on("data", keep);
  child.stderr?.on("data", keep);

  let exited = false;
  let spawnError: string | null = null;
  const done = new Promise<void>((resolve) => {
    child.once("exit", () => {
      exited = true;
      resolve();
    });
    // Without a listener a spawn failure (the command not on PATH) becomes an uncaught exception.
    child.once("error", (thrown: Error) => {
      spawnError = thrown.message;
      exited = true;
      resolve();
    });
  });

  return {
    process: child,
    output: () => tail,
    gone: () => {
      if (spawnError !== null) return `${options.label} could not be spawned: ${spawnError}`;
      if (exited) return `${options.label} exited with code ${String(child.exitCode)}`;
      return null;
    },
    async stop() {
      if (exited) return;
      child.kill("SIGTERM");
      const timer = setTimeout(() => child.kill("SIGKILL"), 5_000);
      await done;
      clearTimeout(timer);
    },
  };
}

/** Polls until `probe` succeeds, failing early with the child's own account if it is already gone. */
export async function waitUntilReady(
  child: ManagedChild,
  label: string,
  timeoutMs: number,
  probe: () => Promise<boolean>,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const gone = child.gone();
    if (gone !== null) throw new Error(`${gone} before it was ready; output:\n${child.output()}`);
    try {
      if (await probe()) return;
    } catch {
      // not listening yet
    }
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  throw new Error(`${label} was not ready within ${timeoutMs}ms; output:\n${child.output()}`);
}
