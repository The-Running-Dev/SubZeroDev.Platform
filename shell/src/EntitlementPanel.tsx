import { useEffect, useState } from "react";
import { fetchEntitlement, type EntitlementView } from "./api.ts";

// D5-S16.4: displays the resolved entitlement, the licence tier and the grace state, reading all
// three through the public API — nothing here is computed from anything else.
export function EntitlementPanel() {
  const [state, setState] = useState<
    { status: "loading" } | { status: "error"; error: string } | { status: "ready"; entitlement: EntitlementView }
  >({ status: "loading" });

  useEffect(() => {
    let cancelled = false;
    fetchEntitlement().then((result) => {
      if (cancelled) return;
      setState(
        result.ok ? { status: "ready", entitlement: result.value } : { status: "error", error: result.error },
      );
    });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <section aria-label="Entitlement">
      <h2>Entitlement and licence</h2>
      {state.status === "loading" && <p>Loading…</p>}
      {state.status === "error" && <p role="alert">{state.error}</p>}
      {state.status === "ready" && (
        <dl>
          <dt>Feature</dt>
          <dd>{state.entitlement.feature}</dd>
          <dt>Granted</dt>
          <dd data-testid="entitlement-granted">{state.entitlement.granted ? "Yes" : "No"}</dd>
          <dt>Licence tier</dt>
          <dd data-testid="licence-tier">{state.entitlement.licenceTier}</dd>
          <dt>Licence expires</dt>
          <dd>{state.entitlement.licenceExpiresAt ?? "Never"}</dd>
          <dt>Grace ends</dt>
          <dd data-testid="licence-grace">{state.entitlement.licenceGraceEndsAt ?? "Not in grace"}</dd>
          <dt>Last verification</dt>
          <dd>{state.entitlement.licenceVerificationOutcome ?? "Never verified"}</dd>
        </dl>
      )}
    </section>
  );
}
