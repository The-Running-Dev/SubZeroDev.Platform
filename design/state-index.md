# Design state — index

The corpus-wide facts a single record cannot state alone: the unit table of contents, and the
four reverse edges `design/10-design.md` § *Derived* forbids writing onto a record
(`Invariant.BoundBy`, `Contract.Consumers`, `Decision.Affects`, `Question.Affects`). Every
table below is a **projected** marked region (`AGENTS.md` § *Marked regions*) — rendered by
`tools/Update-DesignProjection.ps1` from `design/state/`, and overwritten on every
regeneration. Nothing here is written by hand.

This corpus covers the live D5 document chain (`design/00-brief.md` through `design/90-decisions.md`)
only — no module-level unit, contract or question records exist yet, so `Held by` in
`20-contract.md`'s Invariants table and the sections below that depend on them stay empty until
that follow-on work is done. An empty table here means no record exists yet, not that nothing is
true.

## Units

<!-- units:start -->
| Id | Kind | Anchor |
|---|---|---|
| `unit/document/00-brief` | document | `design/00-brief.md` |
| `unit/document/10-design` | document | `design/10-design.md` |
| `unit/document/20-contract` | document | `design/20-contract.md` |
| `unit/document/30-slices` | document | `design/30-slices.md` |
| `unit/document/90-decisions` | document | `design/90-decisions.md` |
| `unit/document/state-index` | document | `design/state-index.md` |
| `unit/script/read-designstate` | script | `tools/Read-DesignState.ps1` |
| `unit/script/test-designdrift` | script | `tools/Test-DesignDrift.ps1` |
| `unit/script/test-gatescache` | script | `tools/Test-GatesCache.ps1` |
| `unit/script/update-designprojection` | script | `tools/Update-DesignProjection.ps1` |
<!-- units:end -->

## Invariants — bound by

<!-- bound-by:start -->
| Invariant | Bound by |
|---|---|
| I-A1 | — |
| I-A2 | — |
| I-A3 | — |
| I-A4 | — |
| I-A5 | — |
| I-A6 | — |
| I-A7 | — |
| I-A8 | — |
| I-A9 | — |
| I-A10 | — |
| I-B1 | — |
| I-B2 | — |
| I-B3 | — |
| I-B4 | — |
| I-B5 | — |
| I-B6 | — |
| I-B7 | — |
| I-C1 | — |
| I-C2 | — |
| I-C3 | — |
| I-C4 | — |
| I-C5 | — |
| I-C6 | — |
| I-C7 | — |
| I-C8 | — |
| I-C9 | — |
| I-I1 | — |
| I-I2 | — |
| I-I3 | — |
| I-I4 | — |
| I-I5 | — |
| I-I6 | — |
| I-L1 | — |
| I-L2 | — |
| I-L3 | — |
| I-L4 | — |
| I-L5 | — |
| I-L6 | — |
| I-L7 | — |
| I-L8 | — |
| I-L9 | — |
| I-M1 | — |
| I-M2 | — |
| I-M3 | — |
| I-M4 | — |
| I-M5 | — |
| I-M6 | — |
| I-M7 | — |
| I-M8 | — |
| I-M9 | — |
| I-M10 | — |
| I-M11 | — |
| I-O1 | — |
| I-O2 | — |
| I-O3 | — |
| I-O4 | — |
| I-O5 | — |
| I-O6 | — |
| I-O7 | — |
| I-O8 | — |
| I-OB1 | — |
| I-R1 | — |
| I-R2 | — |
| I-R3 | — |
| I-R4 | — |
| I-R5 | — |
| I-R6 | — |
| I-R7 | — |
| I-T1 | — |
| I-T2 | — |
| I-T3 | — |
| I-T4 | — |
| I-T5 | — |
| I-T6 | — |
| I-T7 | — |
| I-T8 | — |
| I-U1 | — |
| I-U2 | — |
| I-U3 | — |
| I-U4 | — |
| I-U5 | — |
| I-U6 | — |
| I-U7 | — |
| I-U8 | — |
| I-U9 | — |
| I-U10 | — |
| I-W1 | — |
<!-- bound-by:end -->

## Contracts — consumers

<!-- consumers:start -->
| Contract | Consumers |
|---|---|
| _(no contract records yet)_ | |
<!-- consumers:end -->

## Decisions — in force for

<!-- decision-affects:start -->
| Decision | In force for |
|---|---|
| decision/2026-08-24-ambient-principal-is-total-principal-id-is-issuer-plus-subject | `unit/document/90-decisions` |
| decision/2026-08-24-audit-joins-actions-transaction-two-classes-when-not | `unit/document/90-decisions` |
| decision/2026-08-24-audit-record-has-no-payload-redaction-boundary-moves-to-core | `unit/document/90-decisions` |
| decision/2026-08-24-billing-and-licensing-contribute-to-one-entitlement-seam-union | `unit/document/90-decisions` |
| decision/2026-08-24-caller-who-may-not-see-a-resource-is-told-it-does-not-exist | `unit/document/90-decisions` |
| decision/2026-08-24-composition-profile-is-declared-validated-at-startup-and-fingerprinted | `unit/document/90-decisions` |
| decision/2026-08-24-entitlement-is-decided-at-admission-and-carried-with-work | `unit/document/90-decisions` |
| decision/2026-08-24-framework-owns-questions-modules-own-answers | `unit/document/90-decisions` |
| decision/2026-08-24-licensed-installation-is-the-database-expiry-and-grace-stored-verified | `unit/document/90-decisions` |
| decision/2026-08-24-mcp-tool-catalogue-freezes-at-startup-secret-shaped-parameter-fails | `unit/document/90-decisions` |
| decision/2026-08-24-organization-holds-a-tenant-it-is-not-one | `unit/document/90-decisions` |
| decision/2026-08-24-placement-recorded-for-nine-capabilities-under-adr-006-no-dependency | `unit/document/90-decisions` |
| decision/2026-08-24-tenant-escape-is-declared-type-plus-audited-read-only-scope | `unit/document/90-decisions` |
| decision/2026-08-29-durable-audit-sink-declares-itself-audit-table-keyed-by-event-id | `unit/document/90-decisions` |
| decision/2026-08-29-licence-tier-is-opaque-name-with-well-known-community-baseline | `unit/document/90-decisions` |
| decision/2026-08-29-module-packages-are-subzerodev-platform-capability | `unit/document/90-decisions` |
| decision/2026-08-29-platform-mcp-adopts-official-mcp-csharp-sdk-projects-at-boundary | `unit/document/90-decisions` |
| decision/2026-08-29-provenance-is-a-named-type-well-known-names-are-public-surface | `unit/document/90-decisions` |
| decision/2026-08-29-tools-exposure-is-not-a-field-its-producer-fills-in | `unit/document/90-decisions` |
| decision/2026-08-31-iauditable-createdby-becomes-non-null-under-total-principal | `unit/document/90-decisions` |
| decision/2026-09-02-isharedreadscopefactory-gains-isopenfor-tentity | `unit/document/90-decisions` |
| decision/2026-09-03-consumer-declaring-operated-supplies-profiles-two-registrations | `unit/document/90-decisions` |
| decision/2026-09-03-entitlement-contributor-registers-under-keyed-di-slot | `unit/document/90-decisions` |
| decision/2026-09-03-frozen-contributor-set-reaches-fingerprint-as-parameter | `unit/document/90-decisions` |
| decision/2026-09-05-endpoint-declares-its-permission-and-feature-undeclared-fails-startup | `unit/document/90-decisions` |
| decision/2026-09-05-pipelines-authorization-check-never-resource-scoped | `unit/document/90-decisions` |
| decision/2026-09-19-d5-package-delivery-uses-approved-lockstep-version | `unit/document/90-decisions` |
| decision/2026-09-19-d5-retains-the-consumer-evidence-objection | `unit/document/90-decisions` |
| decision/2026-09-21-10-design-open-questions-2-resolved-separate-frontend-build | `unit/document/90-decisions` |
| decision/2026-09-21-auditing-is-not-a-pipeline-step-evaluator-denials-writer-actions | `unit/document/90-decisions` |
| decision/2026-09-21-byactorasync-filters-on-issuer-and-subject | `unit/document/90-decisions` |
| decision/2026-09-21-connectionunauthenticated-dropped-from-mcperror | `unit/document/90-decisions` |
| decision/2026-09-21-endpoints-undeclared-permission-fails-startup-as-unregisteredpermission | `unit/document/90-decisions` |
| decision/2026-09-21-notamember-names-principal-administrative-action-targets-never-caller | `unit/document/90-decisions` |
| decision/2026-09-21-organizations-and-billing-gain-retryable-storeunavailable | `unit/document/90-decisions` |
| decision/2026-09-21-platform-takes-two-endpoint-exemptions-probes-and-mcp-transport | `unit/document/90-decisions` |
| decision/2026-09-21-required-audit-failures-are-surfaced-never-discarded | `unit/document/90-decisions` |
| decision/2026-09-25-adr-009-supersedes-the-2026-08-10-identity-withdrawal | `unit/document/90-decisions` |
| decision/2026-09-25-every-permission-provider-takes-grants-from-a-revocable-source | `unit/document/10-design` |
| decision/2026-09-25-hosted-and-self-hosted-are-two-methods-when-surfaces-differ | `unit/document/10-design` |
| decision/2026-09-25-identity-gains-generic-bearer-path-asymmetric-only-background-key-refresh | `unit/document/10-design` |
| decision/2026-09-25-platform-validates-credentials-never-conducts-sign-in | `unit/document/10-design` |
| decision/2026-09-25-token-validation-adopts-microsoft-identitymodel-corrects-in-box-claim | `unit/document/10-design` |
| decision/2026-09-25-vendor-dialect-ships-as-configuration-source-packages | `unit/document/10-design` |
<!-- decision-affects:end -->

## Questions — blocks and answered

<!-- question-affects:start -->
| Question | Blocks | Answered |
|---|---|---|
| _(no question records yet)_ | | |
<!-- question-affects:end -->

## Outstanding

From a checkout with no network: the outstanding work, its order, and each item's criteria,
mirrored from GitHub by `tools/Update-WorkMirror.ps1` into `WorkRef` records
(`design/10-design.md` § *WorkRef*). **GitHub stays the authority** — this table is a mirror,
stale by default, and never cited as the reason work is or is not done. `Mirrored at` names the
commit the mirror was taken at; check it against `git log` before trusting an entry that looks old.

<!-- outstanding:start -->
| Rank | Issue | Title | Criteria | Mirrored at |
|---|---|---|---|---|
| 11 | #24 | BarStrad's commercial model is undecided, and two of the brief's binding statements depend on the answer | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 12 | #25 | A live Discord webhook is committed in the BarStrad repository | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 13 | #26 | Three documents describe the scope question as still open, and it is not | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 18 | #57 | A feature/capability toggle seam, and what a licence gates | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 19 | #58 | Runtime settings, as distinct from startup configuration | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 20 | #59 | Soft delete as a contributed column | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 21 | #60 | Optimistic concurrency as a contributed column | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 22 | #61 | Seeding has callers but no infrastructure | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 23 | #62 | No consumer-side idempotency seam, where ABP has an inbox | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 24 | #63 | IPlatformModule has one lifecycle hook where ABP has seven | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 25 | #68 | Development-environment automatic migration application is promised and was never built | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 131 | #131 | /verify's `# verification: true` flag has never been added to this repository's workflows | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 151 | #151 | design/state/ does not exist, and the reader/checker/projector tooling built for it has nothing to read | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 187 | #187 | Audit retention is deferred out of D5 and the table grows without bound | 1, 2 | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| 224 | #224 | Re-enable McpInvocationTests.A_cancelled_call_writes_no_audit_record_and_never_completes_the_invoker once the SDK ships the cancellation fix | — | `4943900bd936ddd5ae4ce7f093d71caf09d30b32` |
| milestone/5 | #80 | Decide which slice set the public site's roadmap page renders | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/6 | #81 | Settle the @subzerodev registry reservations — only signing in settles it | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/6 | #85 | The engine writes the mutated blob to memory before persistence on two of its four write paths, and the four disagree with each other | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/6 | #86 | The Adventures POC already implements what G2, G3 and G4 will need, against this same engine release | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #90 | Platform gains an Identity package, and the decision behind it gets recorded | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #91 | Three documents still say Identity is undecided, and it no longer is | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #92 | Permissions must never travel inside the login token | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #93 | Accounts are matched on the provider's own identifier, never on email | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #94 | Configuration-only needs an escape hatch for providers that ignore the standard | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #95 | Ownership is checked where the thing is loaded, and refusal is an answer rather than a redirect | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #97 | D3 is built but not proven, and Adventures is the product that would prove it | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #98 | What is a principal, when one consumer's principal never has an account? | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #99 | The Automator's audience is unsettled, and its Identity row depends on it | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/3 | #100 | Identity is being replaced in SubZeroDev.Adventures, and that build is the evidence | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
| milestone/5 | #110 | The edge cannot front a streaming workload, and nothing says so | — | `7158bcdedec0a5b27d358bd598f865429e0c0e5c` |
<!-- outstanding:end -->
