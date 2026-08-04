import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SiteFooter, SiteHeader, StatusPill } from "./shared";

describe("StatusPill", () => {
  it("carries a glyph and a word, never colour alone", () => {
    render(<StatusPill tone="ok" label="OPERATIONAL" />);
    const pill = screen.getByText("OPERATIONAL").closest(".status-pill");
    expect(pill).not.toBeNull();
    const glyph = pill!.querySelector("[aria-hidden]");
    expect(glyph).not.toBeNull();
    expect(glyph!.textContent).not.toBe("");
    // The label itself — the accessible word — is outside the aria-hidden
    // glyph span, so it is what a screen reader (and a greyscale reader)
    // actually gets.
    expect(pill!.textContent).toContain("OPERATIONAL");
  });

  it("only marks the live prop's dot as the pulsing element", () => {
    render(<StatusPill tone="ok" label="OPERATIONAL" live />);
    const pill = screen.getByText("OPERATIONAL").closest(".status-pill");
    expect(pill!.querySelector(".live-dot")).not.toBeNull();
  });

  it("renders no live-dot by default", () => {
    render(<StatusPill tone="ok" label="OPERATIONAL" />);
    const pill = screen.getByText("OPERATIONAL").closest(".status-pill");
    expect(pill!.querySelector(".live-dot")).toBeNull();
  });

  it.each([
    ["ok", "OPERATIONAL"],
    ["degraded", "DEGRADED"],
    ["unhealthy", "UNHEALTHY"],
    ["unknown", "NOT MONITORED"],
  ] as const)("renders a distinct tone class for %s", (tone, label) => {
    render(<StatusPill tone={tone} label={label} />);
    const pill = screen.getByText(label).closest(".status-pill");
    expect(pill).toHaveClass(`status-pill-${tone}`);
  });
});

describe("SiteHeader", () => {
  it("marks the current page and links to the other, docs, and repository", () => {
    render(<SiteHeader current="home" />);
    expect(screen.getByRole("link", { name: /Incidents/ })).toHaveAttribute(
      "href",
      "/roadmap/",
    );
    expect(screen.getByRole("link", { name: "Docs" })).toHaveAttribute(
      "href",
      "/docs/",
    );
    expect(screen.getByRole("link", { name: /Repository/ })).toHaveAttribute(
      "target",
      "_blank",
    );
  });
});

describe("SiteFooter", () => {
  it("says plainly that the page is not wired to anything", () => {
    render(<SiteFooter />);
    expect(screen.getByText(/not wired to anything/)).toBeInTheDocument();
  });
});
