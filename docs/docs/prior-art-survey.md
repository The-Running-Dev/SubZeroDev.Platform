---
sidebar_position: 6
sidebar_label: Prior Art Survey
---

# Prior Art — What Was Built Before, and What It Proves

**Document status:** Survey. It records measurements and names open questions. It decides nothing.

**Reading order:** before [`minimal-platform-packages.md`](minimal-platform-packages.md), because
this is the evidence those boundaries were drawn from. That document argues the six packages are the
set that is genuinely hard to retrofit; this one shows what happened when they were retrofitted
anyway, six times.

> **Scope of this document**
>
> A census of the .NET work already on this machine, what the repetition in it demonstrates, what is
> deliberately not being imported and why, and two gaps the census exposed that no other document
> here currently states. Every figure names the measurement that produced it.
>
> **This is not a decision record.** Where the survey reaches a fork it presents both readings and
> stops. §7 lists what stays open and which command owns each one.

---

## 1. What Was Scanned

Every `.csproj` under `D:\Projects`, excluding `bin`, `obj`, `node_modules` and `packages`:

```powershell
Get-ChildItem -Path D:\Projects -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|packages)\\' }
```

| Measure | Value |
|---|---|
| Project files | **661** |
| Top-level directories containing at least one | **41** |
| Largest single target framework | **`netfx v4.6.2`**, 94 projects |
| Explicit `net10.0` declarations | **11** |

**The `net10.0` figure is the one worth pausing on.** Ten of the eleven are in this repository —
five projects, each counted twice because a working tree sits inside the checkout. The eleventh is
`SubZeroDev.WinGet\build\_build.csproj`, a NUKE build script inside a fork of `winget-cli`. The
remaining Platform projects inherit the framework from `Directory.Build.props` rather than declaring
it, so they do not appear in that count.

### Provenance changes what the corpus means

**341 of the 661 projects are other people's code** — forks and working checkouts, not work authored
here:

| Directory | Projects | What it is |
|---|---|---|
| `ensembleVideo` | 119 | A third-party product checkout |
| `SubZeroDev.WinGet` | 81 | A fork of `winget-cli` |
| `Google-GData` | 47 | Google's .NET client libraries, last touched 2013 |
| `UniGetUI` | 41 | `marticliment/UniGetUI` |
| `ShishaWorld` | 21 | ASP.NET Boilerplate scaffolding |
| `WebApi-OAuth2-StarterKit`, `Testing.Toolkit` | 13 | Two `magonzalez/*` starter kits |
| `WinGet-Create`, `WinGet-Create-Orig`, `WPF-NotifyIcon`, `GlobalWeather`, `resourcelib`, others | 19 | Forks and samples |

That leaves **320 projects of work authored here**, and they fall into two eras with almost nothing
between them: a body of work from 2015 to 2021, and a much smaller one from 2024 onward. The gap is
where this repository's design stage sits.

---

## 2. The Same Framework, Six Times

Six directories carry **three or more** of the same project names — `Framework`, `Bootstrapper`,
`Configuration`, `Repository`, `Data.Repository`, `Mocks`. Three is the threshold the count below
was taken at; nothing else in the corpus reaches it.

| Directory | Projects | Last commit | Relationship |
|---|---|---|---|
| `Template` | 13 | 2019 | `origin` is the **CodeCookbook** repository |
| `CodeCookbook` | 37 | 2020 | Carries a `CodeCookbook.old/` tree of its own predecessor |
| `BlueLionheart` | 18 | 2020 | |
| `WebFramework` | 13 | 2020 | A de-branded fork of `BlueLionheart` |
| `Starter` | 88 | — | The same architecture staged seven times, `Stage1` … `Stage7` |
| `@Demos` | 49 | — | Carries `Framework`, `Bootstrapper` and `Repository` among unrelated demos |

The fork relationships are measured, not inferred.

**`Template` is CodeCookbook.** Its `origin` remote is literally
`SubZeroDevelopment/CodeCookbook/CodeCookbook`, and it also carries a `CodeCookbook.sln` beside its
own.

**`WebFramework` is BlueLionheart.** Excluding `bin`, `obj`, `.git`, `node_modules` and `wwwroot`,
`WebFramework` holds 558 files and **553 of them share an identical relative path with
`BlueLionheart`** — the same `Data.Digi`, the same `Data.OpenWeatherMap`, the same four documents
under `Docs/`. The rename never reached the deployment artifact:
`WebFramework/Deploy/docker-compose.yml` still names its service `bluelionheart.web`, still routes
`` Host(`dev.bluelionheart.com`) ``, and still sets an `@bluelionheart.com` address as the ACME
contact.

**`Starter` is the same thing seven times deliberately** — each stage adds one capability
(`MessageBus.RabbitMQ` at Stage 3, `MessageBroker.Azure` at Stage 4) and re-copies everything
beneath it.

### What this measures

Not carelessness — the opposite. Each of these was a reasonable attempt to keep the good parts of
the last one. What it measures is the **cost of having no extraction point**: with nowhere to put
the shared shape, the only way to reuse it was to copy the whole repository and rename it, and the
rename is the part that never finished.

That is the case for this repository stated as evidence rather than as argument. The counterweight —
that a framework designed from zero consumers encodes guesses — is the extraction guard in
[`minimal-platform-packages.md`](minimal-platform-packages.md) §1, and is not restated here.

---

## 3. What Is Deliberately Not Imported

The question "should any of this come across?" has a specific answer, recorded so it is answered
once. **No.** Not because the code is old, but because of what it does. Each finding below was read
in the tree:

| Where | What |
|---|---|
| `WebFramework/Bootstrapper/Setup.cs:81`, `:178` | `services.BuildServiceProvider()` is called twice during bootstrap; the second result is assigned to a static `Ioc.Instance` service locator |
| `WebFramework/Bootstrapper/Setup.cs:72` | Database migrations are run inside DI registration |
| `WebFramework/Bootstrapper/Setup.cs:75–81` | Configuration is re-read from the database mid-bootstrap, through that locator, to override what was just bound |
| `WebFramework/Framework/Extensions/StringExtensions.cs:165`, `:180` | `ToInt` and `ToLong` return `0` when parsing fails — a bad value and a legitimate zero are indistinguishable |
| `WebFramework/Web/Dockerfile` | An unpinned script is fetched from `raw.githubusercontent.com` at image-build time, so the image is not reproducible and its content is not controlled here |
| `WebFramework/Deploy/docker-compose.yml` | Traefik runs with `--api.insecure=true`, with the Docker socket mounted into it, alongside a database password passed by environment variable |

Set that against what
[`src/SubZeroDev.Platform.Hosting/PlatformHostExtensions.cs`](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/src/SubZeroDev.Platform.Hosting/PlatformHostExtensions.cs)
already does with one call, and against Core's done-criterion in
[`minimal-platform-packages.md`](minimal-platform-packages.md) §2 — a module graph that is missing or
cyclic must fail **at startup with a named error**, which is the precise failure mode a static
locator resolved mid-bootstrap cannot have.

**The extension libraries are the tempting exception, and they should be resisted too.** `IsEmpty`
is `string.IsNullOrWhiteSpace`, `FromJson` is Newtonsoft where `System.Text.Json` now sits in the
shared framework, and `Format` is a reflection-driven mini-templater that string interpolation
replaced. Importing them would take a dependency on `Newtonsoft.Json` to obtain methods the base
class library already has.

**What is worth carrying forward is the shape, not the code**: a bootstrapper, typed configuration,
a persistence seam, and a test double set. Those are four of the six packages, and they are already
built.

---

## 4. The Deployment Gap

[Platform Identity](platform-identity.md) calls this repository the reusable application framework
**and hosting layer**. Measured against the tree, that second half has no artifact behind it.

**The only `Dockerfile` in this repository builds the documentation site.** There is no image for
`samples/`, none for `workloads/game-service`, no Compose file, and no workflow that builds or
publishes one. `.github/workflows/build.yml` restores, builds, tests and runs the sample in both
roles; `.github/workflows/release.yml` packs, publishes, restores and re-verifies against the
published packages. Both are thorough, and neither produces a runnable deployment artifact.

That is not automatically wrong — a framework may legitimately ship packages and leave deployment to
its consumers. It is worth stating because nothing currently says which of those two it is, while
[ADR-004](adr/ADR-004-framework-build-not-adopt.md) names local execution, homelab and single-server
self-hosting as in-scope deployment modes, and a mode with no artifact is a mode nobody has tried.

### The prior art has already converged

Three generations, and the third is good enough to be the reference whichever way the question above
resolves.

**2020 — the .NET lineage.** Traefik terminating TLS with an ACME resolver, a `wait-for` shim
sequencing the application behind its database, and **one Compose file that both builds and
deploys**. Superseded on every count: the wait-for shim is what `depends_on` with
`condition: service_healthy` now expresses natively, and the single file is the failure the next
generation named explicitly.

**2026 — `SubZeroDev.Adventures`.** The current answer, and the design notes in it are the valuable
part:

- **Two standalone Compose files rather than an override layer.** The deployment file pulls a
  published image and never builds; the development file builds from source under a deliberately
  different image name, so `up --build` cannot tag something as the deployable image. Both are read
  top to bottom on their own — the file it replaced needed `build: !reset null` to un-declare things
  it had inherited, which meant neither file described a whole stack.
- **The reverse-proxy network is declared `external: true`**, so a missing network fails fast
  instead of silently creating an isolated one the proxy is not attached to.
- **Migrations are a one-shot service**, gated behind `service_completed_successfully`, with the
  restart policy explicitly overridden so a clean exit is not looped forever.
- **`pull_policy: always`** against a moving tag, because otherwise a redeploy reuses whatever layer
  cache the host happens to hold and silently runs the previous build.
- **The entrypoint has a verb vocabulary** — `serve` and `migrate` — so neither Compose file knows
  which build target it got.
- Non-root runtime user, OCI revision and source labels, and the database on an internal network
  that publishes no port.

**2026 — `SubZeroDev.PSGenerator`** covers publishing: pull requests build and smoke-test the image
without pushing, the smoke test exercises the image from the outside as a consumer would rather than
trusting the build, and it publishes an immutable dated tag alongside a moving one.

### The open question

**Does Platform own a deployment contract, or does each product?** Both readings are defensible and
this document takes neither.

| Reading | What it buys | What it costs |
|---|---|---|
| Platform owns it | The hosting-layer claim gains an artifact; every product inherits one deployment shape; the self-hosting modes become testable rather than asserted | A second public surface to keep compatible, and an opinion pushed onto products through infrastructure — the coupling the Core/Hosting boundary test exists to prevent |
| Each product owns it | Platform stays a set of packages with no operational opinion; products differ where they genuinely differ | The convergence above gets rediscovered per product, which is §2's finding repeating in a new medium |

---

## 5. Aspire

`Aspire` appears on the candidate package list in
[`platform-specification.md`](platform-specification.md) §"Package structure". Nothing anywhere says
what that candidate would contain, and the survey found a reason to ask now rather than later.

**The overlap is exact.** `src/SubZeroDev.Platform.Observability` declares
`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, and the
`AspNetCore`, `Http` and `Runtime` instrumentation packages. **Aspire's `ServiceDefaults` template
declares the same five package identifiers** — the only difference in the copy on this machine is
the version. Platform adds Serilog and its redaction pipeline on top; the Aspire template adds
`Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.ServiceDiscovery`.

The corpus's only Aspire use is `MenuApp`, a `net9.0` project with no remote — a scratch project, not
evidence of a working practice. So this is a question the survey raises, not one it can answer from
experience.

[ADR-004](adr/ADR-004-framework-build-not-adopt.md) §4 requires exactly this check — a gap evaluated
against existing packages before anything is written, with the reason recorded either way — and it
has never been run for Aspire. Two things make it non-obvious in both directions: Aspire's
orchestration model is a development-time concern that ADR-004's framework rejection does not
straightforwardly cover, and `ServiceDiscovery` is a capability Platform has not built and has not
declined.

**Running that evaluation is an ADR's work.** This section exists to make sure it is run rather than
inherited by default.

---

## 6. Homelab, as a Concrete Target

"Self-hosted" and "homelab" appear as constraints in
[ADR-004](adr/ADR-004-framework-build-not-adopt.md),
[`observability.md`](observability.md) and
[`events-and-notifications.md`](events-and-notifications.md) — each correctly, and each
abstractly. The survey found the concrete thing those words point at.

`NAS` holds **84 Compose and YAML files**; `Docker-HomeLab` holds a further stack alongside a
`docker-updater.ps1` invoked from cron every five minutes. Between them they run a reverse proxy,
container management, media services, monitoring and uptime checking on a single machine.

Three properties of that environment bear directly on decisions already recorded here, and are worth
having written down where a future decision can cite them:

- **There is no OTLP collector.** [`observability.md`](observability.md)'s opt-in export, with
  console and file as the defaults, is correct for this target rather than merely cautious.
- **There is no message broker**, which is the environment behind
  [`minimal-platform-packages.md`](minimal-platform-packages.md) §3a's finding that adopting a
  broker-only outbox library would put a broker into local development to serve a transport decision
  not yet taken.
- **Deployment is `pull` and `up -d` against a moving tag**, by an operator who is also the author.
  That is the constraint §4's Compose findings were shaped by.

Whether this becomes a **stated** deployment mode with its own criteria, rather than an adjective, is
open — see below.

---

## 7. What This Document Does Not Decide

Stated explicitly, because a survey that reaches a conclusion by implication is worse than one that
reaches none.

| Open | Owned by |
|---|---|
| Whether Platform owns a deployment contract (§4) | A design cycle, or an ADR if it affects a published contract |
| Whether Aspire is adopted, declined, or scoped as a candidate package (§5) | An ADR — the evaluation [ADR-004](adr/ADR-004-framework-build-not-adopt.md) §4 requires |
| Whether homelab becomes a stated deployment mode with criteria (§6) | A design cycle |

None of the three is urgent, and none is blocked. What this survey changes is only that each is now
a question with evidence attached rather than an assumption nobody has looked at.
