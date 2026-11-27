# identity-provider deployment (OpenTofu)

Keycloak at `id.subzerodev.com` (decided 2026-11-27, `design/90-decisions.md`), as its own stack on the
VPS, through `modules/app` of SubZeroDev.Infra. The issuers are `https://id.subzerodev.com/realms/members`
and `https://id.subzerodev.com/realms/operators`, and the passkey relying-party ID is exactly
`id.subzerodev.com`. This root manages:

- the Portainer Git stack `identity-provider` (`compose.yaml` and `compose.tunnel.yaml`): Keycloak 26.8.1
  on its own PostgreSQL 17, a filtering proxy, and the tunnel connector. Nothing is published on the host;
- a Cloudflare tunnel and the DNS record for `id.subzerodev.com`, which reaches only the proxy;
- during an admin session only, a second hostname that reaches Keycloak itself (see "Admin sessions");
- the database password (`POSTGRES_PASSWORD`, in `modules/app`) and Keycloak's bootstrap admin password
  (`admin_password`), generated once and kept in the encrypted state.

Every image is third-party and pinned by digest in `compose.yaml`, so nothing deploys this stack: there is
no image variable, no deploy job and no GitHub setting. A new version is a change to `compose.yaml`, which
an `apply` sends to Portainer.

The realms are not configured here. `npm run cli -- configure` in `workloads/identity-provider` writes
them, from `realm/members.json` and `realm/operators.json`, during an admin session.

Run everything through `./deploy/tofu.ps1 identity-provider <command>`: it loads credentials from your
User-scope environment and the stack's values from a private var-file outside the repository.

## What id.subzerodev.com serves

The proxy passes on `/realms/members/`, `/realms/operators/` and `/resources/`, the paths Keycloak's
reverse-proxy guide says to expose, and answers `404` to everything else. So the `master` realm, the
admin console, the admin REST API, `/health` and `/metrics` are not public. It also refuses, before
matching, any path that names a parent directory, carries a path parameter (`;`), or encodes a dot, a
slash or a backslash, so no spelling of an admin path reaches Keycloak through an allowed prefix. The
query string is not filtered; Keycloak handles it.

Keycloak trusts `X-Forwarded-*` from the proxy's fixed address only (`10.213.81.10` on `front`). The
proxy sets the visitor's address from `CF-Connecting-IP`, so brute-force detection and the stored
events see the visitor, not the tunnel. The proxy keeps no access log.

## Admin sessions

Until the Cloudflare Access gate exists (SubZeroDev.Infra PR #6), the admin side is reached through a
tunnel route that exists only for the session:

1. Choose a label nobody can guess, such as `id-admin-` and 12 random lowercase letters and digits, and
   open the session:

   ```powershell
   ./deploy/tofu.ps1 identity-provider apply -var admin_route=<label>
   ```

   This adds a tunnel route and a DNS record. It does not restart the stack.
2. In `workloads/identity-provider`, set `KC_BASE_URL` to `https://<label>.subzerodev.com` and
   `KC_BOOTSTRAP_ADMIN_PASSWORD` from `./deploy/tofu.ps1 identity-provider output -raw admin_password`,
   with the production values below, and run `npm run cli -- configure`, `check`, `invite` or `reset`.
   Set them for that terminal only; never write them to a file in the repository.
3. Close the session by applying without the variable:

   ```powershell
   ./deploy/tofu.ps1 identity-provider apply
   ```

   The plan removes only the route and its DNS record. Check that `https://<label>.subzerodev.com` no
   longer answers.

While a session is open, that hostname reaches the `master` realm and the admin API from the internet,
behind the bootstrap admin's 48-character password. Keep sessions short.

The CLI uses the admin REST API, which works through the admin hostname. The admin console does not: its
pages use `id.subzerodev.com`, where the proxy refuses them.

The production values, which differ from the test defaults in `workloads/identity-provider` ("Run it"):

| Variable | Value |
|---|---|
| `IDP_WEBAUTHN_RP_ID` | `id.subzerodev.com` |
| `IDP_DASHBOARD_REDIRECT` | `https://dashboard.subzerodev.com/auth/callback` |
| `IDP_SMTP_HOST`, `IDP_SMTP_PORT`, `IDP_SMTP_SSL`, `IDP_SMTP_STARTTLS` | `smtp.resend.com`, `465`, `true`, `false` |
| `IDP_SMTP_AUTH`, `IDP_SMTP_USER` | `true`, `resend` |
| `IDP_SMTP_PASSWORD` | a Resend API key with sending access, restricted to `id-mail.subzerodev.com` |
| `IDP_SMTP_FROM` | an address at `id-mail.subzerodev.com`, Resend's sending domain for this provider |
| `IDP_GAME_AUDIENCE` | the value of `GAME_API_TOKEN_AUDIENCE` in `deploy/game-api`'s var-file |

`IDP_GAME_CLI_REDIRECT` keeps its loopback default. `IDP_ADVENTURES_AUDIENCE` and
`IDP_ADVENTURES_REDIRECT` are chosen when Adventures moves to this issuer; until then their test defaults
name no real host. The event expiries keep their test values while event retention is undecided.

Every `configure` writes the mail settings, so set the SMTP values on every run; `configure` refuses to
run without the password when the realm asks for SMTP authentication.

## The var-file

`$HOME\.subzerodev\identity-provider.tfvars.json` (or the path in `PLATFORM_IDENTITY_PROVIDER_TFVARS`)
must exist. This root has no required values, so it holds `{}`. `admin_route` is passed with `-var` for
a session, never stored here.

Credentials, from User-scope environment variables: `CLOUDFLARE_API_TOKEN`, `R2_ACCOUNT_ID`,
`R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `TF_VAR_state_passphrase`, `TF_VAR_portainer_api_key`,
`TF_VAR_cf_access_client_id` and `TF_VAR_cf_access_client_secret`. No dispatch token: this root writes
no GitHub settings. `GITHUB_TOKEN` falls back to `gh auth token`.

## First apply

The first apply is an operator step.

1. Check that nothing on the host already uses `10.213.81.0/28` (`docker network ls`, then
   `docker network inspect`). Docker refuses to create an overlapping network, so a clash fails the
   stack; it does not shadow anything. Choosing another subnet means changing the subnet, the range,
   the proxy's address and `--proxy-trusted-addresses` in `compose.yaml` together.
2. SubZeroDev.Infra's `modules/app` must be at `v2.5.0` (the `ref` in `main.tf`).
3. `./deploy/tofu.ps1 identity-provider plan`. Expect the stack, the tunnel and its configuration, the
   `id` DNS record and the two passwords to be created, and nothing in GitHub.
4. `./deploy/tofu.ps1 identity-provider apply`. Then
   `https://id.subzerodev.com/realms/master/.well-known/openid-configuration` answers `404`, and a second
   `plan` shows no changes.
5. Open an admin session and run `configure`, then `check`. After that,
   `https://id.subzerodev.com/realms/members/.well-known/openid-configuration` answers `200` with the
   issuer `https://id.subzerodev.com/realms/members`, and the same holds for `operators`. Close the
   session.

State is in R2 at `apps/subzerodev-platform-identity-provider.tfstate`, encrypted with
`TF_VAR_state_passphrase`.

The database is the named volume `identity-provider_postgres`. Removing the stack in Portainer does not
remove it.

## Open, and not settled here

- **Backups and R1.** `npm run cli -- backup` reads the database through `docker` on the machine it runs
  on (`IDP_COMPOSE_PROJECT`, default `identity-provider`), so it does not reach this stack from the
  operator's machine. Production backups, their retention and restore (R1) are not built.
- **The Access gate** (SubZeroDev.Infra PR #6) would replace the session route with a gated admin host.
- **The Resend domain** is ready. `id-mail.subzerodev.com` is verified in Resend (Ireland, sending
  only); its DNS records (DKIM, and the return path that carries SPF) are applied from SubZeroDev.Infra's
  `mail-dns` root (The-Running-Dev/SubZeroDev.Infra#14). The API key ("Platform identity SMTP", sending
  access, restricted to `id-mail.subzerodev.com`) is in `$HOME\.subzerodev\resend-id-mail-smtp.key` on
  the operator's machine; pass it as `IDP_SMTP_PASSWORD`.
- **Event retention** and **D3** (the brute-force numbers) are as in `workloads/identity-provider`.
