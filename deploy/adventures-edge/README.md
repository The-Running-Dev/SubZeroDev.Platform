# adventures-edge deployment (OpenTofu)

Platform's edge in `PassThrough` mode in front of Adventures, as its own stack on the VPS, through
`modules/app` of SubZeroDev.Infra. This root manages:

- the Portainer Git stack `adventures-edge` (`compose.yaml` and `compose.tunnel.yaml`), on `proxy-net` beside
  Adventures, reaching it at `http://adventures-api:8787`;
- a Cloudflare tunnel and DNS record for the edge's **own** health hostname, which the deploy checks;
- the repository variable `DEPLOY_ADVENTURES_EDGE_ENABLED`, which turns on the `deploy-adventures-edge` job;
- the repository's GitHub `production` environment and its `INFRA_DISPATCH_TOKEN`, which both deploy jobs use.
  This root owns them, so apply it before `deploy/game-api`.

It does **not** move players. They keep reaching Adventures at `adventures-api.subzerodev.com`, whose tunnel
route belongs to the Adventures stack. Pointing that route at `http://adventures-edge:8080` is the cutover,
made in the Adventures repository; pointing it back is the rollback (`workloads/game-edge/README.md`,
"Rolling back Adventures").

Run everything through `./deploy/tofu.ps1 adventures-edge <command>`: it loads credentials from your
User-scope environment and the stack's values from a private var-file outside the repository.

## Not decided

- **The health hostname.** `health_route` has no default; the label you set becomes
  `<label>.subzerodev.com`. Proposed: `adventures-edge`.
- **`GameEdge:TrustedProxies`.** It is unset in `compose.yaml`, so the edge trusts nobody's forwarded
  address. Behind the tunnel every anonymous caller then arrives from the connector's address, and all of them
  share one `GameEdge:AbuseLimits` budget (60 requests a minute by default). That has to be settled before the
  cutover sends players through this edge.

## The var-file

`$HOME\.subzerodev\adventures-edge.tfvars.json` (or the path in `PLATFORM_ADVENTURES_EDGE_TFVARS`):

```json
{
  "health_route": "<label under subzerodev.com>",
  "initial_image": "ghcr.io/the-running-dev/platform-game-edge@sha256:<64 hex>"
}
```

`initial_image` is used only when the stack is created; deploys own the image afterwards. Take it from a
`main` run of `.github/workflows/container.yml` (the `edge` job's digest). Until the switch exists, that run
builds and pushes the images and skips both deploy jobs.

Credentials, from User-scope environment variables: `CLOUDFLARE_API_TOKEN`, `R2_ACCOUNT_ID`,
`R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `TF_VAR_state_passphrase`, `TF_VAR_portainer_api_key`,
`TF_VAR_cf_access_client_id`, `TF_VAR_cf_access_client_secret`, and `TF_VAR_dispatch_token` (a token with
Actions write on SubZeroDev.Infra only). `GITHUB_TOKEN` falls back to `gh auth token`.

## First apply

1. SubZeroDev.Infra's `modules/app` must be at `v2.5.0` (the `ref` in `main.tf`); `tofu init` fails until
   that tag exists.
2. `./deploy/tofu.ps1 adventures-edge plan`. Expect the stack, the tunnel and its DNS record, the
   `production` environment, `INFRA_DISPATCH_TOKEN` and `DEPLOY_ADVENTURES_EDGE_ENABLED` to be created.
3. `./deploy/tofu.ps1 adventures-edge apply`. Then `GET https://<health hostname>/health/deployed` answers
   `200` with the commit of `initial_image`, and a second `plan` shows no changes.

State is in R2 at `apps/subzerodev-platform-adventures-edge.tfstate`, encrypted with `TF_VAR_state_passphrase`.

## Deploys

A push to `main` builds and pushes the edge image, then `deploy-adventures-edge` starts the Infra deploy
workflow with its digest. Infra checks the image's revision label, redeploys the stack and waits until
`/health/deployed` reports that commit, which it does only once the edge reaches Adventures' `/readyz`.
