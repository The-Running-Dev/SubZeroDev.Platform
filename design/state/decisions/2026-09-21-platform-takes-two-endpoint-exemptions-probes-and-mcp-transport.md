# decision/2026-09-21-platform-takes-two-endpoint-exemptions-probes-and-mcp-transport
Date: 2026-09-21
Anchor: 2026-09-21 — Platform takes two endpoint exemptions: the probes and the Mcp transport
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — Platform takes two endpoint exemptions: the probes and the Mcp transport"

## Claim
Context — the 2026-09-05 entry below says the probes are the only exemption Platform takes. The Mcp transport endpoint also carries `ExemptFromPlatformAuthorization`, because its permission varies per call and it authorizes and checks entitlement per tool call. That entry is not rewritten; this one amends it.

Chosen — amend the doc to the code. § 11 and the code comments now name both exemptions.

Rejected — declaring a transport-wide permission on the Mcp endpoint, because no single permission covers every tool, and one that did would be granted to everyone who may call any tool.

Reversibility — cheap; the exemption and the per-call check are both local to `Platform.Mcp`.
