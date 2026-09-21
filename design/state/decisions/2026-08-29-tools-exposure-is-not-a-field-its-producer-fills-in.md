# decision/2026-08-29-tools-exposure-is-not-a-field-its-producer-fills-in
Date: 2026-08-29
Anchor: 2026-08-29 — A tool's exposure is not a field its producer fills in
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-29 — A tool's exposure is not a field its producer fills in"

## Claim
Context — `10-design.md` § *Data model* 8 describes one `ToolRegistration` carrying the name, the producer, the schema, the required permission and whether it is exposed, and requires exposure to be configuration, resolved at startup, default closed.

Chosen — two types. `ToolDefinition` is what a producer supplies and carries no exposure; `ToolRegistration` is what the catalogue produces by combining a definition with configuration, and it carries the producer and the exposure. `IToolCatalogue` exposes only the exposed set and an exposure-respecting lookup — no `All`, no exposure-ignoring `TryGet`.

Rejected — **the design's single type with an `IsExposed` field**, which is one type rather than two; rejected because a producer would then supply a value the catalogue must ignore, and a field that is ignored is a trap — it makes default-closed a rule every present and future producer has to remember, which is exactly the automatic-exposure-on-install that `second-consumer-packages.md` §4 refuses. **A catalogue that can enumerate every registration for diagnostics**; rejected because an enumeration of hidden tools is the disclosure I-M4 exists to prevent, and a diagnostic surface is where that leak would be least examined.

Reversibility — cheap. Merging the two types later is additive for a caller that only reads `ToolRegistration`.
