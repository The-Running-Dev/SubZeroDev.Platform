import { useEffect, useState } from "react";
import { fetchPrincipal, type PrincipalView } from "./api.ts";

// D5-S16.1: shows the current principal's kind and display name, and distinguishes an anonymous
// state from an authenticated one.
export function IdentityPanel() {
  const [state, setState] = useState<
    { status: "loading" } | { status: "error"; error: string } | { status: "ready"; principal: PrincipalView }
  >({ status: "loading" });

  useEffect(() => {
    let cancelled = false;
    fetchPrincipal().then((result) => {
      if (cancelled) return;
      setState(result.ok ? { status: "ready", principal: result.value } : { status: "error", error: result.error });
    });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <section aria-label="Identity">
      <h2>Signed in as</h2>
      {state.status === "loading" && <p>Loading…</p>}
      {state.status === "error" && <p role="alert">{state.error}</p>}
      {state.status === "ready" &&
        (state.principal.isAuthenticated ? (
          <p data-testid="identity-authenticated">
            {state.principal.displayName ?? state.principal.kind} ({state.principal.kind})
          </p>
        ) : (
          <p data-testid="identity-anonymous">Anonymous — no credential presented.</p>
        ))}
    </section>
  );
}
