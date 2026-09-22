# decision/2026-09-21-connectionunauthenticated-dropped-from-mcperror
Date: 2026-09-21
Anchor: 2026-09-21 — `ConnectionUnauthenticated` is dropped from `McpError`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — `ConnectionUnauthenticated` is dropped from `McpError`"

## Claim
Context — `/align` after D5 found the variant declared in `20-contract.md` § *Error semantics* 8 and in `McpError.cs`, and raised nowhere. A principal change on an established session is answered by the adopted SDK's own 403 at the transport (`McpPrincipalBindingMiddleware`) before any tool is reached, and a connection with no principal is `Anonymous` and authorized per call.

Chosen — remove the variant from the code and the contract, and state in § 8 where each case is answered instead.

Rejected — keeping it as a reserved variant, because a declared error nothing raises tells a caller to handle a case that cannot reach it; and raising it from Platform code, which would duplicate the SDK's transport answer with a second one.

Reversibility — cheap; re-adding a variant is additive.
