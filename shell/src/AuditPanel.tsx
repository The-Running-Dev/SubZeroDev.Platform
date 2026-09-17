import { useState } from "react";
import { fetchAudit, type AuditEventView } from "./api.ts";

const oneDayAgo = () => new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
const now = () => new Date().toISOString();

// D5-S16.5: reads the audit record filtered by tenant and instant range, and offers no update and
// no delete — there is no edit or delete control anywhere in this panel, by construction.
export function AuditPanel() {
  const [tenant, setTenant] = useState("");
  const [from, setFrom] = useState(oneDayAgo());
  const [to, setTo] = useState(now());
  const [state, setState] = useState<
    { status: "idle" } | { status: "loading" } | { status: "error"; error: string } | { status: "ready"; events: AuditEventView[] }
  >({ status: "idle" });

  const onLoad = async () => {
    setState({ status: "loading" });
    const result = await fetchAudit(tenant, from, to);
    setState(result.ok ? { status: "ready", events: result.value } : { status: "error", error: result.error });
  };

  return (
    <section aria-label="Audit">
      <h2>Audit record</h2>
      <label htmlFor="audit-tenant">Tenant</label>
      <input id="audit-tenant" value={tenant} onChange={(event) => setTenant(event.target.value)} />
      <label htmlFor="audit-from">From</label>
      <input id="audit-from" value={from} onChange={(event) => setFrom(event.target.value)} />
      <label htmlFor="audit-to">To</label>
      <input id="audit-to" value={to} onChange={(event) => setTo(event.target.value)} />
      <button type="button" onClick={onLoad} disabled={tenant === ""}>
        Load
      </button>
      {state.status === "error" && <p role="alert">{state.error}</p>}
      {state.status === "ready" && (
        <table>
          <thead>
            <tr>
              <th>Occurred at</th>
              <th>Actor</th>
              <th>Action</th>
              <th>Outcome</th>
              <th>Correlation</th>
            </tr>
          </thead>
          <tbody>
            {state.events.map((event) => (
              <tr key={event.id}>
                <td>{event.occurredAt}</td>
                <td>{event.actorSubject}</td>
                <td>{event.action}</td>
                <td>{event.outcome}</td>
                <td>{event.correlation}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
