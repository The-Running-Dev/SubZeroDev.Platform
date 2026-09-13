import { useState } from "react";
import { switchOrganization } from "./api.ts";

// D5-S16.2: switches the active organization through the same HTTP endpoint an ordinary caller
// uses — this panel calls it with `fetch`, the same way a bare HTTP client would.
export function OrganizationPanel() {
  const [organizationId, setOrganizationId] = useState("");
  const [result, setResult] = useState<{ status: "ok"; tenant: string } | { status: "error"; error: string } | null>(
    null,
  );
  const [pending, setPending] = useState(false);

  const onSwitch = async () => {
    setPending(true);
    const response = await switchOrganization(organizationId);
    setResult(response.ok ? { status: "ok", tenant: response.value.tenant } : { status: "error", error: response.error });
    setPending(false);
  };

  return (
    <section aria-label="Organization">
      <h2>Switch organization</h2>
      <label htmlFor="organization-id">Organization id</label>
      <input
        id="organization-id"
        value={organizationId}
        onChange={(event) => setOrganizationId(event.target.value)}
      />
      <button type="button" onClick={onSwitch} disabled={pending || organizationId === ""}>
        Switch
      </button>
      {result?.status === "ok" && <p data-testid="switch-result">Switched. Active tenant: {result.tenant}</p>}
      {result?.status === "error" && <p role="alert">{result.error}</p>}
    </section>
  );
}
