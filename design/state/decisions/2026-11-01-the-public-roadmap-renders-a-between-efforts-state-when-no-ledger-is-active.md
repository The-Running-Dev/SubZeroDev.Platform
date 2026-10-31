# decision/2026-11-01-the-public-roadmap-renders-a-between-efforts-state-when-no-ledger-is-active
Date: 2026-11-01
Anchor: 2026-11-01 — The public roadmap renders a between-efforts state when no ledger is active
Status: accepted
SupersededBy:
StatedIn: "unit/document/d5-90-decisions § 2026-11-01 — The public roadmap renders a between-efforts state when no ledger is active"

## Claim
Context — amends `decision/2026-10-08-the-public-roadmap-renders-the-active-effort-s-slices` (#80) for the interval in which no ledger exists. The schema has no amends field, so that record is left as written and stays accepted; this one qualifies it. The roadmap imported `design/30-slices.md` statically, and archiving D5 removes the file, which would fail the site build. Between efforts there is also no active brief, so D5's hand-authored non-goals describe nothing in force.

Chosen — the data module reads the active ledger through a single-file glob, so an absent file yields an empty ledger and the build passes. With no ledger the page says "Nothing In Progress.", keeps its four sections each stating there is nothing to show, shows no non-goals, and links the last archived ledger `design/d5/30-slices.md`, a hand-maintained constant that a test requires to exist. A present ledger is parsed and checked as before; a present but empty or malformed one still fails the build. Ben ruled 2026-11-01.

Rejected — pointing the import at `design/d5/30-slices.md` until G3 has slices (presents a closed effort as current, the failure #80 was filed against); deriving the last archived ledger by glob (effort directories carry no order, and the glob would bundle every archived ledger).

Reversibility — cheap.
