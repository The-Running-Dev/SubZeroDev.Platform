// The exact routes this shell calls — kept in step with
// samples/SubZeroDev.Platform.Sample.Web/AdminEndpoints.cs's `ShellCalls` manifest, which a test
// checks against the live endpoint data source (D5-S16.3, I-W1). Every one of them is an ordinary
// endpoint any caller holding the declared permission may reach without this shell in the loop.

export type ApiResult<T> = { ok: true; value: T } | { ok: false; error: string };

async function request<T>(path: string, init?: RequestInit): Promise<ApiResult<T>> {
  try {
    const response = await fetch(path, init);
    if (!response.ok) {
      return { ok: false, error: `${response.status} ${response.statusText}` };
    }
    const value = (await response.json()) as T;
    return { ok: true, value };
  } catch {
    // The backend is unreachable, not merely refusing — the shell holds no server-side state,
    // so there is nothing to lose here beyond this one panel's own render (D5-S16.7).
    return { ok: false, error: "The backend is unavailable." };
  }
}

export interface PrincipalView {
  kind: string;
  displayName: string | null;
  isAuthenticated: boolean;
}

export interface OrganizationSwitchView {
  tenant: string;
}

export interface EntitlementView {
  feature: string;
  granted: boolean;
  sources: string[];
  licenceTier: string;
  licenceExpiresAt: string | null;
  licenceGraceEndsAt: string | null;
  licenceVerificationOutcome: string | null;
}

export interface AuditEventView {
  id: string;
  occurredAt: string;
  actorIssuer: string;
  actorSubject: string;
  actorKind: string;
  tenant: string;
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  outcome: string;
  correlation: string;
  class: string;
}

export const fetchPrincipal = () => request<PrincipalView>("/api/me");

export const switchOrganization = (organizationId: string) =>
  request<OrganizationSwitchView>(`/api/organizations/${organizationId}/switch`, { method: "POST" });

export const fetchEntitlement = () => request<EntitlementView>("/api/entitlement");

export const fetchAudit = (tenant: string, from: string, to: string) =>
  request<AuditEventView[]>(
    `/api/audit?tenant=${encodeURIComponent(tenant)}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`,
  );
