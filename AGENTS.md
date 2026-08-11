# Agent contract

This file is binding for every agent session in this repo, regardless of tool or model.

## What this project is

**SubZeroDev.Platform — the reusable application framework and hosting layer.** Hosting,
configuration, identity, authorization, tenancy, billing, notifications, storage, events,
observability, API and MCP conventions — built once, reused by every product.

**Platform is not a product.** It never depends on one, and never on a plugin:

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

A reference from Platform to a product is a build failure, not a review comment.

- **Start here:** [`docs/docs/platform-identity.md`](docs/docs/platform-identity.md) — what
  this repository is, and the collision it settles. Everything else depends on it.
- The reading order is the sidebar order: identity → platform specification → Game Engine as
  a Service → engine hosting contract → MCP tool contract → packages → second-consumer
  packages → application modules → implementation plan → ADRs.
- **Game Engine as a Service (GEaaS)** is one *hosted workload*, not what this repository is.
  Formerly "NEaaS — Narrative Engine as a Service"; renamed because the engine ships three
  kinds and only one of them is narrative.

**Companions:**
- **Engine** — [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine):
  the Game Engine, source and specs. Past MVP.
- **Game** — [SubZeroDev.GameOfLife](https://github.com/The-Running-Dev/SubZeroDev.GameOfLife)
  (Life in the Fast Lane) and
  [SubZeroDev.SunTrap](https://github.com/The-Running-Dev/SubZeroDev.SunTrap).
- **Architecture** — [SubZeroDev.Architecture](https://github.com/The-Running-Dev/SubZeroDev.Architecture)
  (private; working copy at `D:\Dropbox\Projects\SubZeroDev\Specs`): cross-cutting
  specifications and ADRs, plus specifications staged for repositories that do not exist yet.
  The `SubZeroDev.Platform/` documents have been **moved** here and the originals deleted, per
  *Move, never copy* under **Single ownership** below. The other directories there belong to
  other repositories and are not this one's to touch.

The docs render as a Docusaurus site via `docs.ps1`. The shared docs-site / graphify /
claude-mem tooling notes live in the engine repo's `CLAUDE.md` and apply identically here.

Lessons learned the hard way live in [`agent.md`](agent.md) — read it after this file.

## Source of truth

**Today**, authority runs:

1. `docs/docs/platform-identity.md` — what this repository is. Everything else depends on it.
2. The sidebar reading order, above.

**Once design work runs through the pipeline** (`/brief-check` → `/reconcile`), the documents
in `design/` outrank the code, in this order:

1. `design/00-brief.md` — problem, non-goals, definition of done
2. `design/20-contract.md` — types, schemas, signatures, error semantics
3. `design/10-design.md` — architecture, data model, failure modes
4. `design/30-slices.md` — work breakdown and acceptance criteria
5. `design/90-decisions.md` — append-only decision log

`design/` is empty until a brief is written, and does not yet outrank `docs/docs/`. When it
holds a contract for a package, that contract is authoritative **for that package**;
`platform-identity.md` stays authoritative for what this repository is.

If the code contradicts the contract, that is a defect in one of them. **Stop and say which
one you think is wrong. Do not silently reconcile.**

## Safe start

Before editing anything:

```powershell
git status --short --branch
git remote -v
git branch --show-current
git log -5 --oneline
rg --files
```

- Discover files and tooling rather than assuming they exist.
- Read this file and the sources you are about to change **completely**. Editing from memory, or from a diff, is the most common cause of drift.
- Preserve unrelated and uncommitted work. Never stage, reset, clean, or overwrite it.
- Work on a focused branch.
- Where guidance conflicts, follow the most specific applicable instruction.

## Effort and model selection

Match capability and reasoning effort to the **task**, not to the tool that reached it and not to the number of files involved. Budget scales with **complexity, not size** — a one-line change to an invariant is architectural; a 500-line transcription against a settled contract is not.

| Tier | Work | Effort |
|---|---|---|
| **Deep reasoning** | Architecture, contracts, API and seam design, root-cause analysis, multi-step planning, security and performance strategy, comparing materially different approaches | Strongest model, high or xhigh |
| **Implementation** | Code against a settled contract, tests, refactors, bug fixes, CI and infrastructure, docs coupled to implementation | Mid tier; high effort for large or hard changes, standard for small ones |
| **High volume** | Summaries, changelogs, commit messages, PR descriptions, formatting, triage, log and tool-output summarisation | Cheapest tier, default effort |

**Escalate rather than guess.** A high-volume task that raises an implementation question becomes implementation tier; an implementation task that raises an architectural question becomes deep reasoning. **Do not keep implementing while that uncertainty is unresolved.**

**Open substantive work with a banner, then gate on it.** Before starting anything beyond a trivial lookup, state what the work is (task or command, plus slice id if applicable) and the tier it requires per *Command routing* or the table above. **It is a heading, not a sentence** — three plain lines fenced above and below by a rule of `=`, labels and tier names in Title Case, never folded into a paragraph. For example:

  ```
  ===============================
  Work: /design — write design/10-design.md
  Tier: Deep Reasoning → opus/high
  Session: opus
  ===============================
  ```

  Then check the session's actual model against the required family. If it matches exactly, proceed without further comment. Any mismatch gates the same way, in either direction: **stop before doing any expensive work**, name the tier the task actually needs, and wait — do not proceed on the wrong tier unless the user explicitly overrides after seeing the mismatch. Under-powered, name the stronger model needed. Over-powered, name the lighter tier that fits — running deep reasoning against implementation-tier work is the same unbudgeted cost as running implementation-tier reasoning against a task that needed more of it, just paid in the other direction. Where the model itself can't be changed mid-session (*Division of control*, next), the override this gate waits for can also be "cap your own reasoning effort to the lighter tier and proceed" rather than a model swap.

**Division of control.** I set the session model. You set subagent models and scale your own reasoning depth. You cannot change your own session model.

**Never use `max` effort unless I ask for it by name.**
- **`xhigh` is for one question, not one pipeline.** Running a whole design phase at `xhigh` is not rigour, it is a substitute for asking a precise question.
- **Escalate rather than guess.** A high-volume task that raises an implementation question becomes implementation tier; an implementation task that raises an architectural question becomes deep reasoning. **Do not keep implementing while that uncertainty is unresolved.**

### Command routing

| Command | Tier | Notes |
|---|---|---|
| `/brief-check`, `/design`, `/contract`, `/slices` | `opus`, `high` | — |
| `/redteam` | strongest model, **different vendor from the design author** | If it must be Claude, a fresh `opus`, `high` session |
| `/slice` | `sonnet`, `medium` | `high` for a large or difficult slice |
| `/reconcile` | `opus`, `high` to decide which side of a drift is correct | `sonnet`, `medium` for the mechanical edits once I have decided |
| `/make-human-docs` | `sonnet`, `medium` | Escalate only if the design turns out to be ambiguous — then stop, do not resolve it in prose |
| `/track` | `sonnet`, `medium` | Mechanical sync; escalate only to judge whether a drifted slice is a design change |
| `/verify` | `sonnet`, `medium` | Escalate to deep reasoning only to diagnose a failure, never to run the gates |
| `/code-review` | review agents run at the effort passed (e.g. `high`); adjudicating findings is deep-reasoning tier, `opus`/`high` | The effort argument sets how hard the review agents think, not the session model, which stays mine to set. A contract contradiction it surfaces goes in the slice's PR description, not a `design/` edit, while `design/FROZEN.md` exists |
| `/pr` | `sonnet`, `medium` | Runs `/verify` and `/resolve` as its own phases — the same tier, and the same escalation rules, apply inside them |
| `/resolve` | `sonnet`, `medium` | Escalate to judge a contested finding, not to triage the obvious ones |
| `/fix` | `sonnet`, `medium` | Escalate only where the fix turns out to need a contract, schema, or public-interface change — that is `/contract`'s or `/design`'s, and this command stops rather than absorbing it |
| `/refine` | `sonnet`, `medium` | Never escalates — an architectural ask is routed to the command that owns it, not refined |
| `/install` | `sonnet`, `medium` | — |
| `/install-all` | `sonnet`, `medium` | Escalate only to judge whether a per-repo hard stop is actually safe to resolve — never to resolve it unattended |
| `/kit-sync` | `sonnet`, `medium` | Escalate only to judge whether a refused fast-forward in `~/.agent-kit` is safe to resolve — never to force past it unattended |
| `/kit-help` | `haiku`, `low` | Orientation from file existence and a tracker listing. Escalate only where the repository's state matches no stage |
| `/done` | `haiku`, `low` | Mechanical git housekeeping — branch switch, `--merged` check, prune. Escalate only to judge whether an unmerged-looking branch is actually safe to delete |
| `/freeze` | `sonnet`, `medium` | `Frozen because`/`Lifts when` come from the user, never invented — ask rather than draft them |
| `/unfreeze` | `sonnet`, `medium` for the sequencing; runs `/reconcile` (`opus`, `high`) and `/track` (`sonnet`, `medium`) as its own phases | Runs unattended, no confirmation prompt — that is this repository's policy, not a gap |

**Never recommend re-running a phase gate.** I decide when a phase repeats. This holds outside `/redteam` too — see that command for its own stopping rule.

### Session boundaries

The tiers above say which model runs a command. This says **when a session must end.** A boundary exists wherever carrying context would corrupt the next step's judgement, or wherever the next step must read the tree rather than remember it. **The artifact is the handoff, not the conversation** — a stage that writes one has already handed over everything the next stage is entitled to.

| Boundary | Rule | Why |
|---|---|---|
| `/design` → `/redteam` | **Fresh session, and a different vendor.** | A model recognises its own output distribution and defends it. Fresh context on the same model is already the weak form; the same session is not a review at all. |
| Any stage that writes an artifact → the next | Fresh. | The next stage's input is the committed file. A session that also remembers the arguments behind it will design against the arguments. |
| `/slices` → `/slice` | Fresh, and **one slice per session**. | A slice that does not fit one session without compaction is too large — that is a `/slices` defect, so say so rather than pressing on. |
| `/slice` → `/pr` | **Same session.** | `/pr` acts on the branch and worktree the slice just produced, and runs the gates and the review threads as its own phases (`.claude/commands/pr.md`). The gate report goes into the PR description's `Verified` section **verbatim**; a fresh session would restate it from a summary, which is the fabricated gate result *Verification* exists to prevent. |
| `/fix` → `/pr` | **Same session.** | Same reason as the slice loop above: `/pr` acts on the branch and worktree `/fix` just produced, and the did-not-run list must be carried verbatim into the PR rather than restated from a summary. |
| merge → `/track` | Fresh. | `/track` reads the tracker and `design/` as they now stand. The session that just implemented the slice holds an opinion about whether it is done, and doneness is my mark, not an agent's. |
| implementation → `/reconcile` | Fresh. | It compares the tree against the docs. The session that wrote the code carries what it *intended* to write, which is the one thing the comparison must not be given. |

**Compaction is a boundary you did not choose.** If a session compacts mid-slice, report it — the slice was mis-sized, and the work after the compaction was done against a summary of the contract rather than the contract.

**End a response that lands on a fresh-session boundary with a banner, not a footnote.** A boundary buried in the last sentence of a report gets carried into the next reply of the same session out of habit, which is the exact failure the boundary exists to prevent. Set it off as a heading in the same form as the work-start banner — `=` rules, Title Case, plain lines — naming: the boundary just crossed, the next command, and its tier from *Command routing*. For example:

```
===============================
Session Boundary — Do Not Carry Into /track
Next: /track, Fresh Session, sonnet/medium
===============================
```

Do not run the next command yourself. Ending a session may be the next step, and a command that starts work cannot also tell the user to start a new one for it — that restriction is unchanged, only how visibly the handoff is stated.

An ADR is not a design cycle and does not inherit this table. `docs/docs/` decisions are authored directly; the boundaries above govern the `design/` pipeline.

### What should stop being model work

The tiers above decide *which* model does a job. This decides whether a model should be doing it at all.

| | Work | Where it belongs |
|---|---|---|
| 🟢 **Necessary** | Architecture, contracts, root-cause analysis, design tradeoffs, adjudicating findings | A model, at the tier above |
| 🟡 **Maybe avoidable** | Regenerating context already established, duplicate repository scans, rewriting boilerplate | A model, but the repetition is a signal — say so |
| 🔴 **Definitely avoidable** | Formatting, mechanical text transformation, arithmetic over files, counting, collecting metrics | Code. It should leave the model entirely |

**A red item is a defect in the tooling, not in the run.** Noticing one is worth a line; performing it repeatedly and never saying so is the failure. When a red item recurs, put it in `## Open` in `design/90-decisions.md` so `/track` can turn it into an issue — that is the existing path, and there is no separate mechanism for this. ADR-005's projected boundary contracts are this repository's precedent: authoring them by hand was red, so it stopped being model work.

Two distinctions that are easy to get wrong:

- **The mechanical half of a task is red; the judgement half is not.** Opening an issue is an API call, but deciding what warrants one is not. Writing a PR description is a template, but which merge convention governs is not. Do not classify a whole command by its cheapest step.
- **Do not report a cost you did not measure.** A model is not given its own token counts or elapsed time, so any figure it states about its own run is an estimate presented as a measurement. `tools/Measure-Session.ps1` reads the real per-call usage from the session transcript, and runs as a `SessionEnd` hook. Use it, or say nothing. It measures **Claude Code sessions only** — Codex writes a different schema this has no reader for, and Copilot records no token usage at all. Under either, *say nothing* is the whole instruction.

## Hard rules

- **This repository is design-stage, and that is the current constraint** — not deferral. The
  near-term package set is unstarted. Technology is settled
  ([ADR-002](docs/docs/adr/ADR-002-implementation-technology.md): .NET, with the product
  boundary a process boundary), so D3 is unblocked but unstarted. Do not build Platform
  packages or hosted game features ahead of the
  [implementation plan](docs/docs/implementation-plan.md)'s stated ordering constraints.
- **Non-goals are binding.** Anything listed as a non-goal in the brief is out of scope even if it looks trivial, even if you are already touching that file.
- **One slice at a time.** Do not start slice N+1 because you noticed something while doing slice N. Write it to `design/90-decisions.md` under `## Open` instead.
- **Prefer an existing package or service to hand-rolled infrastructure.** Hand-rolling is what
  needs justifying, not taking a dependency — check NuGet, and check whether a product or managed
  service already is the capability, before writing anything. What
  [ADR-004](docs/docs/adr/ADR-004-framework-build-not-adopt.md) rejects is adopting a whole
  *application framework*, not using *libraries* or *services*; do not read it as licence to build
  everything. **Build only what is genuinely ours.**
- **Depend on the protocol, not the vendor**, and check the deployment modes before taking a
  service. Self-hosted and homelab installations cannot reach a vendor's SaaS tenant, so a
  SaaS-only dependency needs a self-hostable path or must be optional. OIDC over a named identity
  provider; an S3-compatible API over one vendor's SDK.
- **No new dependencies** without a decision-log entry naming the alternatives rejected and why.
  This is not in tension with the rule above: the bar was never "avoid dependencies", it is "choose
  them deliberately and say why". Record the reason when you take one **and** when you pass one over
  to write your own.
- **No new public interfaces** that are not in `design/20-contract.md`. If you need one, stop and ask for a contract amendment.
- **Ask instead of assuming.** If two readings of the spec are both defensible, stop and present both. Do not pick one and proceed.
- **Every slice ends runnable.** No half-wired states committed.

## The design freeze

The pipeline's normal loop keeps `design/` live: a slice lands, `/reconcile` writes reality back, `/track` resyncs the tracker. That is right while the design is still being settled and **wrong once implementation is the bottleneck**, because each pass is generative rather than merely checking — landing slice N rewrites slice N+1's specification, which desyncs the tracker, which needs `/track`, which finds drift, which needs `/reconcile`. The loop has no fixed point. Freezing is how it is escaped.

**`design/FROZEN.md` is the marker, and its existence is the whole mechanism.** It is tracked, not ignored — a freeze is a statement to everyone working in the repository, not local state. While it exists:

- **`/reconcile` and `/track` do not run.** The tracker is deliberately allowed to go stale.
- **`/design`, `/contract` and `/slices` refuse.** Authoring is gated too, so the docs cannot drift forward while the implementation is being checked against them.
- **Slices implement against `20-contract.md` as a fixed artifact**, at the SHA the marker names.
- **A contradiction found while implementing is stated in that slice's pull request and left in the document.** Do not fix it in `design/`. The staleness is the point; recording it in the PR is what makes the eventual reconciliation cheap.

**`/freeze` writes the marker; `/unfreeze` lifts it** — deletes the file, then runs one reconciliation pass, `/reconcile` then `/track`, in the same session. `/unfreeze` runs unattended, without a confirmation prompt; the freeze itself is still the user's decision, made when `/freeze` is invoked, and lifting it early is one command call away rather than gated a second time. A slice that turns out to need a contract amendment still stops and says so; that escalation is the user's to answer, and answering it may well be "thaw, amend, re-freeze."

The marker's format, which the five gated commands read and must not restate:

```markdown
# design/ is frozen

Frozen at: <sha>, <YYYY-MM-DD>
Frozen because: <what the freeze is escaping>
Lifts when: <the checkable condition — "tier one is code-complete", not "when we are ready">

To lift: run `/unfreeze`, or delete this file by hand and run `/reconcile`, then `/track`.
```

A command that refuses reports `Frozen because` and `Lifts when` **verbatim** rather than paraphrasing them — the point of a stated condition is that it can be checked against, and a paraphrase is where it stops being checkable.

## Single ownership

- **The extraction guard governs what may be added.** A candidate becomes a Platform package
  when a **second** consumer needs it, not when the first one does. Record premature ideas as
  intent, not as a build target.
- **"Phase N" is the ecosystem roadmap's, and no document here defines a second one.** That
  roadmap holds the phase vocabulary for the whole ecosystem; this repository references a
  phase, never renumbers one. Local sequences use a distinct prefix and must not be readable
  as a phase — `D0–D5` for the design and build stages, `G1–G4` for Game Engine hosting.
  They were `P0–P5`, which read as "Phase", and `D3` (ecosystem Phase 2) was taken for
  Phase 3. If you add a sequence, pick a letter that cannot be misread and say which phase
  each stage maps to.
- **Reference, never restate.** A rule that lives in another document is linked, not copied. Two copies of a rule is a promise they will diverge and a guarantee nobody notices which is stale.
- **Move, never copy.** A rule has exactly one home. When it belongs somewhere else, move it and leave a reference behind.
- If a document genuinely must repeat something to stand on its own, name the canonical copy in the text and change both in the same commit. Naming a canonical copy is what makes the others checkable.

## Verification

- **Verify, don't assert.** State only what you have checked. Assert nothing from memory that a command could confirm — remembered values and inferred contracts are how wrong facts get written down confidently.
- **Do not claim a gate passed that did not run.** If a tool is unavailable, say so plainly and name what was not checked. `./docs.ps1 -BuildOnly` needs Docker; when it is unavailable, say so rather than reporting the build as passing.
- **Never state or imply a deployed URL** until the deploy for that exact commit reports success. A merged PR is not a deployed site. Poll; do not estimate.
- **A schema or validator change is not done until it has rejected something.** Positive and negative cases both, with the counts stated. A validator that has never failed is not known to constrain anything.

## Working with me

- Findings and review items are presented **one at a time for sign-off**, not applied in bulk.
  When a suggestion is declined, record it in the affected document as a known-and-retained
  issue rather than dropping it silently.
- Surface real forks as a question with a recommendation, recommended option first. I routinely pick the more rigorous non-recommended option — so ask, do not assume.
- **A reconciliation ends in a decision, not a report.** Any time you compare two things and find they disagree — `/reconcile`, `/install`, `/track` drift, or any time I say "reconcile" — the work is not finished at the findings. Close by asking, one divergence at a time, each with a recommendation and what the alternatives cost. **A report I have to turn into questions myself is half the job.** Recommend the *resolution*: what changes, in which file, and what reversing it costs. If nothing diverged, say so plainly rather than manufacturing a fork.
- `/redteam` is the one exception, and only partly: it must not propose fixes, since naming a fix frames the problem. It still recommends a **classification** — defect, accepted risk, brief conflict, or not sustained.
- Ask before any choice that sets policy or a public contract: licensing, compatibility promises, a major information-architecture change.
- Call out assumptions, unverified claims, and known risks plainly. Explain the concrete evidence behind a recommendation.
- **Never tell me to go edit `design/` or the brief myself.** State what needs to change and why, give a recommendation, ask me to decide — then make the edit. Handing me a diff to type in by hand is not a lighter-weight version of doing the work, it is the same work with an extra round trip. Where the change belongs to a different command's tier (a contract amendment is `/contract`'s, a redesign is `/design`'s), name that command and its tier and say the edit happens there — still not as homework for me to do by hand.

## Git and delivery

- Branch off `main`; stage by explicit named path — never `git add -A`, `git add .`, or a bare
  directory. Never force-push or rewrite published history. Open the PR and leave the merge to
  the repository owner. Commit messages follow the descriptive style used across these
  repositories, not Conventional Commits.
- Run `git diff --check` before committing. Never use trailing double-spaces for a line break; it rejects them.
- If a pushed commit needs changing, add a follow-up commit.
- **Push every commit before announcing a PR is ready.** Announcing invites an immediate merge, and a commit pushed after that lands on a branch nobody merges.
- **Committing and pushing to a non-default branch are delegated in this repository.** Whenever a change is made on a branch other than the default, commit it (staged by named path, per above) and push immediately — no separate ask, and no waiting for the user to request the commit. This is narrower than it sounds: it covers landing work on the branch it was made on, nothing more.
- External writes still need my authorization beyond that: creating a remote repository, changing visibility, pushing **to the default branch**, merging pull requests, changing a domain, deploying. **Discussing a decision does not authorize it.** Carve-outs: GitHub issue, milestone, and project writes (*Tracking work*), commit-and-push to a non-default branch (above), and **opening a pull request** — `/slice`, `/fix`, and `/pr` all open theirs without asking (`.claude/commands/slice.md`, `.claude/commands/fix.md`, `.claude/commands/pr.md`). **Never as a draft.** A draft is invisible to reviewers and to CI gates that ignore drafts, which splits "opened" from "actually in review" and leaves someone to reconcile the two by hand; an open PR is reverted by closing it, which is as cheap as closing an issue. **Merging is not carved out and stays mine.**
- Do not delete files, branches, or history without explicit authorization.
- **Deleting a local branch `/done` independently confirms via `git branch --merged` is delegated in this repository.** `/done` (`.claude/commands/done.md`) runs proactively — as soon as a merge is on the table, not only when asked — and deletes every branch on that confirmed list without a chat confirmation first; the `--merged` check is the authorization. It also may stash (never discard) a dirty tree to unblock its own branch switch, and always reports the stash back rather than popping it silently. This delegation stops exactly where `--merged` stops: a branch it did not confirm, or a `-d` refusal on one it did, still needs a separate ask before anything stronger (`-D`) is even considered.
- Check review **threads**, not just requested reviewers — an automated reviewer can leave blocking conversation threads that do not appear in a reviewer listing. Resolve a thread only when a validated fix satisfies it; leave ambiguous findings open and report them. `/resolve` does this; the query it needs is written out there.
- **Resolving or replying to a review thread is delegated in this repository.** `/resolve` (`.claude/commands/resolve.md`) pushes the fix, updates the pull request, and resolves every `Defect`-class thread it satisfies **without asking first** — this repository's own convention overrides the general external-write rule for this one action. This delegation is unavailable in a repository I do not own — every action there is requested individually, the same boundary every carve-out in *Tracking work* stops at (**I9**). `Ambiguous`-class threads are still brought to me one at a time; delegation covers execution of a classification already made, not the classification itself. The five classes, and what happens to each, stay owned by `resolve.md`.

## Tracking work

**Defer work to the tracker rather than processing it inline.** A finding, a follow-up, or a
defect noticed in passing goes to a GitHub issue — not into a running list in the conversation,
and not into a section of a document that will rot. Prose is where work goes to be forgotten.

- **Opening, labelling, closing, commenting on, and editing an issue is carved out of the
  authorization rule**, in a repository I own — including one opened by someone else. Issues are
  cheap and reversible, which is the entire justification.
- **Milestones and projects are carved out too**, in a repository I own. Creating one no longer
  needs approval; deleting one still does, since that direction is not cheaply reversible.
- **Writing to a repository I do not own is never carved out.** That boundary is the one this
  section does not relax.
- **`/track` owns every GitHub write it can make idempotent.** No other command creates issues,
  milestones, or projects. It is idempotent, so run it often rather than batching. Closing an
  issue and ticking a checkbox are the exceptions — the command that observes the work done does
  those directly, in the same run, rather than waiting for a sync pass.
- It opens one issue per slice in `design/30-slices.md` that lacks one, matching on title
  across open **and** closed issues so a finished slice is never reopened or duplicated. It
  opens one issue per bullet under `## Open` in `design/90-decisions.md`, removing the bullet
  once tracked — that section is a staging area, not a home.
- If a matching GitHub Project exists (named after this repository), `/track` adds the issues
  it opens to it. If none exists, it creates one named after the repository and adds every
  issue it opened, since project creation is carved out the same as an issue or a milestone.
- `design/30-slices.md` stays authoritative for what a slice *is*; its issue tracks whether it
  is *done*. If the two come to describe the work differently, say so rather than editing either.
- **Every issue reads human-first, as a user story** — who this is for and what changes for them, in plain sentences. No pixel values, breakpoints, thresholds, file paths, or investigative notes about the tracker's own state ("the doc still says X but PR #Y already merged") in that narrative — those are ADR-style detail and belong in the agent block, however tempting it is to leave a note where it will be seen first. Then `### Done when` checkboxes — these are allowed to be precise and technical, since they exist to be checked, not read as prose — then the agent detail in a collapsed `<details>` block.
- **The agent block is fenced** by `<!-- agent:start -->` and `<!-- agent:end -->`. Inside the
  fence is regenerable; **outside it, a regenerating command never rewrites anything** — an
  edited narrative is someone's deliberate wording, and a stale copy gets fixed by hand, not
  overwritten. The one narrow exception is a `Done when` checkbox, which the command that
  confirms a criterion ticks directly, in place, outside the fence.
- **Where a document already governs, the block points; where none does, it carries.** A slice
  names `design/30-slices.md § S<n> @ <sha>` and leaves procedure to `.claude/commands/slice.md`
  — copying stop conditions into an issue freezes a stale copy that nothing can go back and fix.
  A bug or a story has no upstream document, so its block legitimately holds the constraints.
- **Criteria carry stable ids** (`S3.1`), and drift is compared on ids, never prose. Reworded
  criteria are not drift; an added, removed, or renumbered id is.
- **Report drift, change neither side.** Which is wrong is my call.
- **Ticking a checkbox is carved out of the authorization rule, the same as opening an issue.**
  `/slice` ticks a `Done when` box in the same run it reports the criterion met, by id, so the
  tick is traceable to the report that justified it rather than a separate confirmation.
- **Bugs and stories are filed by hand** from `.github/ISSUE_TEMPLATE/`. `/track` does not open them — with one narrowing: `/fix` (`.claude/commands/fix.md`), on its description path, files one bug issue itself, and only after reproducing the defect. It never files one for a defect it could not reproduce.
- **This does not suspend one-at-a-time sign-off.** Findings are still presented for
  adjudication; the tracker is where the ones you accept go, not a way to skip the conversation.

## Decision logging

Two homes, one boundary. **Does anyone outside this repository need to cite it?**

- **Yes, or it affects a published contract** → an ADR in `docs/docs/adr/`, numbered. The moved-in ecosystem specifications cite ADRs by number across repositories, so those numbers are stable targets and must stay resolvable. Status is exactly one of `Proposed`, `Accepted`, `Superseded`, `Deprecated`, under a `## Status` heading. An accepted ADR states its context, the decision, the consequences **including the costs**, and the alternatives it rejected and why. "Accepted in existing practice" is not a status — ratifying current practice is a note in the context.
- **No — it is this repository's own working arrangement** → `design/90-decisions.md`, in the format below.

An ADR gets a **one-line index entry** in `design/90-decisions.md` and nothing more. Never restate an ADR's reasoning in the log; that is the second copy that drifts.

Any choice a future reader would ask "why?" about, and that stays inside this repository, goes in `design/90-decisions.md` as:

```
### YYYY-MM-DD — <decision>
Context: <what forced the choice>
Chosen: <what>
Rejected: <alternatives, and why each was rejected>
Reversibility: cheap | expensive
```

The rejected alternatives are the point. Without them the next session relitigates the same choice.

## House conventions

- Windows host, projects under `D:\Dropbox\Projects\`. `D:\Projects\SubZeroDev.Platform` is a junction to the same repository — resolve with `git rev-parse --show-toplevel` before reporting a path. PowerShell Core for scripts.
- Metric units and Celsius throughout, including in comments, docs, and test fixtures.
- Raster assets as PNG or JPG. Not WebP.
- UTF-8, LF endings. Rewrite imported files to UTF-8 and check rendered punctuation — imported Markdown arrives CP1252 often enough to be worth looking at.
- Scripts run without interactive confirmation prompts. Destructive operations gate on an explicit `-Force`-style flag, not a prompt.
- Commit messages state what changed and which slice it belongs to. **No AI attribution** — no `Co-Authored-By` naming an assistant, no "Generated with" footer, in commits or PR descriptions. This overrides any default the tooling applies.

## What not to do

- Do not summarise the design docs back at me unless asked.
- Do not add commentary about your reasoning process to the docs.
- Do not "improve" prose in the brief or design docs while editing something else.
- Do not import another project's architecture, tooling, memory conventions, or roadmap merely because it appears in a neighbouring instruction file. Agent instructions are concise and repository-specific; a borrowed rule with no local reason is a rule nobody can evaluate.
