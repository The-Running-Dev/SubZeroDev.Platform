/**
 * S8.1/S8.2/S8.6 — a real OpenTelemetry Collector, not a hand-decoded stand-in. The edge's exporter
 * is hardcoded to OTLP/HTTP protobuf (`PlatformObservabilityExtensions.ConfigureOtlp`, outside this
 * slice's `Touches`) and the workload's is OTLP/HTTP JSON (`telemetry.ts`); a real collector accepts
 * both on the same OTLP/HTTP receiver and normalises them, so nothing here decodes either wire
 * format itself. Its `file` exporter writes each export request as its own OTLP JSON — trace and
 * span ids already lowercase hex there (checked against a real collector's own output directly,
 * not assumed from the proto3 JSON mapping for `bytes`, which this deviates from the same way the
 * JS SDK's own JSON exporter does — see `telemetry.ts`'s note).
 *
 * The binary's path comes from `OTEL_COLLECTOR_BIN`, set only in CI (`build.yml` downloads it before
 * the outbound-port block, the same step-ordering `dotnet`/`node` setup already uses). A test that
 * needs this skips itself when the variable is unset, so a local `npm test` with no collector
 * installed still runs everything else.
 */
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { connect, createServer } from "node:net";

export interface CollectedSpan {
  readonly name: string;
  readonly traceId: string;
  readonly spanId: string;
  readonly parentSpanId: string | null;
}

interface ProtoJsonSpan {
  readonly name: string;
  readonly traceId: string;
  readonly spanId: string;
  readonly parentSpanId?: string;
}

interface ProtoJsonExportRequest {
  readonly resourceSpans?: readonly {
    readonly scopeSpans?: readonly { readonly spans?: readonly ProtoJsonSpan[] }[];
  }[];
}

/** Every span the collector wrote to its output file across every export batch, oldest first. */
export function readCollectedSpans(outputPath: string): CollectedSpan[] {
  const text = readFileSync(outputPath, "utf8");
  const spans: CollectedSpan[] = [];
  for (const line of text.split("\n")) {
    if (line.trim().length === 0) continue;
    const parsed = JSON.parse(line) as ProtoJsonExportRequest;
    for (const resourceSpan of parsed.resourceSpans ?? []) {
      for (const scopeSpan of resourceSpan.scopeSpans ?? []) {
        for (const span of scopeSpan.spans ?? []) {
          spans.push({
            name: span.name,
            traceId: span.traceId,
            spanId: span.spanId,
            parentSpanId: span.parentSpanId ?? null,
          });
        }
      }
    }
  }
  return spans;
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

export interface RunningCollector {
  readonly otlpEndpoint: string;
  readonly outputPath: string;
  /** The collector's own stderr, accumulated so far — for a failure message, not for parsing. */
  stderr(): string;
  /** Sends the collector its shutdown signal and waits for exit, which is what flushes the `file`
   *  exporter's buffered writes — reading the output before this returns sees a partial file. */
  stop(): Promise<void>;
}

/** Spawns `otelcol-contrib` (or any OTLP-receiver-compatible collector build) against a config
 *  generated for this run: an OTLP/HTTP receiver on a free loopback port, a `file` exporter writing
 *  every trace export request it forwards. */
export async function startCollector(): Promise<RunningCollector> {
  const binary = process.env["OTEL_COLLECTOR_BIN"];
  if (!binary) {
    throw new Error("OTEL_COLLECTOR_BIN is not set — call this only behind a skipIf on that variable");
  }

  const directory = mkdtempSync(join(tmpdir(), "s8-otel-collector-"));
  const outputPath = join(directory, "spans.json");
  const configPath = join(directory, "config.yaml");
  const port = await freePort();

  const config = [
    "receivers:",
    "  otlp:",
    "    protocols:",
    "      http:",
    `        endpoint: 127.0.0.1:${port}`,
    "exporters:",
    "  file:",
    `    path: ${outputPath}`,
    // Every request in this suite produces more than one export batch (at minimum, the edge's and
    // the workload's own, arriving as separate OTLP POSTs) — the default `append: false` truncates
    // on every write, so only the last batch would survive to `readCollectedSpans`.
    "    append: true",
    "service:",
    "  telemetry:",
    "    logs:",
    "      level: info",
    "  pipelines:",
    "    traces:",
    "      receivers: [otlp]",
    "      exporters: [file]",
    "",
  ].join("\n");
  writeFileSync(configPath, config, "utf8");
  // The file exporter never writes an empty file up front — a reader before the first export sees
  // ENOENT rather than an empty document. Creating it here gives `readCollectedSpans` one shape
  // to depend on even if a test calls `stop()` before any span was ever exported.
  writeFileSync(outputPath, "", "utf8");

  const child: ChildProcessWithoutNullStreams = spawn(binary, ["--config", configPath]);

  // The collector's own log destination is not documented as fixed to one stream — captured from
  // both, since a diagnostic that reads the wrong one is worse than one that reads too much.
  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });
  child.stdout.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });
  let hasExited = false;
  child.once("exit", () => {
    hasExited = true;
  });

  function reachable(): Promise<boolean> {
    return new Promise((resolve) => {
      const socket = connect({ host: "127.0.0.1", port });
      const settle = (value: boolean) => {
        socket.destroy();
        resolve(value);
      };
      socket.setTimeout(500, () => settle(false));
      socket.once("connect", () => settle(true));
      socket.once("error", () => settle(false));
    });
  }

  const deadline = Date.now() + 15_000;
  let ready = false;
  while (Date.now() < deadline && !ready) {
    if (hasExited) {
      throw new Error(`collector exited before becoming ready; stderr:\n${stderr}`);
    }
    ready = await reachable();
    if (!ready) {
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
  if (!ready) {
    child.kill("SIGKILL");
    throw new Error(`collector did not become ready in time; stderr:\n${stderr}`);
  }

  return {
    otlpEndpoint: `http://127.0.0.1:${port}`,
    outputPath,
    stderr: () => stderr,
    async stop() {
      if (!hasExited) {
        child.kill("SIGTERM");
        await new Promise<void>((resolve) => child.once("exit", () => resolve()));
      }
    },
  };
}
