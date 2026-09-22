# decision/2026-09-21-endpoints-undeclared-permission-fails-startup-as-unregisteredpermission
Date: 2026-09-21
Anchor: 2026-09-21 — An endpoint's undeclared permission fails startup as `UnregisteredPermission`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — An endpoint's undeclared permission fails startup as `UnregisteredPermission`"

## Claim
Context — § *Error semantics* 9 names `UnregisteredPermission` for a tool, an endpoint or any registration requiring an undeclared permission. The endpoint check threw `HostStartupError.Registration` instead, while Mcp's tool check used the named code.

Chosen — the endpoint check throws `HostStartupError.UnregisteredPermission`, carrying the `PermissionCatalogError` as the inner error. § 9's table now says which conditions are their own codes and which are registry errors wrapped by `Registration`.

Rejected — amending the doc to `Registration`, because one condition would then surface under two codes depending on whether a tool or an endpoint declared it.

Reversibility — cheap before external consumers match on the code.
