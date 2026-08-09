/**
 * Regenerates `tests/fixtures/golden-transcript.json` from `runInProcess` over the committed
 * fixture — never run by the suite itself (`20-contract.md`'s persisted-schemas table: "never
 * rewritten by a passing test"). An explicit act, reviewed as a diff: `npm run regenerate-golden`.
 */
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { loadPublishedContract } from "@subzerodev/service-contract";

import { runInProcess } from "../src/replay.js";
import { REPLAY_FIXTURE } from "../tests/fixtures/replay-fixture.js";

const OUTPUT_PATH = fileURLToPath(new URL("../tests/fixtures/golden-transcript.json", import.meta.url));

const run = await runInProcess(REPLAY_FIXTURE, loadPublishedContract());
if (!run.ok) {
  process.stderr.write(`regenerate-golden-transcript: runInProcess failed: ${JSON.stringify(run.error)}\n`);
  process.exit(1);
}

writeFileSync(OUTPUT_PATH, `${JSON.stringify(run.value.transcript, null, 2)}\n`, "utf8");
process.stdout.write(`wrote ${run.value.transcript.length} entries to ${OUTPUT_PATH}\n`);
