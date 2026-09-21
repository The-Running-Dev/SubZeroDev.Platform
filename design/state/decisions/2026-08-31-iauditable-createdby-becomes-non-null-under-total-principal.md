# decision/2026-08-31-iauditable-createdby-becomes-non-null-under-total-principal
Date: 2026-08-31
Anchor: 2026-08-31 — `IAuditable.CreatedBy` becomes non-null under the total principal
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-31 — `IAuditable.CreatedBy` becomes non-null under the total principal"

## Claim
Context — `20-contract.md` § *Unresolved* 1 (formerly item 1) left two defensible readings open, and `30-slices.md`'s pre-slice decision table named this a blocker on S2: `CreatedBy` becomes non-null, or stays `string?`. Both readings produce different declarations and the contract deliberately chose neither. Asked of the user directly, since S2 cannot implement `IAuditable` against two undetermined shapes at once.

Chosen — **`CreatedBy` becomes non-null (`string`).** The ambient principal is now total — `ICurrentPrincipal.Current` never returns null — so "every row names an actor" becomes a property of the type rather than of the writer, and no consumer runs on Platform today, so the migration cost is zero right now. `ModifiedBy` and `DeletedBy` stay nullable regardless of this answer, because their null means "not yet modified" and "not deleted", a different fact from "no actor".

Rejected — **leaving `CreatedBy` as `string?`.** Preserves a reading where null means "written before D5" for an existing consumer's row, which is real information Platform cannot recover once a consumer exists — but no consumer exists today, so the alternative costs nothing now and would cost a migration later precisely when it stops being free.

Reversibility — cheap today, expensive later — the same asymmetry the contract's own framing named. Widening `CreatedBy` back to nullable after a consumer ships is a breaking change to every row that consumer already wrote; narrowing it now, before any row exists, changes nothing on disk.
