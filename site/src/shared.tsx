import type { ReactNode } from "react";

const repository = "https://github.com/The-Running-Dev/SubZeroDev.Platform";

/**
 * Root-relative, and every value here is checked against a real file under
 * docs/docs/ — matching case — by routes.test.ts. No file there carries a
 * `slug:`, so a route is the file's path without its extension, and an ADR
 * route keeps the file's exact casing: GitHub Pages is case-sensitive, and a
 * lowercased ADR route works on a Windows checkout and 404s on deploy.
 */
export const routes = {
  docsIndex: "/docs/",
  identity: "/docs/platform-identity",
  specification: "/docs/platform-specification",
  packages: "/docs/minimal-platform-packages",
  implementationPlan: "/docs/implementation-plan",
  adrBuildNotAdopt: "/docs/adr/ADR-004-framework-build-not-adopt",
} as const;

export function DocsLink({
  href,
  children,
}: {
  href: string;
  children: ReactNode;
}) {
  return <a href={href}>{children}</a>;
}

export function ExternalLink({
  href,
  children,
}: {
  href: string;
  children: ReactNode;
}) {
  return (
    <a href={href} target="_blank" rel="noreferrer">
      {children}
      <span className="visually-hidden"> (opens in a new tab)</span>
    </a>
  );
}

export type PillTone = "ok" | "degraded" | "unhealthy" | "unknown";

const PILL_GLYPH: Record<PillTone, string> = {
  ok: "●",
  degraded: "◐",
  unhealthy: "○",
  unknown: "○",
};

/**
 * Status is never colour alone: every pill renders a glyph and a word, so the
 * page stays legible in greyscale and to a screen reader. `label` is the
 * pill's accessible name in full — "OPERATIONAL", not the glyph alone.
 *
 * `live` marks the one pulsing dot the design allows — the hero banner's,
 * never a table row's — and the pulse itself stops under
 * prefers-reduced-motion (see status.css).
 */
export function StatusPill({
  tone,
  label,
  live = false,
}: {
  tone: PillTone;
  label: string;
  live?: boolean;
}) {
  return (
    <span className={`status-pill status-pill-${tone}`}>
      <span aria-hidden="true" className={live ? "live-dot" : undefined}>
        {PILL_GLYPH[tone]}
      </span>
      {label}
    </span>
  );
}

export function SiteHeader({ current }: { current?: "home" | "roadmap" }) {
  return (
    <header className="site-header">
      <a className="wordmark" href="/" aria-label="SubZeroDev Platform home">
        <span aria-hidden="true">{"●"}</span> SUBZERODEV.PLATFORM
      </a>
      <nav aria-label="Explore the platform">
        {current !== "home" && <a href="/">Status</a>}
        <a
          href="/roadmap/"
          aria-current={current === "roadmap" ? "page" : undefined}
        >
          Incidents
        </a>
        <a href={routes.docsIndex}>Docs</a>
        <ExternalLink href={repository}>Repository</ExternalLink>
      </nav>
    </header>
  );
}

export function SiteFooter() {
  return (
    <footer>
      <p>
        This page is not wired to anything. If it were, it would be{" "}
        <StatusPill tone="degraded" label="DEGRADED" />.
      </p>
      <p>
        <ExternalLink href={repository}>
          SubZeroDev.Platform on GitHub
        </ExternalLink>
      </p>
    </footer>
  );
}
