# identity-provider

The member identity provider: Keycloak 26.8.1, run as a **workload** beside the edge and
game-service. It is not a Platform package (ADR-009). Platform validates the tokens it issues
through the generic bearer path; nothing in Platform knows it is Keycloak.

What this workload owns:

- `docker-compose.yml`: Keycloak (pinned by digest) on its own PostgreSQL 17, for local and CI use
  only. It runs on HTTP at `http://localhost:8180`. A production deployment is a separate approval.
- `realm/members.json`: the member realm as code. It holds the realm settings, the signing keys
  (ES256 only), a username-only user profile, the passkey and recovery-code required actions, the
  browser flow, and the clients.
- `src/cli.ts`: `configure` makes a Keycloak match the definition, then checks it. `check`
  reports every difference that matters for sign-in safety. `jwks` prints the published key set
  for pinning at the edge.

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
- a required action is enabled that the definition does not list.

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
Keycloak. The access-token lifetime is D9, which is open.

The realm has two clients:

- `game-cli` is the native game client. It is public, uses PKCE S256, and its redirect is the
  loopback `http://127.0.0.1/callback`, which matches on any port. Its access tokens carry the game
  audience. They carry no email and no name.
- `adventures` is the Adventures site. It is confidential and also uses PKCE S256. Its tokens carry
  the adventures audience and never the game one, so the edge refuses them. Keycloak generates its
  secret, which is never in the definition; whoever deploys the site reads it through the admin API.

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

To override any `${NAME}` in `realm/members.json`, set `IDP_NAME`, for example
`IDP_GAME_AUDIENCE`. Every default in `values` is test-only.

`configure` is idempotent. It creates the realm when the realm is absent. When the realm exists,
`configure` reconciles its settings, user profile, required actions, clients, mappers and client
scopes. It never replaces signing keys. It never rebuilds a live browser flow in place: when the
flow has drifted, `configure` refuses.

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
- `check` reports the test-only bootstrap client while that client exists, and passes once it is
  removed.

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

**Test-only enrolment.** How a member gets a first credential is an open decision (D3/D5), so the
realm has no enrolment path of its own. The tests enrol synthetic members through
`test-only-enrol`, which works like this:

1. The client uses Keycloak's built-in password flow.
2. The member is created with a throwaway password and the two required actions.
3. The member completes the required actions.
4. The password is deleted.

`check` flags this client, and the suite removes it. It is not an enrolment design.

Without `KC_BOOTSTRAP_ADMIN_PASSWORD` the suite skips locally. Under CI (`CI` set) it fails instead.

## Open, and not settled here

- **D9:** token lifetimes. The 300 s access token is a test value.
- **D3/D5:** the brute-force numbers and first-credential enrolment.
- **R1, D6 and OD-16:** production gates under G1. They do not block building and are not waived by
  it.
- **Deleting the last credential.** Keycloak lets a member delete their only credential. The member
  then has no way to sign in. `tests/account.test.ts` pins this as Keycloak's behaviour. It is
  recorded, not accepted; it belongs to the open recovery decisions (D3/D5).
- An unknown username answers "Invalid username.", while a known username with no credential gets
  a generic error page. The two answers differ, so they reveal whether a username exists. This is
  Keycloak's behaviour and is recorded, not accepted.
