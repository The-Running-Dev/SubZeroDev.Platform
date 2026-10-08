# decision/2026-10-08-barstrad-is-a-service-operated-for-venues-an-unreachable-channel-degrades-readiness
Date: 2026-10-08
Anchor: 2026-10-08 — #24: BarStrad is a service operated for venues; an unreachable channel degrades readiness
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #24: BarStrad is a service operated for venues; an unreachable channel degrades readiness"

## Claim
Context — [#24](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/24). BarStrad's commercial model was undecided. A hosted answer contradicted the D3 brief's self-host-only non-goal. Separately, a chat channel is online in steady state, which conflicts with deployments that have no outbound network.

Chosen — BarStrad is a service the owner operates for venues. The D3 brief is closed, and D5's brief already names operated SaaS, so no brief changes. The billing cell in `platform-identity.md` §4 and `application-modules.md` §5 say so. Independently of the deployment answer, an unreachable channel degrades readiness and never fails the host or the triggering work (`events-and-notifications.md` *Delivery failure*). Ben ruled 2026-10-08.

Rejected — self-hosted and licensed per venue installation, which the owner did not choose; a channel whose reachability is a startup condition, which makes an offline deployment unstartable.

Reversibility — expensive once a venue is onboarded; cheap before.
