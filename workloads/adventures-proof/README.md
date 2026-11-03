# Adventures through the edge (G3 S7)

A test-only project. It starts the Adventures server at a pinned commit, puts the real game edge in
`PassThrough` mode in front of it, and proves from a browser's side that the edge carries what
Adventures owns — its guest and signed-in sessions, its cookies, its redirects and its cross-origin
policy — without taking part in any of it. It ships nothing and nothing depends on it.

```
browser (cookie jar) → game edge (dotnet, PassThrough) → recording proxy → Adventures
                                  test OIDC issuer (HTTPS) ← reached directly by the browser and Adventures
```

The recording proxy sits between the edge and Adventures so a test can say a response was
Adventures' own: it is on the wire before the edge sees it.

## Run it

Needs Node 24, the .NET 10 SDK, `git`, and a PostgreSQL server. The proof creates and drops its own
database per run, so the server only has to accept the credentials below.

```bash
docker compose up -d                      # in workloads/game-service
dotnet build workloads/game-edge/SubZeroDev.Platform.GameEdge/SubZeroDev.Platform.GameEdge.csproj
cd workloads/adventures-proof
npm install
npm run prepare-adventures                # clone at the pin, install its dependencies
npm test
```

| Variable | Default | Meaning |
|---|---|---|
| `GAME_EDGE_DLL` | `../game-edge/…/bin/<configuration>/net10.0/SubZeroDev.Platform.GameEdge.dll` | The built edge to run. |
| `GAME_EDGE_CONFIGURATION` | `Debug` | Picks the default dll's build configuration. |
| `ADVENTURES_DIR` | `.adventures/` here | Where the pinned checkout lives. |
| `ADVENTURES_PROOF_DATABASE_URL` | `GAME_SERVICE_TEST_DATABASE_URL`, else the `game-service` compose database | A role that may `create database`. |

## The pin

`adventures-pin.json` names the Adventures repository and one commit. `prepare-adventures` clones
that commit, initialises its submodules, installs its dependencies and writes a marker for the
commit; the tests refuse to run without it. To move to a newer Adventures, change the commit,
run `npm run prepare-adventures` and `npm test`, and commit the new pin with whatever the proof
needed to change. Adventures itself is never modified here.

## Why the issuer is HTTPS

`openid-client` refuses a plain-HTTP issuer, so the test issuer serves HTTPS with a certificate made
per run, and Adventures' own process is given it through `NODE_EXTRA_CA_CERTS`. No exemption for
HTTP issuers is introduced anywhere.
