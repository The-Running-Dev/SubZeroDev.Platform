# decision/2026-08-29-provenance-is-a-named-type-well-known-names-are-public-surface
Date: 2026-08-29
Anchor: 2026-08-29 — Provenance is a named type, and the well-known names are Platform's own public surface
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-29 — Provenance is a named type, and the well-known names are Platform's own public surface"

## Claim
Context — `/contract` for D5. `10-design.md` requires an authorization decision to carry "the non-empty set of providers that produced the grant" and an entitlement decision to name its contributors, and it requires the publication of a shared row to be "an explicit, permissioned, audited write" and an audit-write failure to degrade readiness. None of those is expressible without something to name: the design states the requirement and not the handle.

Chosen — `PermissionProviderName` and `EntitlementContributorName` as opaque name structs on the shape `ModuleName` and `HealthCheckName` already establish; `ResourceRef(Type, Id)` as the one type both a resource-scoped authorization check and an audit record name a resource with; `AuditAction` as a name struct; and three well-known name classes on the `PlatformHealthChecks` precedent — `PlatformPermissions` (`Platform.Tenancy.ShareResource`, `Platform.Organizations.Administer`, `Platform.Audit.Read`), `PlatformAuditActions`, and `PlatformHealthChecks.AuditSink` (`platform.audit.sink`). All are public surface, because a consumer's policy and an operator's probe body both refer to them by name.

Rejected — **bare strings for provider and contributor provenance**, which needs no new type; rejected because the union's entire risk control is that a decision names its source, and a raw string is one typo away from attributing a grant to a provider that did not make it, with nothing to catch it. **Two resource types, one for authorization and one for audit**; rejected because the audit record of a denial must name the same resource the check named, and two types is a mapping that can disagree. **Leaving the publication permission unnamed for a slice to invent**; rejected because an unnamed permission is one each consumer names differently, and I-A3 makes an undeclared name a startup failure — so the name has to exist before the first slice, not after it.

Reversibility — cheap for each name's spelling while packages are 0.x; expensive for the decision that provenance is typed at all, since it is what every audit and diagnostic reading a decision depends on.
