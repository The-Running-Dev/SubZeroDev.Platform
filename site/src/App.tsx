import "./index.css";
import "./status.css";
import "./landing.css";
import {
  DocsLink,
  ExternalLink as RepositoryLink,
  SiteFooter,
  SiteHeader,
  StatusPill,
  routes,
  type PillTone,
} from "./shared";
import { totalCount } from "./roadmap/roadmapData";

type Component = {
  name: string;
  tone: PillTone;
  label: string;
  message: string;
};

/** The six shipped packages. `components.length` below is what "the six
 * packages" derives from — never a typed 6. */
const components: readonly Component[] = [
  {
    name: "Abstractions",
    tone: "ok",
    label: "OPERATIONAL",
    message: "Depends on nothing. Insufferably proud of it.",
  },
  {
    name: "Core",
    tone: "ok",
    label: "OPERATIONAL",
    message: "Refuses to start rather than start wrong.",
  },
  {
    name: "Hosting",
    tone: "ok",
    label: "OPERATIONAL",
    message: "Starts, serves, and stops when asked.",
  },
  {
    name: "Persistence",
    tone: "ok",
    label: "OPERATIONAL",
    message:
      "Two providers, one connection, no opinion about your repository pattern.",
  },
  {
    name: "Observability",
    tone: "ok",
    label: "OPERATIONAL",
    message: "Exports nowhere by default and will not be taking questions.",
  },
  {
    name: "Testing",
    tone: "ok",
    label: "OPERATIONAL",
    message: "Ships with nothing. Depended on by nothing shipped.",
  },
];

const jokeComponents: readonly Component[] = [
  {
    name: "Marketing",
    tone: "unknown",
    label: "NOT MONITORED",
    message: "No telemetry configured.",
  },
  {
    name: "Adoption",
    tone: "degraded",
    label: "DEGRADED",
    message: "Peer host missing. It's you.",
  },
];

const plannedConsumers = ["SubZeroDev.Automator", "Game Engine as a Service"];

const refusals = [
  "Start on a configuration it cannot explain.",
  "Retry your request on your behalf.",
  "Impose a repository pattern on your tables.",
  "Depend on a product.",
  "Reach the internet at startup.",
  "Report a missing check as a passing one.",
];

const capabilities = [
  "Abort startup and name the setting that broke it.",
  "Survive being killed between the commit and the publish.",
  "Notice two hosts pointed at different databases, and say so out loud.",
  "Sort timestamps identically on PostgreSQL and SQLite — which took more effort than it sounds.",
];

/**
 * The readiness-probe body used as the page's one screenshot. Every name in
 * it must exist in design/d3/30-slices.md — asserted by App.test.tsx — because
 * a status page whose own demo cites a check it never built is the exact
 * failure "nothing may be funnier than it is true" forbids.
 */
const readinessExample = `{
  "status": "Degraded",
  "checks": [
    { "name": "Database", "status": "Healthy" },
    { "name": "PendingMigrations", "status": "Healthy" },
    { "name": "SettingsFingerprint", "status": "Healthy" },
    {
      "name": "PeerHost",
      "status": "Degraded",
      "detail": "Worker role not seen within PeerAbsenceGrace"
    }
  ]
}`;

function App() {
  return (
    <>
      <SiteHeader current="home" />

      <main>
        <section className="page-section hero" aria-labelledby="hero-title">
          <p className="section-index">STATUS</p>
          <h1 id="hero-title">
            <StatusPill tone="ok" label="ALL SYSTEMS OPERATIONAL" live />
          </h1>
          <p className="hero-sub">
            The last incident was never. The last user is a sample.
          </p>
          <dl className="fact-strip">
            <div>
              <dt>uptime</dt>
              <dd>100%</dd>
            </div>
            <div>
              <dt>open incidents</dt>
              <dd>0</dd>
            </div>
            <div>
              <dt>consumers</dt>
              <dd>0 ({plannedConsumers.length} planned)</dd>
            </div>
          </dl>
        </section>

        <section
          id="components"
          className="page-section"
          aria-labelledby="components-title"
        >
          <p className="section-index">01 / COMPONENTS</p>
          <h2 id="components-title">Monitored Components</h2>
          <div className="panel">
            <table className="status-table">
              <caption className="visually-hidden">
                Component status for SubZeroDev.Platform
              </caption>
              <thead>
                <tr>
                  <th scope="col">Component</th>
                  <th scope="col">Status</th>
                  <th scope="col">Message</th>
                </tr>
              </thead>
              <tbody>
                {[...components, ...jokeComponents].map((component) => (
                  <tr key={component.name}>
                    <td>{component.name}</td>
                    <td>
                      <StatusPill
                        tone={component.tone}
                        label={component.label}
                      />
                    </td>
                    <td className="component-message">{component.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>

        <section
          id="what-this-is"
          className="page-section"
          aria-labelledby="what-title"
        >
          <p className="section-index">02 / WHAT THIS IS</p>
          <h2 id="what-title">The Part Nobody Demos</h2>
          <p>
            {components.length} packages, two processes, zero excitement, and a
            dependency direction enforced by the build rather than by intent —
            see the{" "}
            <DocsLink href={routes.identity}>platform identity</DocsLink> and{" "}
            <DocsLink href={routes.packages}>package set</DocsLink>.
          </p>
        </section>

        <section
          id="incident-report"
          className="page-section"
          aria-labelledby="incident-title"
        >
          <p className="section-index">03 / INCIDENT REPORT</p>
          <h2 id="incident-title">Postmortem: Two Products, No Shared Layer</h2>
          <dl className="postmortem">
            <div>
              <dt>Impact</dt>
              <dd>
                Two unrelated products each re-deriving hosting shape,
                configuration binding, startup validation, and test
                infrastructure.
              </dd>
            </div>
            <div>
              <dt>Root cause</dt>
              <dd>Nothing shared existed.</dd>
            </div>
            <div>
              <dt>Time to detect</dt>
              <dd>Years.</dd>
            </div>
            <div>
              <dt>Time to resolve</dt>
              <dd>Ongoing.</dd>
            </div>
            <div>
              <dt>Resolution</dt>
              <dd>
                This repository — see the{" "}
                <DocsLink href={routes.specification}>
                  platform specification
                </DocsLink>
                .
              </dd>
            </div>
            <div>
              <dt>Action items</dt>
              <dd>{totalCount}, tracked on the roadmap.</dd>
            </div>
          </dl>
        </section>

        <section
          id="returns-503"
          className="page-section"
          aria-labelledby="refuses-title"
        >
          <p className="section-index">04 / RETURNS 503</p>
          <h2 id="refuses-title">Things This Platform Refuses to Do</h2>
          <ul className="declined-list">
            {refusals.map((item) => (
              <li key={item}>
                <span aria-hidden="true">✕</span>
                {item}
              </li>
            ))}
          </ul>
        </section>

        <section
          id="returns-200"
          className="page-section"
          aria-labelledby="capable-title"
        >
          <p className="section-index">05 / RETURNS 200</p>
          <h2 id="capable-title">Things It Does, Loudly</h2>
          <ul className="accepted-list">
            {capabilities.map((item) => (
              <li key={item}>
                <span aria-hidden="true">✓</span>
                {item}
              </li>
            ))}
          </ul>
        </section>

        <section
          id="uptime"
          className="page-section"
          aria-labelledby="uptime-title"
        >
          <p className="section-index">06 / UPTIME</p>
          <h2 id="uptime-title">Ninety Days, All Green</h2>
          <div
            className="uptime-bars"
            role="img"
            aria-label="Ninety day-bars, all green: a day nobody filed an issue, largely because nobody knew where to file one."
          >
            {Array.from({ length: 90 }, (_, i) => (
              <span key={i} className="uptime-bar" aria-hidden="true" />
            ))}
          </div>
          <p className="uptime-legend">
            Each bar is a day nobody filed an issue — largely because nobody
            knew where to file one.
          </p>
        </section>

        <section
          id="the-only-demo"
          className="page-section"
          aria-labelledby="demo-title"
        >
          <p className="section-index">07 / THE ONLY DEMO WE HAVE</p>
          <h2 id="demo-title">A Readiness Response, As the Hero Image</h2>
          <p>
            A probe body is the only screenshot infrastructure ever gets.{" "}
            <code>Degraded</code> returns HTTP 200. <code>Unhealthy</code>{" "}
            returns 503. The difference is the whole personality.
          </p>
          <pre className="panel readiness-example">
            <code>{readinessExample}</code>
          </pre>
        </section>

        <section
          id="subscribe"
          className="page-section"
          aria-labelledby="subscribe-title"
        >
          <p className="section-index">08 / SUBSCRIBE</p>
          <h2 id="subscribe-title">There Is No Mailing List</h2>
          <p>
            There is a repository. Watch it, if that is a thing you do for
            software that returns 200 on purpose.
          </p>
          <p>
            <RepositoryLink href="https://github.com/The-Running-Dev/SubZeroDev.Platform">
              Browse the repository
            </RepositoryLink>{" "}
            ·{" "}
            <DocsLink href={routes.docsIndex}>Read the documentation</DocsLink>{" "}
            · <DocsLink href="/roadmap/">View the incident history</DocsLink>
          </p>
        </section>
      </main>

      <SiteFooter />
    </>
  );
}

export default App;
