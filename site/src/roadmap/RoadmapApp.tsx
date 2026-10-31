import "../index.css";
import "../status.css";
import "./roadmap.css";
import { ExternalLink, SiteFooter, SiteHeader, StatusPill } from "../shared";
import {
  lastArchivedLedger,
  nonGoals as activeNonGoals,
  slices as activeSlices,
  type NonGoal,
  type Slice,
} from "./roadmapData";

const repo = "https://github.com/The-Running-Dev/SubZeroDev.Platform";

function SliceRow({ slice }: { slice: Slice }) {
  return (
    <tr>
      <td>{slice.id}</td>
      <td>{slice.title}</td>
      <td className="component-message">Depends on: {slice.dependsOn}</td>
      <td>
        {slice.pr ? (
          <ExternalLink href={slice.pr.url}>#{slice.pr.number}</ExternalLink>
        ) : (
          <span className="visually-hidden">No pull request yet</span>
        )}
      </td>
    </tr>
  );
}

function SliceTable({ slices }: { slices: readonly Slice[] }) {
  return (
    <div className="panel">
      <table className="status-table">
        <thead>
          <tr>
            <th scope="col">Slice</th>
            <th scope="col">Title</th>
            <th scope="col">Dependency</th>
            <th scope="col">PR</th>
          </tr>
        </thead>
        <tbody>
          {slices.map((slice) => (
            <SliceRow key={slice.id} slice={slice} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

export type RoadmapViewProps = {
  slices: readonly Slice[];
  nonGoals: readonly NonGoal[];
};

/**
 * The page for a given ledger. An empty `slices` is the between-efforts state:
 * no effort is active, so nothing is in progress or scheduled and no brief's
 * non-goals are in force. It says so, and links the last archived ledger.
 */
export function RoadmapView({ slices, nonGoals }: RoadmapViewProps) {
  const betweenEfforts = slices.length === 0;
  const shippedSlices = slices.filter((s) => s.status === "shipped");
  const currentSlice = slices.find((s) => s.status === "in-progress");
  const queuedSlices = slices.filter((s) => s.status === "queued");
  const ledgerHref = `${repo}/blob/main/${betweenEfforts ? lastArchivedLedger.path : "design/30-slices.md"}`;

  return (
    <>
      <SiteHeader current="roadmap" />
      <main>
        <section className="page-section hero" aria-labelledby="roadmap-title">
          <p className="section-index">INCIDENT HISTORY</p>
          {betweenEfforts ? (
            <>
              <h1 id="roadmap-title">Nothing In Progress.</h1>
              <p className="hero-sub">
                No effort is active, so there is no open ledger. The last one,{" "}
                {lastArchivedLedger.effort}&apos;s, is archived.
              </p>
            </>
          ) : (
            <>
              <h1 id="roadmap-title">
                {shippedSlices.length} of {slices.length} Slices Resolved.
              </h1>
              <p className="hero-sub">
                {currentSlice ? (
                  <>
                    <strong>{currentSlice.id}</strong> is the open
                    incident.{" "}
                  </>
                ) : null}
                {queuedSlices.length} scheduled. The queue is deterministic.
              </p>
            </>
          )}
        </section>

        <section
          id="resolved"
          className="page-section"
          aria-labelledby="resolved-title"
        >
          <p className="section-index">RESOLVED</p>
          <h2 id="resolved-title">Closed Incidents</h2>
          {shippedSlices.length > 0 ? (
            <SliceTable slices={[...shippedSlices].reverse()} />
          ) : betweenEfforts ? (
            <p className="component-message">
              No active ledger. The resolved incidents are in the{" "}
              <ExternalLink href={ledgerHref}>
                archived {lastArchivedLedger.effort} ledger
              </ExternalLink>
              .
            </p>
          ) : (
            <p className="component-message">Nothing resolved yet.</p>
          )}
        </section>

        <section
          id="ongoing"
          className="page-section"
          aria-labelledby="ongoing-title"
        >
          <p className="section-index">ONGOING</p>
          <h2 id="ongoing-title">Open Incident</h2>
          {currentSlice ? (
            <SliceTable slices={[currentSlice]} />
          ) : (
            <p className="component-message">Nothing currently in progress.</p>
          )}
        </section>

        <section
          id="scheduled"
          className="page-section"
          aria-labelledby="scheduled-title"
        >
          <p className="section-index">SCHEDULED</p>
          <h2 id="scheduled-title">Scheduled Maintenance</h2>
          {queuedSlices.length > 0 ? (
            <SliceTable slices={queuedSlices} />
          ) : (
            <p className="component-message">Nothing scheduled.</p>
          )}
        </section>

        <section
          id="wont-fix"
          className="page-section"
          aria-labelledby="wontfix-title"
        >
          <p className="section-index">WON&apos;T FIX</p>
          <h2 id="wontfix-title">Closed As Not Planned</h2>
          {betweenEfforts ? (
            <p className="component-message">
              No brief is active, so nothing is closed as not planned right now.
              The next effort&apos;s non-goals are published with its brief.
            </p>
          ) : (
            <ul className="wontfix-list">
              {nonGoals.map((goal) => (
                <li key={goal.title}>
                  <StatusPill tone="unknown" label="WON'T FIX" />
                  <div>
                    <strong>{goal.title}</strong>
                    <p>{goal.reason}</p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section
          id="continue"
          className="page-section"
          aria-labelledby="continue-title"
        >
          <p className="section-index">CONTINUE READING</p>
          <h2 id="continue-title">The Rest of the Record</h2>
          <p>
            <a href="/">Back to status</a> ·{" "}
            <ExternalLink href={ledgerHref}>
              {betweenEfforts
                ? `Read the last slice ledger (${lastArchivedLedger.effort}, archived)`
                : "Read the slice ledger"}
            </ExternalLink>{" "}
            · <ExternalLink href={repo}>Browse the repository</ExternalLink>
          </p>
        </section>
      </main>
      <SiteFooter />
    </>
  );
}

export default function RoadmapApp() {
  return <RoadmapView slices={activeSlices} nonGoals={activeNonGoals} />;
}
