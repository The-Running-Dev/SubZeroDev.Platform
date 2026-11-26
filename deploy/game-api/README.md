# game-api deployment (OpenTofu)

Platform's edge in `Terminate` mode in front of game-service, at `game-api.subzerodev.com`, through
`modules/app` of SubZeroDev.Infra. Two Portainer Git stacks:

- `game-api` (`compose.yaml`): the edge and game-service. Every deploy pins both images, built from one
  commit, and redeploys only this stack.
- `game-api-platform` (`compose.platform.yaml`): Postgres and the tunnel connector, which deploys never
  restart. It creates the `game-api-ingress` and `game-api-data` networks; `game-api-data` is internal.

This root also creates the tunnel route and DNS record for `game-api.subzerodev.com`, generates
`POSTGRES_PASSWORD` once (shared with the platform stack), and writes the repository variable
`DEPLOY_GAME_API_ENABLED`, which turns on the `deploy-game-api` job. The `production` environment and
`INFRA_DISPATCH_TOKEN` belong to `deploy/adventures-edge`: **apply that root first.**

Run everything through `./deploy/tofu.ps1 game-api <command>`.

## Not live yet

The issuer is the Keycloak `members` realm with the public `game-cli` client. That realm is not live, and the
G1 gates still stand in front of S14; nothing here passes them. `GameEdge:TrustedProxies` is unset, so
anonymous callers share one `AbuseLimits` budget behind the tunnel (see `deploy/adventures-edge/README.md`).

## Keys

Two kinds, neither ever in the repository:

- **The handoff key.** The edge signs a short-lived assertion for game-service with a P-256 private key;
  game-service verifies it with the public half. In SubZeroDev.Infra, run once:

  ```powershell
  ./tools/New-SigningKey.ps1 -App game-api
  ```

  It backs up `game-api-signing-key.pem` and `game-api-verification-key.pem` and sets
  `secrets.GAME_API_SIGNING_KEY_PEM_B64` in the var-file. Add the public half to `env` by hand, as
  `GAME_API_VERIFICATION_KEY_PEM_B64`: the base64 of the whole `game-api-verification-key.pem` file.
- **The issuer's signing keys.** `GAME_API_TOKEN_JWKS` is the realm's JWKS, pinned: the edge fetches nothing
  from the issuer. Rotating a realm key means adding it here and applying before the realm signs with it
  (`workloads/game-edge/README.md`, "The issuer, and what the edge fetches").

## The var-file

`$HOME\.subzerodev\game-api.tfvars.json` (or the path in `PLATFORM_GAME_API_TFVARS`):

```json
{
  "initial_images": {
    "game_edge": "ghcr.io/the-running-dev/platform-game-edge@sha256:<64 hex>",
    "game_service": "ghcr.io/the-running-dev/platform-game-service@sha256:<64 hex>"
  },
  "env": {
    "GAME_API_HANDOFF_ISSUER": "<the edge's assertion issuer>",
    "GAME_API_HANDOFF_AUDIENCE": "<game-service's audience>",
    "GAME_API_HANDOFF_KEY_ID": "<key id: letters, digits, dot, underscore or hyphen>",
    "GAME_API_TOKEN_ISSUER": "<the members realm issuer URL>",
    "GAME_API_TOKEN_JWKS": "<the realm's JWKS JSON, as one string>",
    "GAME_API_TOKEN_AUDIENCE": "<the audience game-cli tokens carry>",
    "GAME_API_VERIFICATION_KEY_PEM_B64": "<base64 of game-api-verification-key.pem>"
  },
  "secrets": {
    "GAME_API_SIGNING_KEY_PEM_B64": "<written by New-SigningKey.ps1>"
  }
}
```

`GAME_API_HANDOFF_LIFETIME` (default `00:00:30`) is optional in `env`. Both `initial_images` come from one
`main` run of `.github/workflows/container.yml` (the `edge` and `service` jobs' digests); they are used only
when the stacks are created. The plan refuses a missing value, a tag in place of a digest, and a key id that
could name a path.

Credentials are the same User-scope variables as `deploy/adventures-edge`, without `TF_VAR_dispatch_token`.

## First apply

1. `deploy/adventures-edge` is applied, and SubZeroDev.Infra's `modules/app` is at `v2.5.0`.
2. `./deploy/tofu.ps1 game-api plan`, then `apply`. The platform stack comes up first, then `game-api`.
   game-service migrates its schema on start; on a cold host it may restart until Postgres is ready.
3. `GET https://game-api.subzerodev.com/health/deployed` answers `200` with the commit of the initial
   images; then work through "Verifying a deployment" in `workloads/game-edge/README.md`. A second `plan`
   shows no changes.

State is in R2 at `apps/subzerodev-platform-game-api.tfstate`.

## Deploys

A push to `main` builds and pushes both images, then `deploy-game-api` starts the Infra deploy workflow with
both digests in one request. Infra checks each image's revision label, redeploys `game-api` with both and
waits until `/health/deployed` reports that commit. It needs the `images` input of ActionTemplates'
`infra-deploy` action, which is on its `main` but in no release yet; the workflow pins that commit.
