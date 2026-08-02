---
sidebar_position: 11
sidebar_label: Tenancy, Billing, Licensing
---

> **Moved from the ecosystem specification staging tree**
> (`SubZeroDev/Specs/SubZeroDev.Platform/10-tenancy-billing-licensing.md`), which names this repository as its
> destination. Content is unchanged; only this provenance note and the front matter
> were added. See [Platform Identity](platform-identity.md) for why it moved.

# Tenancy, Billing, and Licensing

Split from the original `10-security-tenancy-billing.md`. The security half moved to
`SubZeroDev.Automator/10-security-model.md`, because enforcement is the Automator's job while the
commercial primitives below belong to Platform.

**Phase:** none of this is built before Phase 8. It is specified here so the shapes are agreed, not
so it is implemented early. Building tenancy into the first schema is far cheaper than retrofitting
it; building billing before a paying customer is not.

## Tenancy

Platform tenancy is optional. A single-user local deployment must not require artificial tenant
setup — no synthetic "default tenant" a user has to understand.

For SaaS, these are tenant-scoped:

- users
- workflows
- plugins
- executions
- schedules
- artifacts
- secrets
- billing
- agents

Cross-tenant operations require explicit platform-level permissions.

### The decision that is expensive to defer

Whether a tenant identifier appears in the first database schema is not a Phase 8 decision, even
though tenancy itself is. Adding a nullable tenant column later is easy; adding tenant _isolation_
to queries, storage paths, and secret scopes after data exists is a migration with correctness risk
on every table at once.

Recommendation: carry a tenant identifier from the first schema, defaulted to a single implicit
tenant in local mode and never surfaced in the UI until tenancy ships. Do not build tenant
management, invitations, or switching until Phase 8.

## Billing

Automator SaaS may meter:

- executions
- execution minutes
- active agents
- artifact storage
- premium plugins
- API and MCP usage

Billing is provided by Platform, not embedded in plugin logic. A plugin must never know whether the
caller is paying.

Billing must not be required for self-hosted use, and no code path outside the billing module may
branch on subscription state — otherwise the self-hosted build carries commercial logic it cannot
satisfy.

### Metering caution

"Execution minutes" is the only metric here that requires the execution path to be
billing-aware, because it needs accurate start and stop timestamps even when a run is killed,
crashes, or has its agent disappear. Those are precisely the paths where timing is least reliable
(see the orphan-execution gap recorded in `REVIEW.md`, C14). If metering ships, it should meter what
is durably recorded — completed executions and stored bytes — rather than wall-clock time whose
accuracy depends on failure handling.

## Licensing

Separate from subscription billing. Billing answers "is this account paid up"; licensing answers
"is this installation permitted to run this build", which self-hosted deployments need offline.

Capabilities:

- license keys
- signed license documents
- offline validation
- machine activation
- seat limits
- feature entitlements
- expiration
- trial licenses
- revocation lists when online

Open-source and community modules must not be technically coupled to online license checks unless
product policy explicitly requires it. An offline-capable homelab deployment that stops working
because a license server is unreachable is a support burden and a reputational cost.

## Decided

**Multi-tenancy in the first schema: carry the column, defer the feature.** A nullable tenant
identifier from the first migration, defaulted to a single implicit tenant and never surfaced until
tenancy ships. Adding the column later is trivial; adding tenant _isolation_ to queries, storage
paths, and secret scopes after data exists is a correctness migration touching every table at once.

## Billing provider

**Paddle first, behind the Platform billing abstraction.**

Paddle acts as merchant of record: it sells to the customer, and it owns sales tax registration,
calculation, remittance, and liability across every jurisdiction. Stripe is a payment processor —
cheaper per transaction, and it leaves you as the merchant of record.

The fee gap is roughly two percentage points. The compliance gap is EU VAT, UK VAT, and US state
sales tax thresholds, each with its own registration trigger and filing cadence. For a small team
selling a developer tool internationally, two points is far cheaper than the accounting work, and
much cheaper than getting it wrong.

**What would change this answer:** a shift to a handful of large invoiced B2B contracts. At that
shape, tax handling is per-contract anyway, volume makes the fee difference material, and Stripe or
direct invoicing wins. Paddle also vets what it will sell, so approval is a prerequisite rather than
a formality.

Nothing outside the billing module may branch on provider, so this remains a swap rather than a
migration.

### Metered dimensions

Meter **completed executions** and **stored artifact bytes**. Both are durably recorded and
independently verifiable by the customer.

Do not meter execution minutes. As noted above, wall-clock accuracy depends on the failure paths
where timing is least reliable — a killed run, a crashed agent, an expired lease — and a bill nobody
can reproduce is worse than a coarser one they can.

## License model

**Open-core, feature-tiered, enforced per installation, with agent count as the paid dimension.**

| Edition    | Licence                                | Gates                                         |
| ---------- | -------------------------------------- | --------------------------------------------- |
| Community  | **None. No licence code path exists.** | Single execution node; every plugin available |
| Pro        | Signed offline licence document        | Multiple agents, advanced workflow features   |
| Enterprise | Signed offline licence document        | SSO, tenancy, audit export, priority support  |

### Why agents rather than seats

This tool's value does not scale with the number of humans using it — it scales with how much
automation runs. Per-seat pricing would charge least for the case the product is best at, one person
automating a great deal, and would meter a number nobody wants to think about.

Agent count tracks capacity, which is what actually costs to run and what a customer recognizes as
the thing they are buying more of.

### Enforcement rules

These matter more than the pricing, because getting them wrong makes the product untrustworthy for
self-hosting:

- **Community has no licence check at all** — not a check that always passes, no code path. A build
  that cannot phone home is one that cannot be made to.
- **Licences verify offline.** A signed document with a claim set; signature verified against a
  public key compiled into the build. No network, ever, unless the operator opts into revocation
  checks.
- **Expiry degrades features; it never touches data or running work.** A lapsed licence stops new
  paid-feature use after a 30-day grace period. It does not stop executions already scheduled, does
  not lock the database, and never prevents export. An automation platform that halts on a billing
  event is one nobody will trust with anything important.
- **Failure is open, not closed.** If licence verification itself errors — a corrupt file, a clock
  problem — the system logs loudly and continues at the last known tier. The alternative is a
  self-inflicted outage, and the threat model here is casual over-use rather than determined piracy.

**What would change this answer:** a shift to hosted-only. With no self-hosted deployment there is
nothing to license, and entitlements collapse into the subscription record.

## Decided elsewhere

The root package name is settled in `SubZeroDev.Ecosystem/adr/ADR-002`.
