# adventures-edge deployment (OpenTofu)

Platform's edge in `PassThrough` mode in front of Adventures, as its own stack on the VPS, through
`modules/app` of SubZeroDev.Infra. This root manages:

- the Portainer Git stack `adventures-edge` (`compose.yaml` and `compose.tunnel.yaml`), on `proxy-net` beside
  Adventures, reaching it at `http://adventures-api:8787`. The stack also creates `adventures-edge-ingress`,
  the network Adventures' tunnel connector reaches the edge on (see "Trusted proxy");
- a Cloudflare tunnel and DNS record for the edge's **own** health hostname, `adventures-edge.subzerodev.com`,
  which the deploy checks. Players never use it;
- the repository variable `DEPLOY_ADVENTURES_EDGE_ENABLED`, which turns on the `deploy-adventures-edge` job;
- the repository's GitHub `production` environment and its `INFRA_DISPATCH_TOKEN`, which both deploy jobs use.
  This root owns them, so apply it before `deploy/game-api`.

It does **not** move players. They keep reaching Adventures at `adventures-api.subzerodev.com`, whose tunnel
route belongs to the Adventures stack. Pointing that route at `http://adventures-edge-ingress:8080` is the cutover,
made in the Adventures repository; pointing it back is the rollback (`workloads/game-edge/README.md`,
"Rolling back Adventures").

Run everything through `./deploy/tofu.ps1 adventures-edge <command>`: it loads credentials from your
User-scope environment and the stack's values from a private var-file outside the repository.

## Trusted proxy

The edge counts each anonymous caller against `GameEdge:AbuseLimits` by address. Behind the tunnel the
connection always comes from Adventures' `cloudflared`, so the edge trusts that one address
(`GameEdge:TrustedProxies`) and counts the visitor's address that Cloudflare appends to `X-Forwarded-For`.
Each player then has a budget of their own instead of all of them sharing one.

`TrustedProxies` takes exact addresses, and a container's address on `proxy-net` is not fixed, so the
connector reaches the edge on a network of their own:

- `adventures-edge-ingress`, created by this stack: subnet `10.213.80.0/28`, with dynamic addresses limited
  to `10.213.80.0/29`.
- Adventures' `compose.edge.yaml` attaches its `cloudflared` at `10.213.80.10`, the address the edge trusts.
  Nothing else joins the network, and Docker never assigns that address dynamically.
- Its route is `http://adventures-edge-ingress:8080`, an alias that resolves on this network only, so the
  connector always arrives from the trusted address. `adventures-edge` would also resolve on `proxy-net`.

Before the first apply, check that nothing on the host already uses `10.213.80.0/28` (`docker network ls`,
then `docker network inspect`). Docker refuses to create an overlapping network, so a clash fails the
deploy; it does not shadow anything. Choosing another subnet means changing the address here, in
`compose.yaml`, and in Adventures' `compose.edge.yaml`.

While Adventures' connector is attached, Compose cannot remove the network. To take this stack down, first
detach Adventures (its `deploy/README.md`, "Platform's edge").

## The var-file

`$HOME\.subzerodev\adventures-edge.tfvars.json` (or the path in `PLATFORM_ADVENTURES_EDGE_TFVARS`):

```json
{
  "initial_image": "ghcr.io/the-running-dev/platform-game-edge@sha256:<64 hex>"
}
```

`health_route` defaults to `adventures-edge` (the 2026-11-26 ruling); set it here only to override that.

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
3. `./deploy/tofu.ps1 adventures-edge apply`. Then `GET https://adventures-edge.subzerodev.com/health/deployed` answers
   `200` with the commit of `initial_image`, `docker network inspect adventures-edge-ingress` shows the
   subnet above, and a second `plan` shows no changes.

State is in R2 at `apps/subzerodev-platform-adventures-edge.tfstate`, encrypted with `TF_VAR_state_passphrase`.

## Deploys

A push to `main` builds and pushes the edge image, then `deploy-adventures-edge` starts the Infra deploy
workflow with its digest. Infra checks the image's revision label, redeploys the stack and waits until
`/health/deployed` reports that commit, which it does only once the edge reaches Adventures' `/readyz`.
