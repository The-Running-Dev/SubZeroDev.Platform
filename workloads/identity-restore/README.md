# identity-restore

Restoring Keycloak's database rolls back everything members did after the backup: a passkey they removed
works again, a used recovery code works again, a deleted account comes back, a disabled account is
enabled, and every session and refresh token issued since is valid again. Under R1-a
([decision log, 2026-11-21](../../design/90-decisions.md)) this workload keeps an off-host record of
Keycloak's security events and, after a restore, replays the removals onto the restored realm before
members are let back in.

It is not deployed anywhere. Nothing here authorises a production restore, a live configuration
change or an account migration. **G1 (the restore proof) is a production gate and is not waived by
R1-a**; its evidence is reported separately from these tests.

## What it does, and what it does not

| Rolled back by the restore | Repaired by | If the record has a gap over it |
|---|---|---|
| Sessions, refresh tokens, logouts | the session reset (realm `notBefore` = now, log everyone out) | still repaired: the reset does not depend on the record |
| A removed passkey or other credential | replaying the recorded `DELETE` / `REMOVE_CREDENTIAL` | **not repaired**; the gap blocks reopening |
| A used recovery code | replaying the recorded code sign-in: the whole code set is removed | **not repaired**; the gap blocks reopening |
| A deleted account | replaying the recorded deletion | **not repaired**; the gap blocks reopening |
| A disable, enable or temporary lockout | replaying the recorded change; a temporary lock becomes a disable plus a release marker | **not repaired**; the gap blocks reopening |
| Anything added after the backup (a new passkey, a new account, a role grant) | nothing: it is lost, and the plan lists it | lost |

The session reset covers session rollback only. It cannot repair an unrecorded credential removal, a
consumed recovery code or an account deletion. A window the recorder could not vouch for is a gap,
and a gap anywhere between the replay window's start and the old service's end blocks the restore.
What happens when a gap cannot be closed is an owner decision and is not settled here (see
*Open owner decisions*).

## Components

| Command | Credentials | What it does |
|---|---|---|
| `install-schema` | record database owner | creates the schema and grants the two roles: the recorder may only `INSERT` (column-level) and read its own windows; the restorer may read and write the restore log |
| `record [--once] [--interval-ms N]` | Keycloak recorder client (`view-events`, `view-realm`), record recorder role | one window per cycle: checks the realm's event settings, signs the canary in, reads events by time-range bisection, and writes the window as `covered` only if every check passed, else `gap` with the reasons |
| `backup --out FILE -- CMD…` | the database dump command's | runs the dump, writes `FILE` and `FILE.meta.json` (start, finish, sha256, bytes); refuses to write metadata if the dump failed |
| `coverage --dump FILE` | record restorer role | read-only: is the record complete for a restore of `FILE`? |
| `plan --dump FILE` | operator client, record restorer role | read-only: the replay intents and findings |
| `restore --dump FILE --restore-id ID [--reopen]` | operator client (master realm admin), record restorer role | the procedure below |
| `release-temp-locks` | operator client, record restorer role | lifts the disables that stand in for temporary lockouts that have ended |

Shared options: `--margin-ms N` (default 60000), `--service-ended-at T`, `--store-retired-at T` (epoch
ms or ISO), `--member-clients a,b`, `--allow-replay-lockouts`.
Environment and exit codes are listed in the header of [`src/cli.ts`](src/cli.ts). Exit 0 is success,
2 means blocking findings (the realm stays held), 3 means the restore was refused, and 1 is an error.

## The restore procedure (`restore`)

1. Refuse if this restore ID already reopened the realm, or if the replay window reaches back over an
   earlier restore that reopened.
2. Hold: disable the realm.
3. Check coverage from `backup start − margin` to the old service's end.
4. Plan, then apply the removals. Each one reads before it writes, so a rerun is all no-ops. Before
   it disables a temporarily locked member, it writes that member's release target (the account's
   state before the restore touched it) to the restore log as a `locking` entry. A rerun after an
   interruption takes the target from that entry, not from the account it has already disabled.
5. Session reset: realm `notBefore` = now, and log every session out.
6. Check that every member client signs in through fixA.
7. List members left with no passkey and no recovery codes.
8. Re-run step 4 without writing: every removal must now be a no-op.
9. Reopen only with `--reopen` and only if nothing above blocks.

A rerun with the same `--restore-id` continues a held restore. A rerun after it reopened is refused.

## Runbook

**Before any backup is taken that a restore might use**

- Run `record` continuously, against a record store on a different host from Keycloak and its
  database. A record kept on the Keycloak host is lost with it, which is why the spike's local file
  was rejected.
- Keep the realm's event settings as the recorder requires them: user events on with the required
  types listed explicitly, admin events on with representations, and both expiries at least the
  recorder's minimum. If a setting fails its check, or the canary sign-in is not found, the window is
  written as a gap and the restore stays blocked.
- Take backups only through `backup`, so that the dump's start and finish times are recorded beside it.

**Restore**

1. Close public ingress to Keycloak. Leave it closed until the procedure reopens the realm. The
   realm hold stops sign-in and refresh, but it does not stop requests that reach Keycloak.
2. Drain the old service, then note when it ended. That time is `--service-ended-at`.
3. Let the recorder complete one cycle past the end, then take the old Keycloak database out of
   service and note the time. That time is `--store-retired-at`. Windows polled after it are ignored,
   because the recorder could be reading from the restored database. Stop the recorder before step 4.
4. Restore the database and start Keycloak on it.
5. Run `coverage` and `plan`, and read both.
6. Run `restore` without `--reopen`, passing every client members sign in through in
   `--member-clients`. The member clients must be complete: a client left off the list is not
   checked for fixA (see *Open owner decisions*).
7. When it reports `held` with nothing blocking, rerun it with the same `--restore-id` and `--reopen`.
8. Reopen ingress. Run `release-temp-locks` on a schedule until it reports nothing pending.

**Do not**

- Change the realm's configuration, flows, clients or roles between the backup and the restore.
  An admin change of that kind is a blocking `configuration-changed` or `access-removed` finding,
  because the replay cannot reproduce it.
- Re-apply fixA to a restored realm by hand to clear a fixA finding. A backup taken before fixA was
  applied needs a decision (see below).
- Use `--allow-replay-lockouts` unless the owner has decided how those members are recovered.

## What G1 established about Keycloak 26.8.1

The fakes in `tests/fakes.ts` answer the way the real server did in G1. Where the workload depends on
Keycloak's behaviour, it was observed there, not assumed:

- The events API's `dateFrom` and `dateTo` are both inclusive, in milliseconds.
- An admin event names the acting client by its internal id (a UUID) in `authDetails.clientId`, not by
  its `clientId`. The restore resolves its own id (`Keycloak.identities`) so that it can recognise its
  own changes.
- An admin `UPDATE` of a user writes `previous_enabled` / `updated_enabled` only when `enabled` changed.
  An admin disabling an account that is already disabled leaves only `"enabled": false` in the
  representation. `release-temp-locks` reads both, and keeps an account disabled when it cannot tell.
- A temporary lockout's `not_before` is a UTC time with no zone. Brute-force state is not in the
  database, so a restore cannot bring a lock back; the replay holds the account disabled until the
  lock's end instead.
- A sign-in with a recovery code is a `LOGIN` with `credential_type=recovery-authn-codes`.
- While the realm is held, the recorder's canary sign-in is refused (`Realm not enabled`), so the
  recorder writes nothing during the hold.
- Admin-event retention is not in `/events/config`; that endpoint rejects an `adminEventsExpiration`
  field with 400. It is the realm attribute `adminEventsExpiration`, a string of seconds, which a
  client with `view-realm` reads from `GET /admin/realms/{realm}`. An empty, `0` or absent value means
  no expiry. This was checked after G1, on 2026-11-21, in a disposable 26.8.1. The recorder checks a
  window against both the user-event and the admin-event expiry. If the attribute is not a whole
  number of seconds, the window is a gap. Keycloak's actual deletion of expired admin events was not
  observed; the fakes model it.

## Residual exposures

These remain with R1-a as built. None of them has been accepted; each needs an owner decision before
production.

- Anything a member added after the backup is lost: a new passkey, a new account, an enrolment.
- One used recovery code removes the member's whole code set, not just that code. A code-set
  *creation* event inside the margin is also read as a use and drops the set (observed in G1 for the
  margin member `pia`).
- A member whose only credential was removed after the backup is left with nothing (`carol` in G1).
  Recovering them is D6, which is unresolved.
- A compromised recorder can insert false `covered` windows or false events. The schema stops it from
  updating or deleting rows, not from inserting them.
- The record trusts the clocks of the Keycloak host, the recorder and the record store. Skew wider
  than the settle delay can leave an event outside every window.
- Any event in a gap is not replayed. The procedure detects the gap and blocks; it does not recover
  the event.
- The PostgreSQL record store is the production solution only once its host, its backups and who can
  reach it are decided. This workload provisions one for tests only (`docker-compose.yml`).

## Open owner decisions

- What to do when a gap cannot be closed: keep the realm held, or accept named members' rollback.
- `--allow-replay-lockouts` and D6: how members left without credentials are recovered.
- The margin before the backup start (default 60 s).
- What to do with backups taken before fixA was applied.
- D2, D3 and D9, and the backup frequency, which sets the recovery point.
- The full list of member clients, including Adventures'.
- Whether configuration changes inside the margin are forbidden by policy or handled another way.

## Tests

```powershell
npm ci
```

```powershell
npm run typecheck
```

```powershell
npm test
```

The store's grant tests need a PostgreSQL server. Start the one in `docker-compose.yml` and set
`RECORD_TEST_DATABASE_URL=postgres://identity_record:identity_record@127.0.0.1:5433/identity_record`.
Without it they skip locally. Under CI they fail instead. The `identity-restore` job in
`.github/workflows/build.yml` runs them. G1 is not run in CI; it needs a disposable Keycloak.
