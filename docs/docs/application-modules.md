---
sidebar_position: 9
sidebar_label: Application Modules
---

# Application Modules — What It Takes to Host a Content-and-Ordering Site

**Document status:** Design. Justified in part, overridden in part, and scheduled nowhere.

**Reading order:** after [Second-Consumer Packages](second-consumer-packages.md), whose argument
this continues with a third consumer.

> **Scope of this document**
>
> The module set an application like BarStrad needs from Platform, tested against three
> consumers rather than two; the split between the framework and the optional modules built
> on it; and the two modules admitted by decision rather than by the boundary test, with the
> objection to them retained rather than dropped.

---

## 1. Platform Is a Framework Plus a Library, and the Distinction Is Load-Bearing

**The decision, its four rules, its consequences and the alternatives it rejected are
[ADR-006](adr/ADR-006-application-modules.md).** That is its one home; this section is orientation
and does not restate it.

Everything in [`minimal-platform-packages.md`](minimal-platform-packages.md) is **framework**: a
consumer cannot decline it and still be hosted. Hosting, persistence, observability and the
abstractions beneath them are what "running on Platform" means.

An **application module** is different in exactly one way that matters: **nothing in the framework
may reference one.** A module is opt-in, separately packaged, and its absence is invisible. The
Automator takes none. The Game Engine takes none. BarStrad takes two.

That single rule is what keeps [ADR-001](adr/ADR-001-platform-identity.md) intact while the module
library grows. The decision ADR-001 records is a *dependency direction* — Platform never depends on a
product — and a catalogue module is not a product. What would break ADR-001 is a framework package
that knows what a price is, and the rule above makes that a build failure rather than a review
comment, which is the standard ADR-001 already sets for the product direction.

**What the split costs is stated in ADR-006's consequences**, including the one most likely to be
paid silently: separate packaging *feels* like quarantine, and only an enforced reference check makes
it one.

---

## 2. The Third Consumer

BarStrad is a running Discord-and-web ordering product for a bar, in Bulgarian and English. It is
unrelated to workflow automation and unrelated to game hosting, which is what makes it useful here:
[`second-consumer-packages.md`](second-consumer-packages.md) §1 argues that a package shaped against
one consumer encodes that consumer's assumptions as though they were general. A third, unrelated one
tests that more cheaply than the first paying customer does.

**[Platform Identity](platform-identity.md) §4 is the canonical count of which candidate has which
consumer.** The table below is a view of it from BarStrad's side — it adds a standing column and the
framework rows §4 does not carry, and where the two disagree, §4 is right.

| Capability | Automator | Game Engine | BarStrad | Standing |
|---|---|---|---|---|
| Hosting, configuration, startup validation | ✓ | ✓ | ✓ | Framework, built |
| Persistence, transactions, migrations | ✓ | ✓ | ✓ | Framework, built |
| Outbox and integration events | ✓ | ✓ | ✓ | Framework, in progress |
| Observability | ✓ | ✓ | ✓ | Framework, in progress |
| Ambient culture on the operation and the row | ✓ templates | ✓ culture packs | ✓ two languages | Framework, decided |
| **Notifications** — recipient, channel, template, dedup, backoff | ✓ | ✓ | ✓ | **Guard satisfied** |
| **Channel providers** — Discord, email, webhook | ✓ | ✓ | ✓ | **Guard satisfied** |
| **Localized structured content** — validated, culture fallback, reloadable | ~ | ✓ | ✓ | **Guard satisfied** |
| **Inbound command surface** | ✓ plugin tools | ✓ game tools | ✓ chat commands | **Guard satisfied** |
| Media and asset storage | ✓ artifacts | ✓ saves, assets | ✓ item photos | Justified previously |
| Identity, including non-account principals | ✓ | ✓ | ✓ | Justified previously |
| Preferences, theming, branding | ✓ | ✓ white-label | ✓ | Justified previously |
| Billing and entitlements | ✓ | ✓ | ~ | Justified previously |
| **Catalogue** — priced, categorised, localized items | ✗ | ✗ | ✓ | **§4 — by decision** |
| **Ordering** — lines, subject, state machine | ✗ | ✗ | ✓ | **§4 — by decision** |

Four rows change status here. Notifications was gated on
[`implementation-plan.md`](implementation-plan.md) §D4's condition — *two named consumers, not one and
a plan* — and now has three, one of which is in production. Its channel providers come with it.
Localized content acquires a second consumer that is not a variation on the first: the Game Engine's
`base campaign → expansion → localization → culture pack` chain and a bilingual menu want the same
resolution and fallback behaviour for unrelated reasons. The inbound command surface becomes a third
producer against the seam [`second-consumer-packages.md`](second-consumer-packages.md) §4 deliberately
left open, and it is worth noting that it holds: a chat command table needed no change to that design.

**Two shapes are corrected rather than confirmed:**

- **A principal need not be an account.** §2 of that document says identity must stay *optional* for
  local-only products. BarStrad is stronger — its customer-side principal is a table, established by
  a QR link and held client-side, and no account will ever exist for it. An identity design that
  treats anonymity as a degraded case will not serve it.
- **Preferences must have a principal-less mode.** The per-tenant-and-per-user model in
  [`events-and-notifications.md`](events-and-notifications.md) has nowhere to store a preference for
  a principal with no record.

---

## 3. What Each Admitted Module Owns

Stated as shape, not as schedule.

**Content.** Structured data resolved per `CultureTag` with a fallback chain, validated against a
schema at load, and reloadable without a redeploy. The last property is the one BarStrad actually
lacks today — its menu is JSON baked into a container image, so a price change is a rebuild — and it
is in tension with the framework's deliberate choice that configuration binds once at startup and is
fingerprinted. **Content is not configuration**, and that distinction has to be explicit in the
design or the fingerprint's guarantee quietly stops meaning anything.

**Notifications.** The model is already specified in
[`events-and-notifications.md`](events-and-notifications.md) and is not restated here. One rule that
document does not yet carry, and which BarStrad supplies the evidence for: **a channel credential
never leaves the server.** That is stronger than the telemetry rule about secrets in logs and spans,
and BarStrad breaks it in both directions today — a webhook URL committed to its repository, and a
second one shipped into a browser bundle.

A second rule the same evidence forces: a channel is online in steady state, which meets the brief's
*no runtime dependency on outbound network* head-on. **A channel's unreachability degrades readiness
and never fails the host or the work that triggered it** — already half-stated there as "a Discord
outage does not fail a sync", and needing to be said outright.

**Catalogue.** Items with a stable identity, a category, a price, media references, availability, and
localized names and descriptions per culture. Not: item numbering, volumes, drink categories, or
anything else that only reads as sensible in a bar.

**Ordering.** An order with lines, a subject that names where it goes, a state machine, and an
integration event per transition through the outbox. Not: tables, seats, kitchen printers, tips, or
staff rotas.

---

## 4. Catalogue and Ordering Are Admitted by Decision, Not by the Test

Both fail the boundary test in [Platform Identity](platform-identity.md) §3 as written. Neither the
Automator nor the Game Engine wants either module, so each has exactly one consumer, and one consumer
is the condition the extraction guard exists to refuse. They are here because the repository owner
decided they should be, which is an authority the guard does not override.

**The objection, retained rather than dropped**, per this repository's rule that a declined finding is
recorded in the affected document: §3 of Platform Identity names "campaign content, session envelopes
and save files" as product concepts wearing infrastructure clothes, and a priced, categorised item
list is the same class of thing. The specific hazard is not that these modules are useless — they are
obviously useful to BarStrad — it is that a module written for one consumer encodes that consumer's
assumptions as general, which is the failure
[`second-consumer-packages.md`](second-consumer-packages.md) §1 describes and the reason a second
consumer was ever required. This repository has also already paid once for a boundary drifting in
this direction; ADR-001 is what that cost.

**What makes the decision cheap anyway, and what has to hold for that to stay true.** Under §1's rule
these are opt-in packages that no framework package references. If a second consumer never appears,
they move into BarStrad and Platform loses two packages and no framework code — a cheap reversal,
**but only while the rule holds**. Two things would make it expensive, and both are checkable:

1. **A framework package gaining knowledge of a catalogue or an order.** The same build-failure
   standard ADR-001 sets for product references applies.
2. **A second module depending on one of them.** A notification template that knows about order
   states, or a content schema that knows about prices, welds them in.

**Revisit when** either module has a second consumer — at which point it graduates and this section is
deleted — or when D4 finishes without one, at which point moving them into BarStrad is the cheaper
answer and this section is the record of why.

---

## 5. What This Document Does Not Do

It schedules nothing. The near-term set is still the six of
[`minimal-platform-packages.md`](minimal-platform-packages.md), and D3 finishes before any module here
starts — [`implementation-plan.md`](implementation-plan.md) holds the ordering, and nothing on this
page moves it.

It also does not settle whether BarStrad is self-hosted and licensed per installation or a service
operated for venues. That question contradicts binding statements in the D3 brief depending on its
answer, it is the repository owner's to settle, and it is tracked rather than assumed.
