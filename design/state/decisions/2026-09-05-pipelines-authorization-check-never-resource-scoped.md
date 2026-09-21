# decision/2026-09-05-pipelines-authorization-check-never-resource-scoped
Date: 2026-09-05
Anchor: 2026-09-05 — The pipeline's authorization check is never resource-scoped
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-05 — The pipeline's authorization check is never resource-scoped"

## Claim
Context — `IAuthorizationEvaluator.EvaluateAsync` takes a `ResourceRef?`, and the fixed order's step 4 has to pass something. Mcp scopes its check to the parsed resource because the SDK parsed arguments against a declared schema before any producer code ran; an HTTP endpoint convention has no equivalent — the identifier is in a route value or a body at the moment the declaration is written.

Chosen — **the pipeline passes no resource**, and an endpoint whose authorization is genuinely per-resource makes a second, explicit `EvaluateAsync` call in its handler with the reference it constructed. Recorded as I-R7, enforced by instruction.

Rejected — **the declaration naming a resource type and the route parameter that supplies the id**, so Hosting could build the `ResourceRef` from matched route values. Both strings are opaque to Platform, so this is no more product knowledge than carrying a `PermissionName`, and it was the option that made I-R7 unnecessary. Rejected because it buys less than it looks: an identifier carried in a request body cannot be expressed at all, so those endpoints re-check in the handler regardless — the second check returns for a subset rather than being eliminated — and the resulting `ResourceRef` is only ever as good as the route template, which makes a renamed route parameter a silent widening of a check rather than a compile error. It also puts Hosting in the business of reading a route value to mint an identifier, against *Module boundaries* § 3's "it knows no product concept".

Reversibility — cheap. An overload of `RequiresPlatformAuthorization` taking the two strings adds the rejected shape later without breaking a call site, and I-R7 becomes narrower rather than false.
