# decision/2026-09-25-platform-validates-credentials-never-conducts-sign-in
Date: 2026-09-25
Anchor: 2026-09-25 — Platform validates credentials and never conducts a sign-in
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § Path 4 — a person signs in and signs out, and Platform is on neither path"

## Claim
Context — #94's done-when includes sign-out; `IAuthenticationRequest` carries headers only, and Mcp uses the same seam.

Chosen — Platform is a resource server. Interactive sign-in and sign-out are between the client and its issuer; no Platform host serves a sign-in page, callback, session or sign-out endpoint. An issued access token stays valid at Platform until expiry; grants do not ride in it.

Rejected — a backend-for-frontend host conducting the code exchange and holding a session.

Reversibility — cheap; a BFF can be added later as a module that is an ordinary client of Platform.
