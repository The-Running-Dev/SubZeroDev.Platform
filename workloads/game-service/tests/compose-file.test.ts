/**
 * S3.15 — the committed compose file provisions PostgreSQL on loopback, with `UTF8` encoding and
 * an explicit initdb locale, and starts nothing else. A structural check on the committed text,
 * not a YAML-schema validation — the file is small and hand-authored, and the properties this
 * criterion names are exactly the ones worth pinning against silent drift.
 *
 * The other half of S3.15 — that the file actually brings up a reachable server — is proven by
 * every other test in this suite: they all connect to the server this file's `docker compose up`
 * starts, and fail outright if it is not there.
 */
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const COMPOSE_PATH = resolve(dirname(fileURLToPath(import.meta.url)), "../docker-compose.yml");

/** The `services:` block only, up to the next top-level (0-indent) key — so a check scoped to
 *  "one service" is not confused by `volumes:`'s own top-level named-volume declaration, whose
 *  entries share `services:` entries' 2-space indent. */
function servicesBlock(text: string): string {
  const withoutComments = text.replace(/^\s*#.*$/gm, "");
  // `(?![\s\S])` is the true end-of-string assertion — JS `\Z` is a literal "Z" character, not an
  // anchor (unlike Python/PCRE), so it can never fire and this fallback would silently depend on
  // `services:` never being the last top-level block in the file.
  const match = /^services:\n([\s\S]*?)(?=^\S|(?![\s\S]))/m.exec(withoutComments);
  if (!match?.[1]) throw new Error("docker-compose.yml has no top-level services: block");
  return match[1];
}

describe("S3.15 — the compose file provisions PostgreSQL, and nothing else", () => {
  const text = readFileSync(COMPOSE_PATH, "utf8");
  const services = servicesBlock(text);

  it("defines exactly one service, running a postgres image", () => {
    const serviceHeaders = services.match(/^ {2}\S+:$/gm) ?? [];
    expect(serviceHeaders).toHaveLength(1);
    expect(services).toMatch(/image:\s*postgres:/);
  });

  it("binds the published port to loopback only", () => {
    expect(services).toMatch(/"127\.0\.0\.1:\d+:\d+"/);
    expect(services).not.toMatch(/^\s*-\s*"?\d+:\d+"?\s*$/m); // bare "host:container" binds every interface
  });

  it("pins the server encoding to UTF8 and sets an explicit initdb locale", () => {
    expect(services).toMatch(/POSTGRES_INITDB_ARGS:.*--encoding=UTF8/);
    expect(services).toMatch(/POSTGRES_INITDB_ARGS:.*--locale=\S+/);
  });

  it("names no second image, build, or command that could start a workload instance", () => {
    expect(services.match(/^\s*image:/gm) ?? []).toHaveLength(1);
    expect(services).not.toMatch(/^\s*(build|command|entrypoint):/m);
  });
});
