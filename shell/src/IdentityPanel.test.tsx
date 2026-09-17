import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { IdentityPanel } from "./IdentityPanel.tsx";

describe("IdentityPanel", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("S16.1 — shows an authenticated principal's kind and display name", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({ kind: "Account", displayName: "Alice", isAuthenticated: true }),
      }),
    );

    render(<IdentityPanel />);

    expect(await screen.findByTestId("identity-authenticated")).toHaveTextContent("Alice (Account)");
    expect(screen.queryByTestId("identity-anonymous")).not.toBeInTheDocument();
  });

  it("S16.1 — shows an anonymous state distinctly from an authenticated one", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({ kind: "Anonymous", displayName: null, isAuthenticated: false }),
      }),
    );

    render(<IdentityPanel />);

    expect(await screen.findByTestId("identity-anonymous")).toBeInTheDocument();
    expect(screen.queryByTestId("identity-authenticated")).not.toBeInTheDocument();
  });

  it("S16.7 — renders an error, not a crash, when the backend is unavailable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("network down")));

    render(<IdentityPanel />);

    expect(await screen.findByRole("alert")).toHaveTextContent("The backend is unavailable.");
  });
});
