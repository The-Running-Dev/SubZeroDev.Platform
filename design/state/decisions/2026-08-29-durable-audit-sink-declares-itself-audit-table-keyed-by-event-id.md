# decision/2026-08-29-durable-audit-sink-declares-itself-audit-table-keyed-by-event-id
Date: 2026-08-29
Anchor: 2026-08-29 — A durable audit sink declares itself, and the audit table is keyed by event id
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-29 — A durable audit sink declares itself, and the audit table is keyed by event id"

## Claim
Context — `10-design.md` makes `Operated` with no durable audit sink a startup failure, but a sink's durability is not discoverable by trying it, and the audit record's storage shape is not stated.

Chosen — `IAuditSink.IsDurable` is declared on the interface, on the precedent `IHealthCheck.TouchesExternalDependency` already sets for a property a registry must reject on. The audit table is keyed by the event id, which the writer mints, and **is not tenant-prefixed** unlike every product table; it is indexed on tenant with instant, on correlation, and on actor subject with instant, and on nothing else. The actor is stored as two columns, issuer and subject.

Rejected — **inferring durability from whether the Audit store module is present**; rejected because it makes a framework check depend on knowing a module's identity, which is ADR-006 rule 1 through the back door. **A tenant-prefixed audit key**, matching every other table; rejected because an audit row is written *about* a tenant rather than owned by one, and the cross-tenant queries an operator actually runs would become cross-partition scans. **Storing the rendered `PrincipalId`**; rejected because the rendering is not injective over two opaque halves and could not be read back.

Reversibility — cheap for the index set; expensive for the key, which every stored row carries.
