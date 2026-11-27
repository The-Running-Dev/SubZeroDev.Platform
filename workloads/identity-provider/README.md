# identity-provider

The identity provider for members and operators: Keycloak 26.8.1, run as a **workload** beside the edge and
game-service. It is not a Platform package (ADR-009). Platform validates the tokens it issues
through the generic bearer path; nothing in Platform knows it is Keycloak.

What this workload owns:

- `docker-compose.yml`: Keycloak (pinned by digest) on its own PostgreSQL 17, with a mail catcher
  (mailpit) that receives every message Keycloak sends. It is for local and CI use only, on HTTP at
  `http://localhost:8180`; the caught mail is at `http://localhost:8025`. The deployment at
  `id.subzerodev.com` is [`deploy/identity-provider`](../../deploy/identity-provider/README.md).
- `realm/members.json`: the member realm as code. It holds the realm settings, the signing keys
  (ES256 only), a username-only user profile, the passkey and recovery-code required actions, the
  browser flow, the stored-event settings, the session lifetimes, the mail server, and the clients.
- `realm/operators.json`: the operators realm as code (see *The operators realm*). It follows
  `members.json` in everything but its one client, `dashboard`.
- `src/cli.ts`: `configure` makes a Keycloak match the definitions, then checks them. `check`
  reports every difference that matters for sign-in safety. Both take every realm, or one with
  `--realm NAME`. `invite` and `reset` enrol a member and start one again (see *Enrolment and
  reset*). `jwks` prints the published key set for pinning at the edge. These three act on `members`
  unless `--realm operators` is given. `backup` dumps the database, which holds both realms.

## Sign-in

The browser flow is `members-browser`: a username, then either a passkey or a recovery code.

```text
0 auth-username-form REQUIRED
0 flow:members-browser-credential REQUIRED
1 webauthn-authenticator-passwordless ALTERNATIVE
1 auth-recovery-authn-code-form ALTERNATIVE
```

A username with no usable credential gets an error page. It never reaches a set-up page, so
knowing a username is not enough to enrol a credential on that account. This is the "fixA"
correction from the G1 recovery spike.

`check` fails if any of these hold:

- the flow has a different shape, or is not the realm's browser flow;
- any client in the realm overrides the browser flow;
- a client the definition does not name receives the game audience;
- a published key is anything other than EC P-256 ES256;
- a realm setting in the definition has another value: the session lifetimes, the passkey policy,
  brute-force detection, and the mail server (the mail password is never compared, because Keycloak
  never returns it);
- a required action is enabled that the definition does not list;
- the stored-event settings differ from the definition (see *Security events*);
- a service account holds roles other than the ones the definition grants it;
- the realm brokers any identity provider, such as GitHub.

## Enrolment and reset

Enrolment is by invitation only (decided 2026-11-27). The owner creates the account and Keycloak
emails it a link:

```powershell
npm run cli -- invite USERNAME --email ADDRESS
```

The address is admin-only: no member can see or change it, and no token carries it. The link opens
on "Perform the following action(s)", registers a passkey, then shows the recovery codes, and ends on
"Your account has been updated." The account holds no credential until the member follows the link,
and the link works once. It lasts Keycloak's default for links an administrator sends (12 hours); that
lifetime is not decided. `invite` refuses a username that exists. When the link cannot be sent, it
removes the account it made, so the invite can be repeated.

When a member has lost their passkeys and their recovery codes, the owner resets them:

```powershell
npm run cli -- reset USERNAME
```

`reset` removes every credential, ends every session, and revokes every client's grant, which ends
the offline sessions too. It then sends a new link to the address on the account. The account is
kept, so the member keeps their subject: the `sub` the edge and game-service know them by.

Neither command is reachable from sign-in. The browser flow still refuses a username with no
credential.

Keycloak sends the mail through the realm's mail server. In compose and CI that is the mail catcher.
In production it is Resend over SMTP, from a verified `subzerodev.com` domain; the Resend API key is
`IDP_SMTP_PASSWORD` and is never in the definition. `configure` refuses when the definition asks for
SMTP authentication and no password is set, because every run writes the mail settings, and a run
without the password would erase it.

## Account actions

A signed-in member manages their own credentials through Keycloak's application-initiated actions:
the client adds `kc_action=<action>` to an authorization request, and the action runs after the
member signs in. There is no SSO step in the flow, so every action needs a full sign-in first.

| `kc_action` | What it does |
|---|---|
| `webauthn-register-passwordless` | adds a passkey |
| `CONFIGURE_RECOVERY_AUTHN_CODES` | replaces the recovery-code set; the old codes stop working |
| `delete_credential:<credential id>` | asks the member to confirm, then deletes that credential |

These three are the only required actions the realm enables. Keycloak enables others by default,
and a signed-in member can start any enabled one: `UPDATE_PASSWORD` gives a passkey member a
password credential. `configure` disables every action the definition does not list, and `check`
fails on any that is enabled.

Sign-out is the OIDC logout endpoint with the refresh token, or token revocation. Either ends the
session at Keycloak, and its refresh token is then refused. Refresh tokens rotate: a reused one is
refused and ends the session. An access token already issued stays valid at the edge until it
expires, because the edge validates tokens locally against the pinned key set and does not ask
Keycloak. D9 (decided 2026-11-27) sets the access-token lifetime to 5 minutes.

The realm has three clients:

- `game-cli` is the native game client. It is public, uses PKCE S256, and its redirect is the
  loopback `http://127.0.0.1/callback`, which matches on any port. Its access tokens carry the game
  audience. They carry no email and no name.
- `adventures` is the Adventures site. It is confidential and also uses PKCE S256. Its tokens carry
  the adventures audience and never the game one, so the edge refuses them. Keycloak generates its
  secret, which is never in the definition; whoever deploys the site reads it through the admin API.
- `identity-recorder` reads the realm's stored events (see *Security events*). It is confidential,
  has only a service account, and signs no member in.

## Security events

The realm stores user events and admin events, with admin-event representations, for an explicit
list of types: sign-ins, code exchanges, refreshes, logouts, revocations, credential changes and
removals, account deletion, lockouts, and their errors. The list is the complete set: `check` fails
on a defined type that is not stored and on a stored type that is not defined. Both expiries are
30 days, a test-only value; retention is not decided. The admin-event expiry is a realm attribute,
`adminEventsExpiration`, because Keycloak's `/events/config` does not carry it.

These are the settings the identity-restore recorder (`workloads/identity-restore`) requires before it
calls a window of events complete. `identity-recorder` is the client it would run as. Its service
account holds `view-events` and `view-realm` and nothing else, and its token carries no scopes.
It can read events, admin events, the event settings and the realm. It is refused users,
credentials, clients and any change to the event settings.

Its own token request is a `CLIENT_LOGIN` event. The recorder uses that event to prove event
storage was on and had committed past the end of each window.

## Backup

```powershell
npm run cli -- backup --out D:\backups\members.dump
```

This runs `pg_dump --format=custom` in the compose `db` container and writes the dump. Beside it,
it writes `<dump>.meta.json` with `startedAtMs`, `finishedAtMs`, `sha256` and `bytes`. That sidecar is
the format the identity-restore workload's `verifiedMeta` reads. `startedAtMs` is read before the
dump starts, because a restore replays events from that time. It prints only the path, the size and
the hash.

The dump is a secret: it holds the realm's private signing keys, client secrets and every member's
credentials. So `backup`:

- refuses a path inside the repository;
- never overwrites a dump or a sidecar;
- deletes the partial file and writes no sidecar when the dump command fails or writes nothing;
- creates both files with mode 0600. Windows ignores that mode, so on Windows the directory's own
  permissions are the only protection.

`IDP_COMPOSE_PROJECT` names the compose project (default `identity-provider`). Any other dump
command goes after `--`: `backup --out FILE -- pg_dump ...`. A dump is not a restore plan; restoring
the member realm is R1, which is open.

## Run it

```powershell
$env:KC_BOOTSTRAP_ADMIN_PASSWORD = "<a test-only password>"
```

```powershell
docker compose -p identity-provider up -d --wait
```

```powershell
npm ci
```

```powershell
npm run cli -- configure
```

```powershell
npm test
```

```powershell
npm run cli -- jwks
```

These variables set the connection:

| Variable | Default |
|---|---|
| `KC_BASE_URL` | `http://localhost:8180` |
| `KC_BOOTSTRAP_ADMIN_USERNAME` | `bootstrap-admin` |
| `KC_BOOTSTRAP_ADMIN_PASSWORD` | required |

To override any `${NAME}` in `realm/members.json` or `realm/operators.json`, set `IDP_NAME`, for
example `IDP_GAME_AUDIENCE`. The realms share the names they have in common, so one setting covers
both. Every default in `values` is test-only, including these:

- `IDP_WEBAUTHN_RP_ID` is `localhost`. In production it is exactly `id.subzerodev.com`.
- `IDP_SMTP_HOST`, `IDP_SMTP_PORT`, `IDP_SMTP_FROM`, `IDP_SMTP_SSL`, `IDP_SMTP_STARTTLS`,
  `IDP_SMTP_AUTH`, `IDP_SMTP_USER` and `IDP_SMTP_PASSWORD` point at the compose mail catcher, with no
  authentication. In production they point at Resend.
- `IDP_DASHBOARD_REDIRECT` is `http://localhost:4400/auth/callback`. In production it is
  `https://dashboard.subzerodev.com/auth/callback`.

The session lifetimes are not placeholders. They are D9 (decided 2026-11-27): a 5-minute access
token, a 2-hour idle and 10-hour maximum SSO session, and a 30-day idle offline session with no
maximum. Only `game-cli` can ask for an offline session (`offline_access` is its optional scope).

`configure` is idempotent. It creates the realm when the realm is absent. When the realm exists,
`configure` reconciles its settings, user profile, required actions, clients, mappers and client
scopes. It never replaces signing keys. It never rebuilds a live browser flow in place: when the
flow has drifted, `configure` refuses.

## The operators realm

The people who run the platform sign in on a realm of their own, `operators` (decided 2026-11-27),
so no operator account is a member account and no member token is accepted by an operator tool. It
has the members realm's settings: invite-only enrolment, a passkey or a recovery code and nothing
else, the D9 access-token and SSO lifetimes, the same events and the same signing-key policy. Its
browser flow is `operators-browser`, of the same shape as `members-browser`. It brokers no identity
provider: there is no GitHub sign-in, and `check` fails on any.

Its one client is `dashboard`: confidential, standard flow only, PKCE S256, no offline session, and
no game audience. Keycloak generates its secret; the secret is never in the definition and is read
through the admin API by whoever deploys the Dashboard. The Dashboard moves to this issuer in the
Supabase-exit sequence's step 6; until then nothing signs in here.

```powershell
npm run cli -- invite USERNAME --email ADDRESS --realm operators
```

`reset USERNAME --realm operators` starts an operator again, as for a member.

## Tests

`tests/sign-in.test.ts` runs against the compose Keycloak after `configure`. It proves:

- a member signs in with a passkey through `game-cli` and receives an ES256 access token whose
  `kid` is in the JWKS, whose `iss` is the realm, and whose `aud` includes the game audience;
- a sign-in through `adventures` yields the adventures audience and not the game one, and its code
  is refused without the client secret;
- a recovery code signs the member in through "try another way", and the same code is refused a
  second time;
- a passkey the member does not hold is refused;
- a username with no credential, and an unknown username, get no token;
- a username with no credential is never shown a set-up page;
- `check` passes on the configured realm, and names a client that overrides the browser flow.

`tests/enrolment.test.ts` proves enrolment and the reset:

- an invited member follows the emailed link to a passkey and recovery codes, signs in, and the
  token's `sub` is the account's id;
- a link that has been followed registers nothing the second time;
- `invite` refuses an existing username, a malformed username and a malformed address;
- `reset` keeps the account and its id and removes both credentials. The member's refresh token,
  offline token, old passkey and old recovery codes are then refused, and the new link enrols the
  member again under the same `sub`;
- `reset` refuses a member that does not exist;
- `check` names a drifted SSO lifetime and a drifted mail host, and `configure` puts both back.

`tests/account.test.ts` proves the account actions:

- the realm enables only the three actions above, and `check` fails when `UPDATE_PASSWORD` is
  enabled;
- with configure applied, `kc_action=UPDATE_PASSWORD`, `CONFIGURE_TOTP` and `UPDATE_PROFILE` reach
  no page and add no credential;
- a second authorization in the same browser asks for the username again;
- a member adds a second passkey, and it signs in;
- answering the delete page with Decline keeps the passkey;
- a removed passkey is refused, and the remaining one still signs in;
- a member cannot delete another member's credential by naming its id;
- regenerated recovery codes replace the old set;
- logout and revocation end the session, and a reused refresh token is refused and ends the
  session.

`tests/events.test.ts` proves the security events:

- the realm stores user and admin events for the defined types, with representations, at the
  defined expiries;
- `check` fails when a defined type stops being stored, and when the admin-event expiry drifts;
- `identity-recorder` reads events, admin events, the event settings and the realm, and is refused
  users, credentials, clients and a change to the event settings;
- a passkey sign-in records `LOGIN` and `CODE_TO_TOKEN`, a credential deletion records
  `REMOVE_CREDENTIAL`, and enrolment records one `UPDATE_CREDENTIAL` per credential;
- the admin event for a created member carries its representation, and that representation holds no
  credential.

`tests/backup.test.ts` proves `backup`:

- a path in the repository is refused, an existing dump or sidecar is never overwritten, and a
  failed, empty or missing dump command leaves no file behind (these run without Keycloak);
- a dump of the compose database starts with `PGDMP` and matches its sidecar's hash and size;
- the dump restores, through `pg_restore`, into a scratch database that holds the member, the
  member's credentials and the realm's signing keys. The scratch database is dropped afterwards.

`tests/restore-boundary.test.ts` starts identity-restore's recorder and dump check in a child
process (`tests/support/restore-boundary.ts`), importing them from `workloads/identity-restore` at
run time; no code from that workload is copied here, and this workload's package does not depend
on it. It runs with the other suites, in CI too. It proves that, on this realm, a recorder window after its bootstrap is covered with its canary found
and holds the member's sign-in, and that `verifiedMeta` accepts a dump from `backup`. With
`REMOVE_CREDENTIAL` removed from the stored types, the same window is a gap. That was checked by
hand and is not a test.

`workloads/game-service/tests/keycloak-through-edge.test.ts` takes it further. A member's real
game-cli token crosses the edge into game-service, and the same member's adventures token is
refused at the edge. It gets its tokens from `tests/support/mint.ts`, a test-only command:

```powershell
node tests/support/mint.ts mint t2-example
```

```powershell
node tests/support/mint.ts remove t2-example
```

The tests use a software WebAuthn authenticator (`tests/support/authenticator.ts`). It proves the
server side of the ceremony. It says nothing about real browsers or real authenticators.

The suites enrol their synthetic members the way the owner does: `invite`, then the link, which they
read from the mail catcher's API (`IDP_MAIL_API`, default `http://localhost:8025`). No member ever
holds a password. A member with only one credential, which `invite` never makes, gets the same kind of
link asking for that one action.

`tests/operators.test.ts` proves the operators realm:

- `check` passes, and names a brokered GitHub provider and a drifted access-token lifetime, which
  `configure` puts back;
- an invited operator signs in to `dashboard` with a passkey. The ES256 token's `kid` is in the
  operators key set and not the members one, its `iss` is the operators issuer, its `sub` is the
  account's id, and it carries no game audience. The ID token carries no address;
- a recovery code signs the operator in once; a foreign passkey and a wrong client secret are refused;
- `dashboard` cannot ask for an offline session (`invalid_scope`);
- nothing crosses between the realms: neither holds the other's accounts or clients, a member cannot
  sign in to `dashboard`, and an operator's passkey does not sign in to `game-cli`.

Without `KC_BOOTSTRAP_ADMIN_PASSWORD` the suite skips locally. Under CI (`CI` set) it fails instead.

## Open, and not settled here

- **Decided on 2026-11-27, built but not applied** (`design/90-decisions.md`): the deployment at
  `id.subzerodev.com` is [`deploy/identity-provider`](../../deploy/identity-provider/README.md), whose
  first apply is an operator step. The realms here carry the other rulings: enrolment by invitation,
  the owner reset, the D9 lifetimes, the relying-party ID as a setting, and the `operators` realm.
  Nothing is applied to a live Keycloak, the Resend domain is not set up, and the Dashboard still
  signs in through Supabase.
- **The enrolment link's lifetime** is Keycloak's default (12 hours). It is not decided.
- **D3:** the brute-force numbers are test values.
- **R1 and D6:** production gates under G1. They do not block building and are not waived by it.
- **Deleting the last credential.** Keycloak lets a member delete their only credential. The member
  then has no way to sign in. `tests/account.test.ts` pins this as Keycloak's behaviour. It is
  recorded, not accepted. The owner reset (`reset`) is the way back; whether the deletion should be
  allowed is still open.
- **Event retention.** The 30-day expiries are test values. How long events and backups are kept is
  not decided.
- **Restoring the realm** is R1. `backup` writes a dump the restore can verify; nothing here
  restores the live realm, replays events, or shows complete-loss recovery.
- **Backup file modes on Windows.** Mode 0600 is not enforced there.
- An unknown username answers "Invalid username.", while a known username with no credential gets
  a generic error page. The two answers differ, so they reveal whether a username exists. This is
  Keycloak's behaviour and is recorded, not accepted.
