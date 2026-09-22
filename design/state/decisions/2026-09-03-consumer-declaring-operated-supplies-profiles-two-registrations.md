# decision/2026-09-03-consumer-declaring-operated-supplies-profiles-two-registrations
Date: 2026-09-03
Anchor: 2026-09-03 — A consumer declaring `Operated` supplies the profile's two registrations itself
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-03 — A consumer declaring `Operated` supplies the profile's two registrations itself"

## Claim
Context — S8 makes I-C1 and I-C2 real — an `Operated` host with no authentication provider, or with no sink declaring `IsDurable`, refuses to start. The packages that supply those are Identity (S9) and the audit store (S13), both of which land *after* S8. Every `Operated` host in this repository — the web and worker samples, the game-edge workload, the test host — therefore stopped starting the moment the gate went in, and S8.12 forbids degrading instead of failing.

Chosen — each host supplies what its declared profile requires, at the layer that declares it. The two samples register a `NoCredentialAuthenticationProvider` and a `FileAuditSink` of their own (`samples/SubZeroDev.Platform.Sample.Web/Composition.cs`), written as consumer code and marked as what S9 and S13 replace. `SubZeroDev.Platform.Testing` gains `FakeAuthenticationProvider` and `FakeDurableAuditSink`, which `PlatformTestHost` registers when the profile it will declare is `Operated` — S18.2 already owes Testing the composition profile on the test host, so this is that obligation arriving when the gate made it necessary. The game-edge workload's declared profile moved `Operated` → `Local`, because it authenticates nobody and keeps no durable audit trail: `Operated` was a claim it could not back, and S8 is the slice that makes such a claim checkable.

Rejected — **a framework-supplied default provider or sink**, which would have kept every host starting with no per-host change; rejected because a default that satisfies a startup check is the "registered check that always passes" the brief refuses by name — the check would have been unfalsifiable from the moment it shipped. **Moving the samples to `Local` as well**; rejected because S1 exists to give the effort two hosts of different shapes, and a repository with no operated host cannot demonstrate any later slice's operated behaviour. **Deferring I-C1 and I-C2 until S9 and S13 land**; rejected because that is the degradation I-C9 rules out, and a gate that arrives after the packages it gates is a gate nothing was ever checked against.

Reversibility — cheap for the samples and the test host — a registration each. Expensive for the game-edge profile change, which is a statement about what that deployment is rather than a wiring detail.
