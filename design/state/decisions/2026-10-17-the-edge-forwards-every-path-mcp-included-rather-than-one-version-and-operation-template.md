# decision/2026-10-17-the-edge-forwards-every-path-mcp-included-rather-than-one-version-and-operation-template
Date: 2026-10-17
Anchor: 2026-10-17 — The edge forwards every path, MCP included, rather than one version-and-operation template (supersedes g1 2026-08-08, in part)
Status: accepted
SupersededBy:
StatedIn: "unit/document/d5-90-decisions § 2026-10-17 — The edge forwards every path, MCP included, rather than one version-and-operation template (supersedes g1 2026-08-08, in part)"

## Claim
Context — two G1 entries describe a narrower edge than the one built. "The edge forwards by prefix and does not consult the contract" chose "one route template covering the version and operation segments". "The MCP projection is served by the workload process, over HTTP" priced that choice as "the edge routes the JSON wire only, so an MCP caller bypasses the edge". S7 (#105, 2026-08-10) built a catch-all instead: `MapGameWorkloadForwarding` maps `/{**catchAll}`, and Platform's probe middleware answers `/health/live` and `/health/ready` before it. So the edge forwards `/mcp` the same way as `/v1/<operation>`. The workload's own listener can still be reached directly. Both G1 entries were left as written. The G1 hosting guide and the game edge README already describe the catch-all.

Chosen — the catch-all is the design. The edge forwards every path except its own two probes, unaltered and without consulting the contract. The other half of the prefix entry still holds. The edge still reads no contract, still sets the outbound `traceparent` from the ambient operation scope, and still cannot reject an unknown operation locally. An MCP caller can reach the workload through the edge or directly. The G1 cost changes accordingly: at G3, the risk is that the direct listener is still reachable, not that the edge never sees MCP.

Rejected — narrowing the route to the version-and-operation template, so that MCP bypasses the edge as G1 described. That would match the G1 text, but it breaks the workloads decided since. Adventures is a workload behind this edge (2026-10-17, #97), and its routes are `/api/*`, `/livez` and `/readyz`, not `/v<n>/<operation>`. SkyNet HR is the second hosted workload ([ADR-007](../../../docs/docs/adr/ADR-007-second-hosted-workload.md)), and it would need a template of its own. A per-workload route list in configuration was also rejected: it would be a second statement of each workload's surface, and G1 already rejected the contract-aware edge as G3's job.

Reversibility — cheap while the edge has no authorization of its own. At G3, narrowing would change which paths authorization can see. Supersedes: g1 2026-08-08 — "The edge forwards by prefix and does not consult the contract", for its route-template clause; g1 2026-08-08 — "The MCP projection is served by the workload process, over HTTP", for its statement that MCP bypasses the edge.
