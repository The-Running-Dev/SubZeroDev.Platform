import { IdentityPanel } from "./IdentityPanel.tsx";
import { OrganizationPanel } from "./OrganizationPanel.tsx";
import { EntitlementPanel } from "./EntitlementPanel.tsx";
import { AuditPanel } from "./AuditPanel.tsx";

// The administration shell (D5-S16): a screen a consumer can throw away and replace with their
// own. It holds no server-side state of its own — every panel below reads or writes through the
// public HTTP API a caller reaches without this shell in the loop (I-W1).
export function App() {
  return (
    <main>
      <h1>Platform administration</h1>
      <IdentityPanel />
      <OrganizationPanel />
      <EntitlementPanel />
      <AuditPanel />
    </main>
  );
}
