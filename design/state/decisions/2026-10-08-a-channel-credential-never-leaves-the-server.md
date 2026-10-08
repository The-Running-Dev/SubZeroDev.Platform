# decision/2026-10-08-a-channel-credential-never-leaves-the-server
Date: 2026-10-08
Anchor: 2026-10-08 — #25: a channel credential never leaves the server
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #25: a channel credential never leaves the server"

## Claim
Context — [#25](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/25). A live Discord webhook is committed in the BarStrad repository, and a second reaches its browser bundle.

Chosen — Notifications carries the rule that a channel credential is held and used by the host, never sent to a browser, a client configuration or a committed file (`events-and-notifications.md` *Delivery failure*). This is stronger than "a secret appears in no log, span or metric label". The issue stays open until the owner rotates the webhooks in the BarStrad repository. Ben ruled 2026-10-08.

Rejected — relying on the log-redaction rule, which a bundled credential never touches.

Reversibility — cheap.
