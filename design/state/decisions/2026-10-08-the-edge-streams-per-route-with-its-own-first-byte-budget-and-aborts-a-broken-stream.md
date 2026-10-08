# decision/2026-10-08-the-edge-streams-per-route-with-its-own-first-byte-budget-and-aborts-a-broken-stream
Date: 2026-10-08
Anchor: 2026-10-08 — #110: the edge streams per route, with its own first-byte budget, and aborts a broken stream
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #110: the edge streams per route, with its own first-byte budget, and aborts a broken stream"

## Claim
Context — [#110](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/110), reconciling [#106](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/106). The edge buffered every response, which breaks SkyNet HR's SSE console (its own decision D10), and #106 made a buffered route's mid-body death an honest `503`. Ben ruled 2026-10-08: streaming is a per-route opt-in at the edge with its own time-to-first-byte budget, and a failure mid-stream aborts the stream.

Chosen — routes opt in under `GameEdge:StreamingRoutes`, each a `PathPrefix` matched ordinal on segment boundaries with a required `FirstByteTimeout` greater than zero and no greater than `ForwardTimeout`, validated at startup. The budget runs to the workload's response headers, the edge's commit point; after headers there is no edge deadline. A mid-stream failure aborts the response (no terminating chunk on HTTP/1.1, `RST_STREAM` on HTTP/2) with no edge-authored byte after headers, logged once as `EdgeStreamAborted` at Warning and counted once on `subzerodev.edge.stream.aborts`; caller disconnects are neither. An additive `ForwardStreamingAsync` leaves the buffered path, its byte identity and #106 untouched. No new `EdgeError` variant; exactly one attempt. WebSocket upgrade is not covered.

Rejected — a dictionary of prefixes (environment-variable keys), a global flag or glob patterns; a first-body-byte budget (holds headers back and times out an idle SSE stream); defaulting to `ForwardTimeout` (not "its own" budget); a total or idle timeout after headers; a clean end on failure (disguises truncation); an in-band error frame (teaches the edge content framing); rethrowing to `ErrorEnvelopeMiddleware` (logs an expected death as Error); a separate meter; changing `ForwardedResponse.Body` to a stream; a third `EdgeError` variant.

Reversibility — the key shape and the abort semantics are expensive once a deployment and a client rely on them; the rest is cheap.
