# Brief — Principals (G3)

> The problem, the non-goals and the definition of done are mine. A model may interrogate this
> file (`/brief`) or type it from my answers (`/interview`). It may not originate it from an idea.
>
> **Provenance of this draft:** typed by `/agentkit:interview` on 2026-11-01. Six scope answers
> I gave that day are recorded as stated. Everything else stated here comes from documents I wrote:
> the G3 entry in [`implementation-plan.md`](../docs/docs/implementation-plan.md) §5,
> [`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §5, §6.2 and §6.3, and
> [#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340). Readings the
> interview proceeded on are under *Assumptions and open questions*, labelled.

## Problem

A signed-in player has to stay signed in behind Platform's edge, and their games have to be theirs.

Today neither holds. The edge passes on only the request line, the body, `Content-Type` and trace
context. It returns only the status, `Content-Type` and the body. Anonymous reads work through it
byte for byte, but sign-in, sessions and browser clients on another origin do not. Adventures'
signed-in routes therefore call its API directly
([#340](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/340)).

Behind that sits the gap the hosting contract names. Once operations travel over a wire, an
unauthenticated or guessable session or save id is a cross-account read of another player's game
(§6.2). The engine says nothing about authorization, which is correct in process. Hosting is what
introduces the gap.

G3 adds authentication, ownership, and an authorization decorator that wraps the store (§6.3). It
also brings the account surface, which stays separate from the game surface (§5).

## What we do instead today (and what it costs)

Adventures' signed-in routes bypass the edge and call its API directly. Its anonymous routes go
through the edge (#340, observed during #97's stage-3 proving, Adventures#84).

## Who it is for

A game team putting a real game behind the edge, and that game's players. Adventures is the
first, live at `adventures.subzerodev.com` (#340). Ben is the operator.

## Non-goals

- **Guest ownership of Platform-owned state.** Only a signed-in principal owns sessions and saves
  in game-service's stores and under Platform's decorator. A workload's own guest principal, such
  as Adventures' guest session, is that workload's own. Stopping it from crossing the edge is not
  how this rule is enforced. *(Ruled 2026-11-01.)*
- **A raw-state endpoint, under any name.** Inherited unchanged from G1 and G2. No hosted
  endpoint returns engine state (implementation plan, G3 *Done when*).
- **Catalogue, publishing and metering.** These are G4's (implementation plan §5).
- **Multiplayer, white-label, AI-assisted authoring and community modding.** These are still
  deferred (implementation plan §5, *Still deferred*).

## Definition of done

**Ownership** (implementation plan, G3):

- The decorated store produces byte-identical `serialize()` output to the undecorated one.
- A session id belonging to another principal returns a refusal rather than data.
- No hosted endpoint returns engine state.
- Ownership is checked at the edge on every operation carrying an id, and ids are unguessable.
  Unguessable ids are defence in depth, never the authorization (hosting contract §6.2).
- Identity is Platform's. D5 has landed, so G3 adopts Platform's Identity rather than
  implementing it locally (implementation plan, G3).

**The caller's credential at the edge** (#340):

- G3's principal design records who owns the caller's credential at the edge, passed through to
  the workload or terminated there, and cites #340 as its outside evidence.
- The edge forwards, strips or answers each header #340 lists according to that ruling. There is
  a test per header in each direction, on buffered and streamed routes alike.
- #340's security notes hold whichever answer is chosen:
  - `Cookie` and `Origin` travel together or not at all.
  - The edge holds no cookie state, and
    `A_cookie_the_workload_sets_for_one_caller_is_never_sent_on_anothers_request` keeps passing.
  - The edge sets `X-Forwarded-*` from the connection and never copies it from the caller.
  - G1 invariant 41 survives as written or is explicitly restated.
- Adventures' guest session, OIDC sign-in and a cross-origin write are proven end to end through
  the edge. *(Kept in G3, 2026-11-01.)*

**The account surface** *(ruled 2026-11-01)*:

- A signed-in player can list their own saves and delete their account. Both are on the account
  surface, never added to the game surface, and the game surface stays exactly as wide as the
  engine's (hosting contract §5).

**Public** *(ruled 2026-11-01)*:

- `adventures-api.subzerodev.com` is served through Platform's edge on the existing VPS, with TLS
  and real players.
- Abuse limits are in scope.

## Narrowest useful version

## Environment

- **The edge** is .NET, `workloads/game-edge/`, today a buffering unary forwarder
  ([ADR-007](../docs/docs/adr/ADR-007-second-hosted-workload.md)). **game-service** is the Node
  workload in `workloads/game-service/`, holding G2's durable stores and the storage seam G2
  declared for this decorator.
- **Platform's Identity** comes from D5. Its principal kinds are `Anonymous`, `Account`,
  `Delegated` and `System`, and a principal id is issuer plus subject. It includes an optional
  **`SubZeroDev.Platform.SignIn`** module ([`d5/90-decisions.md`](d5/90-decisions.md), 2026-10-08).
  The module is stateless: its session is an encrypted cookie, with no refresh token and no client
  secret. That ruling partly reverses 2026-09-25, "Platform validates credentials and never
  conducts a sign-in".
- **Adventures** is a separate repository and a hosted workload, not a package consumer
  ([#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97)). It runs live on a VPS
  at `adventures.subzerodev.com`, with its API at `adventures-api.subzerodev.com`. It signs players
  in through OIDC, keeps sessions in the `sza_session` cookie, and answers CORS itself (#340).
- **Reachable from the public internet.** This lifts G1's and G2's trusted-local limit. *(Ruled
  2026-11-01.)*

## Lifespan

## Assumptions and open questions

- Assumed: maintained for years. The principal model is public contract, Adventures has live
  players, and G2's brief was built to last on the same reasoning.
- Assumed: the edge stays an application under `workloads/game-edge/`. G3 does not do ADR-007's
  extraction into a Platform package, which that ADR schedules as a new effort with its own brief.
- Assumed: the evidence runs in CI from a fresh clone, except the live VPS deployment, as G2's did.
- Open: **pass-through or termination** for the caller's credential at the edge. This is `/design`'s
  to decide (#340). The guest rule above constrains it. Under termination, Adventures' guest
  session still has to cross as the workload's own principal.
- Open: whether an unknown id and someone else's id look the same to a caller. The Adventures POC
  answers an unknown id with 404 and an owned one with 403. The implementation plan says to decide
  this on purpose.
- Open: **MCP bypasses the edge** (named in G1, repeated in #340). Is ownership on the MCP path in
  G3's scope? This affects whether "every operation carrying an id" includes MCP tool calls.
- Open: **what the abuse limits are.** Per principal, per address or both, and what a limited
  caller sees.
- Open: **what deleting an account removes.** Platform-owned state only, or also what the workload
  holds for that principal. Adventures has its own database.
- Open: which Adventures routes move behind the edge in G3: all of its API, or only those #340
  names.
