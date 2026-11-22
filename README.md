# PoRepoLineTracker

PoRepoLineTracker is a self-hosted GitHub repository analytics app built with Blazor WebAssembly and an ASP.NET Core API. It authenticates with GitHub, tracks user-owned repositories, clones and analyzes commit history, persists derived metrics in Azure Table Storage, and surfaces line-count trends, extension breakdowns, and contributor statistics.

**Live:** https://app-porepolinetracker2.azurewebsites.net (sign in with GitHub) · status page: [/diag](https://app-porepolinetracker2.azurewebsites.net/diag)

## Architecture overview

- Edge delivery: a Blazor WebAssembly client is served from the same App Service as the API.
- Compute tier: minimal APIs handle auth, settings, repository CRUD, GitHub lookups, and diagnostics; per-route handler classes and background tasks coordinate analysis, with live progress pushed over SignalR.
- Data tier: Azure Table Storage holds users, repositories, commit aggregates, and user preferences.
- External dependencies: GitHub provides OAuth identity, repository metadata, and clone/pull access; Azure Key Vault provides secrets; Application Insights collects telemetry (Jaeger via OTLP locally).

## Documentation

| Document | Purpose |
| --- | --- |
| [AGENTS.md](AGENTS.md) | The Po Web App Standard this repo follows, repo-specific notes and the owner's rules |
| [docs/spec.md](docs/spec.md) | What the app does and must keep doing: journeys, boundaries, edge cases, build/run/test |
| [docs/testing.md](docs/testing.md) | Test tiers and local execution commands |
| [SCREENSHOTS/](SCREENSHOTS/) | Before/after mockups of UI changes |

## Runtime summary

- Auth: GitHub OAuth (the only provider) issues an application cookie; user metadata and tokens are upserted to storage on sign-in.
- Repository management: repositories are added in bulk via `POST /api/repositories/bulk` — the single-add path was removed because it did not dedupe — then analyzed in the background by `AnalysisQueue` (two workers; queued work survives a restart; clone/pull network failures are retried; a revoked token shows as "Sign-in needed").
- Account: `DELETE /api/account` removes everything stored for the user and revokes the GitHub OAuth grant; Settings also offers the JSON/CSV export of `GET /api/repositories/export`.
- Hotspots: `GET /api/repositories/{id}/hotspots/{days}` ranks files by commits × code lines, read from the clone (`git log --numstat`).
- Insights: page `/insights` opens on the year (`GET /api/insights/year/{year}?offset=<minutes>`) and offers "All time" (`/insights/all`, `GET /api/insights/portfolio`) and "Code health" (`/insights/health`, `GET /api/code-health`).
- Push webhooks: after an analysis, the app registers a GitHub push hook on the repository (owner's token, `repo` scope, a per-repository HMAC secret) pointing at anonymous `POST /api/webhooks/github/{id}`. A push to the default branch queues an incremental analysis. On only when `WEBSITE_HOSTNAME` is set (App Service sets it; local runs have no hooks). Re-running the import connects repositories tracked earlier; deleting a repository removes its hook.
- One-click import skips forks and archived repositories and sends large accounts in chunks of 200.
- Public badges (opt-in per repository via `PUT /api/repositories/{id}/visibility`; there is no UI for it since the Share tab was removed): anonymous `GET /badge/{id}/lines.svg`, `/badge/{id}/health.svg` and the share page `/share/{id}`. Rate-limited per address; nothing is public until the owner switches it on.
- Analysis pipeline: the app clones or pulls repositories locally (no working-tree checkout), filters files by user-selected extensions and the vendored-code ignore rules, computes commit-level totals, and writes derived records back to Azure Table Storage.
- Presentation: the client reads repository history, extension percentages, contributor stats, and user preferences from the same API host.
- Operations: Serilog writes console and file logs in development; Application Insights receives cloud telemetry.

## Local development

### Prerequisites

- .NET 10 SDK
- Docker Desktop (Azurite + Jaeger via compose)

### Start dependencies

```powershell
docker compose up -d
```

### Run the app

```powershell
dotnet run --project src/PoRepoLineTracker.Api --launch-profile https
```

### Authenticating without GitHub (Development/Test only)

Tools and tests authenticate by sending an `X-Fake-User` header (a GUID verbatim; any other
string hashes to a stable GUID) — see
[src/PoRepoLineTracker.Api/api-tests.http](src/PoRepoLineTracker.Api/api-tests.http) for
worked examples including the antiforgery pair required by writes.

In a browser, Development also has a sign-in bypass: `/auth/login` signs you in as a fixed
"DevUser" when no GitHub OAuth app is configured, or when called with `?dev=true`.

## CI and deployment

[.github/workflows/deploy.yml](.github/workflows/deploy.yml) is the only workflow. Every push to `master`:
format check → Release build (warnings are errors) → Unit tier (151 tests) → Integration tier (40 tests,
Testcontainers Azurite on the runner's Docker) → publish → deploy to the Linux App Service
`app-porepolinetracker2` → wait until `/health/live` answers with the pushed commit in `X-Commit`. A red step stops
the deploy and the live site keeps its last good build. Nothing runs on pull requests.

Locally: the E2E tiers ([docs/testing.md](docs/testing.md)), `scripts/ci-checks.ps1` (compiles the Bicep) and, after
a deploy, `scripts/post-deploy-smoke.ps1 -BaseUrl https://app-porepolinetracker2.azurewebsites.net`.

Infrastructure is [infra/main.bicep](infra/main.bicep) + [infra/main.bicepparam](infra/main.bicepparam) (modules in
`infra/modules/`), applied by hand: `az deployment sub what-if -l eastus2 -f infra/main.bicep -p infra/main.bicepparam`
first. The topology is the combined API + WASM host on an F1 plan, backed by Azure Table Storage, with Key Vault and
Application Insights shared in `PoShared`. First-run machine setup: `scripts/setup.ps1`.

### GitHub sign-in (OAuth apps)

GitHub has no API for an OAuth app's settings, so these live only on github.com (Settings → Developer settings → OAuth Apps) and must match by hand:

| App | Client ID (Key Vault `kv-poshared2`) | Authorization callback URL |
| --- | --- | --- |
| Production `PoRepoLineTracker` | `PoRepoLineTracker--GitHub--ClientId` | `https://app-porepolinetracker2.azurewebsites.net/signin-github` |
| Development | `PoRepoLineTracker--GitHub--Dev--ClientId` | the localhost callback of your launch profile |

If the App Service is ever renamed or re-provisioned under another host name, update the production callback URL, or sign-in fails with GitHub's "redirect_uri is not associated with this application". (It broke on 2026-11-05 because the callback still named `app-porepolinetracker`, without the `2`.)
