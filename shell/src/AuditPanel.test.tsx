import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuditPanel } from "./AuditPanel.tsx";

describe("AuditPanel", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("S16.5 — offers no update and no delete control", () => {
    render(<AuditPanel />);

    expect(screen.queryByRole("button", { name: /delete/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /edit/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /save/i })).not.toBeInTheDocument();
  });

  it("S16.5 — reads records filtered by tenant and instant range through the public API", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [
        {
          id: "1",
          occurredAt: "2026-01-01T00:00:00Z",
          actorIssuer: "test",
          actorSubject: "alice",
          actorKind: "Account",
          tenant: "11111111-1111-1111-1111-111111111111",
          action: "platform.tenancy.shared-read",
          resourceType: null,
          resourceId: null,
          outcome: "Allowed",
          correlation: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          class: "Recorded",
        },
      ],
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<AuditPanel />);
    await userEvent.type(screen.getByLabelText("Tenant"), "11111111-1111-1111-1111-111111111111");
    await userEvent.click(screen.getByRole("button", { name: "Load" }));

    expect(await screen.findByText("alice")).toBeInTheDocument();
    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toContain("/api/audit?tenant=");
    expect(url).toContain("from=");
    expect(url).toContain("to=");
  });
});
