# decision/2026-09-25-token-validation-adopts-microsoft-identitymodel-corrects-in-box-claim
Date: 2026-09-25
Anchor: 2026-09-25 — Token validation adopts Microsoft.IdentityModel; the 2026-08-24 "in-box" claim was wrong
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § 9. Building the tenancy and identity substrates, against adopting existing ones"

## Claim
Context — the 2026-08-24 placement entry said token validation is the in-box ASP.NET Core handlers; the installed shared framework 10.0.12 does not contain them.

Chosen — `Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect` under the generic bearer path; no IdentityModel type in Platform's public surface; the library's on-demand refresh called only from the refresh timer; licence re-checked at the version taken.

Rejected — `Microsoft.AspNetCore.Authentication.JwtBearer`; hand-rolling over `System.Security.Cryptography` and `System.Text.Json`.

Reversibility — cheap; no library type crosses the public surface.
