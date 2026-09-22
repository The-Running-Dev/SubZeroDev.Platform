# decision/2026-08-24-mcp-tool-catalogue-freezes-at-startup-secret-shaped-parameter-fails
Date: 2026-08-24
Anchor: 2026-08-24 — The MCP tool catalogue freezes at startup, and a secret-shaped parameter fails startup
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The MCP tool catalogue freezes at startup, and a secret-shaped parameter fails startup"

## Claim
Context — two inherited non-negotiables — authentication belongs to the connection and never to a call, and exposure is opt-in per tool, default closed. The brief additionally requires that no tool schema or call accepts a secret parameter, and that a registered-but-unexposed tool is neither listed nor callable.

Chosen — tools register and are exposed at startup, from any number of producers, and the catalogue is **immutable afterwards**. Registration and exposure are separate facts. Every parameter name in every registered schema is tested against the same fixed marker set the redaction boundary uses, and a match fails startup with a named error, in the shape Core already uses for a bad module graph. A tool whose required permission is not a registered name fails startup too. Unregistered and registered-but-unexposed both answer "unknown tool", never "forbidden", which would confirm existence.

Rejected — **a mutable catalogue supporting runtime registration**, which the Automator will eventually want so a plugin install needs no restart; rejected because freezing lets both safety properties be checked once with a named startup failure rather than on every registration with a runtime error nobody sees, and because it removes a catalogue-mutation-versus-invocation race from the path every call takes. The reversal is available in the useful direction only: a mutable catalogue can replace a frozen one without changing a consumer, and the reverse cannot.

Reversibility — cheap.
