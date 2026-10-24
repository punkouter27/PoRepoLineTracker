# PoRepoLineTracker

PoRepoLineTracker is a self-hosted GitHub repository analytics app built with Blazor WebAssembly and an ASP.NET Core API. It authenticates with GitHub, tracks user-owned repositories, clones and analyzes commit history, persists derived metrics in Azure Table Storage, and surfaces line-count trends, extension breakdowns, and contributor statistics.

## Architecture overview

- Edge delivery: a Blazor WebAssembly client is served from the same App Service as the API.
- Compute tier: minimal APIs handle auth, settings, repository CRUD, GitHub lookups, and diagnostics; per-route handler classes and background tasks coordinate analysis, with live progress pushed over SignalR.
- Data tier: Azure Table Storage holds users, repositories, commit aggregates, and user preferences.
- External dependencies: GitHub provides OAuth identity, repository metadata, and clone/pull access; Azure Key Vault provides secrets; Application Insights collects telemetry (Jaeger via OTLP locally).

## Documentation

| Document | Purpose |
| --- | --- |
| [SPEC.md](SPEC.md) | What the app does and must keep doing: journeys, boundaries, edge cases, build/run/test |
| [AGENTS.md](AGENTS.md) | The writing ruleset for anyone (or anything) changing the code |
| [tests/README.md](tests/README.md) | Test scopes and local execution commands |

## Runtime summary

- Auth: GitHub OAuth (the only provider) issues an application cookie; user metadata and tokens are upserted to storage on sign-in.
- Repository management: repositories are added in bulk via `POST /api/repositories/bulk` — the single-add path was removed because it did not dedupe — then analyzed in the background by `AnalysisQueue` (two workers; queued work survives a restart; clone/pull network failures are retried; a revoked token shows as "Sign-in needed").
- Account: `DELETE /api/account` removes everything stored for the user and revokes the GitHub OAuth grant; Settings also offers the JSON/CSV export of `GET /api/repositories/export`.
- Hotspots: `GET /api/repositories/{id}/hotspots/{days}` ranks files by commits × code lines, read from the clone (`git log --numstat`).
- Year in Review: `GET /api/insights/year/{year}?offset=<minutes>` (page `/year-in-review`), with an optional Claude caption at `/api/insights/year/{year}/narrative`.
- Public badges (opt-in per repository via `PUT /api/repositories/{id}/visibility`): anonymous `GET /badge/{id}/lines.svg`, `/badge/{id}/health.svg` and the share page `/share/{id}`. Rate-limited per address; nothing is public until the owner switches it on.
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
dotnet run --project src/PoRepoLineTracker.API --launch-profile https
```

### Authenticating without GitHub (Development/Test only)

Tools and tests authenticate by sending an `X-Fake-User` header (a GUID verbatim; any other
string hashes to a stable GUID) — see
[src/PoRepoLineTracker.API/api-tests.http](src/PoRepoLineTracker.API/api-tests.http) for
worked examples including the antiforgery pair required by writes.

In a browser, Development also has a sign-in bypass: `/auth/login` signs you in as a fixed
"DevUser" when no GitHub OAuth app is configured, or when called with `?dev=true`.

### Optional: Claude features

The digest summary, the Year in Review caption and the "Ask" button on the repositories grid appear only when an Anthropic API
key is configured: `Anthropic:ApiKey` in `appsettings.Development.local.json`
locally, the Key Vault secret `PoRepoLineTracker--Anthropic--ApiKey` when deployed. Without it
nothing is sent anywhere and no assistant UI is rendered. See SPEC.md for exactly what is sent.

## CI

The pipeline's `build` job runs **`tests/PoRepoLineTracker.Unit` only**. Integration needs
Testcontainers/Azurite and the E2E tiers need a running app plus Playwright browsers; on a hosted
runner those fail for reasons unrelated to the change, so they run locally
(`docker compose up -d`, then the commands in [tests/README.md](tests/README.md)).

Unit tests passing is not enough before a push: composition-root changes (`Program.cs`,
`AddInfrastructure`, `AddAuth`, middleware order) only show up in the Integration tier. Run
Unit + Integration locally.

Tier sizes are kept small by convention, not by a gate: Unit ~100, Integration ~50, E2E ~25 each.
The `lint-infra` job compiles every `infra/*.bicep` on every run.

## Azure deployment

Deployment runs from GitHub Actions ([.github/workflows/deploy.yml](.github/workflows/deploy.yml)) on push to `master`: it lints and builds, deploys the app package to a Linux App Service, and can apply the Bicep in [infra/main.bicep](infra/main.bicep) / [infra/resources.bicep](infra/resources.bicep) via the manual `deploy_infra` input. The deployed topology is the combined API + WASM host backed by Azure Table Storage, Key Vault, and Application Insights.
