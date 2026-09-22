# decision/2026-08-24-placement-recorded-for-nine-capabilities-under-adr-006-no-dependency
Date: 2026-08-24
Anchor: 2026-08-24 — Placement recorded for all nine capabilities under ADR-006, and no dependency taken
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — Placement recorded for all nine capabilities under ADR-006, and no dependency taken"

## Claim
Context — the brief makes it a completion condition that `/design` records framework-versus-module placement for every capability under ADR-006 with none left `Undecided`, and `AGENTS.md` requires every gap to be checked against existing packages before it is written, with the reason recorded either way.

Chosen — the placement table in `10-design.md` § *Module boundaries* 1 — framework seams for Identity's principal contract, Authorization, Tenancy, the shared entitlement question and Audit's contract; modules for Organizations, the audit store, Billing, Licensing, Mcp and the web shell. **No new dependency is taken by this design.** Token validation is the in-box ASP.NET Core authentication handlers; licence signature verification is `System.Security.Cryptography`; the deliberately shared resource, which is the hard half of tenancy, is not provided by any multi-tenancy library.

Rejected — **Finbuckle.MultiTenant**, which `minimal-platform-packages.md` §3a explicitly recommended adopting "when tenancy becomes a feature", and D5 is that stage — so this needs a reason rather than a preference. Two: the brief's non-goals forbid redesigning the settled tenant identifier, keys and implicit-tenant representation, and Finbuckle brings its own tenant-info model and store abstractions, so adopting it re-homes a shape D3 settled and G2 built on; and the part that is actually hard — a declared shareable type, an audited read-only escape, and the no-cross-tenant-write invariant — is not in the library, so its hard half would be written anyway on top of a model this repository did not choose. Its resolution strategies remain the design reference, and the resolver seam is shaped so a consumer could register a Finbuckle-backed resolver without Platform depending on it. **Supabase as an identity substrate**, recorded there as a live option for D5; not rejected and not chosen — the brief's non-goals put choosing or operating an identity substrate out of scope, so it becomes a deployment choice behind the authentication seam, which is what "decide neither now" wanted and what "depend on the protocol, not the vendor" requires. **A licensing library**; rejected because taking one means adopting its document format as Platform's public licence format.

Reversibility — cheap for each rejection — every one leaves the seam that would accept the package later. Expensive for the placement table, which every D5 contract is written against.
