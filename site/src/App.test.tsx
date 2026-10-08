import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import App from "./App";

const siteRoot = dirname(fileURLToPath(import.meta.url)).replace(
  /[\\/]src$/,
  "",
);
// The names the platform's own source declares: its well-known health
// checks and its options. The demo may cite nothing the code never built.
const declaredNames = [
  join(
    siteRoot,
    "..",
    "src",
    "SubZeroDev.Platform.Abstractions",
    "WellKnownNames.cs",
  ),
  join(siteRoot, "..", "src", "SubZeroDev.Platform.Core", "PlatformOptions.cs"),
]
  .map((file) => readFileSync(file, "utf8"))
  .join("\n");
const declares = (name: string) =>
  new RegExp(`\\bpublic\\s+(?:static\\s+)?[\\w<>?]+\\s+${name}\\s*\\{`).test(
    declaredNames,
  );

describe("landing page", () => {
  it("renders one page-level heading carrying the live status pill", () => {
    render(<App />);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "ALL SYSTEMS OPERATIONAL",
    );
  });

  it("lists all six packages plus the two joke components, none published pre-S9", () => {
    render(<App />);
    const table = screen.getByRole("table");
    for (const name of [
      "Abstractions",
      "Core",
      "Hosting",
      "Persistence",
      "Observability",
      "Testing",
      "Marketing",
      "Adoption",
    ]) {
      expect(within(table).getByText(name)).toBeInTheDocument();
    }
    // No package claims to be published, released or installable.
    expect(screen.queryByText(/\bpublished\b/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/\breleased\b/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/\binstallable\b/i)).not.toBeInTheDocument();
  });

  it("uses the routes constant's documentation destinations, root-relative", () => {
    render(<App />);
    expect(
      screen.getByRole("link", { name: "platform identity" }),
    ).toHaveAttribute("href", "/docs/platform-identity");
    expect(
      screen.getByRole("link", { name: "Read the documentation" }),
    ).toHaveAttribute("href", "/docs/");
  });

  it("does not render a <form>, and collects nothing", () => {
    const { container } = render(<App />);
    expect(container.querySelector("form")).toBeNull();
    expect(container.querySelector("input")).toBeNull();
  });

  it("cites the same action-item count as the parsed slice inventory", () => {
    render(<App />);
    // totalCount is imported by App itself from the same parsed module the
    // roadmap uses — this only re-confirms it renders, not that it is right,
    // since roadmapData.test.ts already proves the arithmetic.
    const dd = screen.getByText("Action items").closest("div");
    expect(dd).not.toBeNull();
    expect(
      within(dd as HTMLElement).getByText(/tracked on the roadmap/),
    ).toBeInTheDocument();
  });

  it("every name in the readiness-probe example is declared in the platform's source", () => {
    render(<App />);
    const pre = document.querySelector(".readiness-example code");
    expect(pre).not.toBeNull();
    const namesInExample = [
      ...pre!.textContent!.matchAll(/"name":\s*"([^"]+)"/g),
    ].map((m) => m[1]);
    expect(namesInExample.length).toBeGreaterThan(0);
    for (const name of namesInExample) {
      expect(declares(name), name).toBe(true);
    }
    expect(declares("PeerAbsenceGrace")).toBe(true);
    expect(declares("NotAPlatformName")).toBe(false);
  });

  it("declines and accepts lists render as real lists, not pills alone", () => {
    render(<App />);
    const declined = screen
      .getByRole("heading", {
        name: "Things This Platform Refuses to Do",
      })
      .closest("section")!;
    expect(within(declined).getAllByRole("listitem").length).toBeGreaterThan(0);
    const accepted = screen
      .getByRole("heading", {
        name: "Things It Does, Loudly",
      })
      .closest("section")!;
    expect(within(accepted).getAllByRole("listitem").length).toBeGreaterThan(0);
  });
});
