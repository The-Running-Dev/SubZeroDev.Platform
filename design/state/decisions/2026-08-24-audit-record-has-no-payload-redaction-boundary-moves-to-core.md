# decision/2026-08-24-audit-record-has-no-payload-redaction-boundary-moves-to-core
Date: 2026-08-24
Anchor: 2026-08-24 — The audit record has no payload, and the redaction boundary moves to Core
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The audit record has no payload, and the redaction boundary moves to Core"

## Claim
Context — `platform-specification.md` lists "changed fields where appropriate" among audit fields. The brief requires tests that push representative secrets through every audited input surface and assert that neither values nor payloads reach the stored record or the logs.

Chosen — the audit record carries the brief's seven fields plus the actor kind and the record class. **No payload, no changed-field list, no free-form detail string.** The three caller-controlled strings — action, resource type, resource id — pass through the fixed redaction boundary before storage, and that boundary moves from Observability to Core — the framework package both modules already depend on, and not Abstractions, which exposes contracts only and acquires no implementation — so the Audit store module and Mcp reach the same one. It stays non-injectable; the D3 decision that made it fixed rather than configurable is unchanged.

Rejected — **the specification's fuller list including changed fields**, which is what an auditor usually asks for and where "we redact it" is the standard answer; rejected because with a payload field the brief's test asserts a property of the redaction rules and of every future caller's discipline, while without one it asserts a property of the type — and a changed-field list is the single most likely place for a credential to arrive by accident, because the field that changed is often the one that matters. A consumer wanting field-level change history builds it in its own tables, where the secret question is theirs.

Reversibility — cheap in the direction that should be resisted. Adding a payload later is easy; the whole value of the decision is in not doing it.
