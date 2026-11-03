/**
 * G3 S7 — the pinned Adventures server as a child process, against a database made for this run.
 * `scripts/prepare-adventures.mjs` has already put the pinned commit at `adventuresDirectory()` and
 * installed its dependencies; this starts `src/index.ts` there with the environment its own deployment
 * reads (`server/src/index.ts`) and nothing of Adventures' source is changed or imported.
 */
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import pg from "pg";
import { randomBytes } from "node:crypto";

import { startChild, waitUntilReady } from "./child.js";
import type { ManagedChild } from "./child.js";
import { freePort } from "./free-port.js";
import { CLIENT_ID, CLIENT_SECRET } from "./issuer.js";

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

const ADMIN_DATABASE_URL =
  process.env["ADVENTURES_PROOF_DATABASE_URL"] ??
  process.env["GAME_SERVICE_TEST_DATABASE_URL"] ??
  "postgresql://game_service:game_service@127.0.0.1:5432/game_service";

/** The pinned commit `adventures-pin.json` names. */
export function pinnedCommit(): string {
  const pin = JSON.parse(readFileSync(join(projectRoot, "adventures-pin.json"), "utf8")) as { commit: string };
  return pin.commit;
}

export function adventuresDirectory(): string {
  return process.env["ADVENTURES_DIR"] ?? join(projectRoot, ".adventures");
}

/** Fails with the one instruction that fixes it when the checkout is absent or at the wrong commit. */
export function assertPrepared(): void {
  const directory = adventuresDirectory();
  const marker = join(directory, `.prepared-${pinnedCommit()}`);
  if (!existsSync(marker)) {
    throw new Error(
      `Adventures is not prepared at ${directory} for commit ${pinnedCommit()}; run "npm run prepare-adventures" first.`,
    );
  }
}

export interface AdventuresConfiguration {
  /** Where the browser reaches the API: the edge's public address, which the OIDC callback is built from. */
  readonly apiUrl: string;
  /** The one browser origin Adventures treats as its own site. */
  readonly siteUrl: string;
  /** Extra origins allowed to call the API and to write to it. */
  readonly previewOrigins: readonly string[];
  readonly oidcIssuer: string;
  /** Trusted by Adventures' own process so it can dial the HTTPS test issuer. */
  readonly caCertificatePath: string;
}

export interface RunningAdventures {
  readonly baseAddress: string;
  stop(): Promise<void>;
}

/** Starts Adventures on a fresh database and waits until it answers; drops the database on `stop`. */
export async function startAdventures(configuration: AdventuresConfiguration): Promise<RunningAdventures> {
  assertPrepared();
  const database = `adventures_proof_${randomBytes(6).toString("hex")}`;
  const admin = new pg.Client({ connectionString: ADMIN_DATABASE_URL });
  await admin.connect();
  try {
    await admin.query(`create database ${database}`);
  } finally {
    await admin.end();
  }

  const url = new URL(ADMIN_DATABASE_URL);
  url.pathname = `/${database}`;

  const port = await freePort();
  const server = join(adventuresDirectory(), "server");
  let child: ManagedChild | undefined;
  try {
    child = startChild(process.execPath, ["node_modules/tsx/dist/cli.mjs", "src/index.ts"], {
      cwd: server,
      label: "Adventures",
      env: {
        ...process.env,
        NODE_ENV: "development",
        PORT: String(port),
        DATABASE_URL: url.toString(),
        MIGRATE_ON_START: "true",
        BACKUP_RETENTION_DAYS: "7",
        SITE_URL: configuration.siteUrl,
        API_URL: configuration.apiUrl,
        PREVIEW_ORIGINS: configuration.previewOrigins.join(","),
        CAMPAIGNS_DIR: join(adventuresDirectory(), "public", "campaigns"),
        OIDC_ISSUER_URL: configuration.oidcIssuer,
        OIDC_CLIENT_ID: CLIENT_ID,
        OIDC_CLIENT_SECRET: CLIENT_SECRET,
        NODE_EXTRA_CA_CERTS: configuration.caCertificatePath,
      },
    });

    const baseAddress = `http://127.0.0.1:${port}`;
    await waitUntilReady(child, "Adventures", 90_000, async () => (await fetch(`${baseAddress}/api/me`)).status === 200);
    const started = child;
    return {
      baseAddress,
      async stop() {
        await started.stop();
        await dropDatabase(database);
      },
    };
  } catch (thrown) {
    await child?.stop();
    await dropDatabase(database);
    throw thrown;
  }
}

async function dropDatabase(name: string): Promise<void> {
  const admin = new pg.Client({ connectionString: ADMIN_DATABASE_URL });
  await admin.connect();
  try {
    await admin.query(`drop database if exists ${name} with (force)`);
  } finally {
    await admin.end();
  }
}
