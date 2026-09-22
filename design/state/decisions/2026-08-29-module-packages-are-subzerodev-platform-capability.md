# decision/2026-08-29-module-packages-are-subzerodev-platform-capability
Date: 2026-08-29
Anchor: 2026-08-29 — Module packages are `SubZeroDev.Platform.<Capability>`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-29 — Module packages are `SubZeroDev.Platform.<Capability>`"

## Claim
Context — `10-design.md` names the six module units — Identity, Organizations, Billing, Licensing, the audit store and Mcp — but fixes a package name for only one of them, `Platform.Mcp`, and the contract has to name the assembly each declaration lives in.

Chosen — `SubZeroDev.Platform.Identity`, `.Organizations`, `.Billing`, `.Licensing`, `.Audit` and `.Mcp`, extending the convention `Platform.Mcp` already uses and the ecosystem prefix ADR-003 settles. The web shell takes no .NET package name, because it has no .NET package.

Rejected — **a distinguishing prefix or suffix for the module tier**, such as `Platform.Modules.*`; rejected because ADR-006's rule is structural and the architecture checks (I-C6, I-C7) enforce it over the resolved package graph, so a naming convention adds a second, weaker statement of the same fact that can disagree with the first. **`SubZeroDev.Platform.AuditStore`** for the audit store; rejected because the module is the only thing named Audit that ships, and the word "store" is the design's disambiguator against the framework's audit *contract*, which lives in Abstractions and needs no name of its own.

Reversibility — cheap while every package is 0.x and unpublished.
