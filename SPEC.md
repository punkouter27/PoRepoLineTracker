# PoRepoLineTracker — SPEC.md

Durable reference for what this app is, what it does, what it won't do, and what must remain true at every commit.
The cross-agent *how* (YAGNI ladder, validation/security/a11y invariants) lives in `AGENTS.md` and `.github/copilot-instructions.md`. This document is the *why* and the *contract*.

---

## 1. Objective

PoRepoLineTracker is a self-hosted **GitHub repository analytics** app. A user signs in with GitHub, selects repositories they own, and the app clones each one, analyses its commit history, persists derived metrics, and surfaces line-count trends, extension breakdowns, contributor statistics, and a portfolio digest. The deployment model is one binary: the ASP.NET Core API hosts the Blazor WebAssembly client itself, talks to Azure Table Storage for state and to Azure Key Vault for secrets, and emits telemetry to Application Insights (Jaeger via OTLP locally). External surface is **GitHub + Azure** only — no SaaS analytics, no AI/ML, no alternative identity providers.

## 2. User Journeys

The single primary journey optimises every page:

1. **Sign in** — `/auth/login` → GitHub OAuth → application cookie. In Dev/Test, tools authenticate by sending `X-Fake-User`. Development also has a browser bypass: `/auth/login` signs in a fixed "DevUser" when no OAuth app is configured or with `?dev=true`.
2. **Bulk-add owned repositories** — `/api/repositories/bulk` is the only write path. Single-add was removed because it did not dedupe.
3. **Watch analysis** — background clone/pull + line counting pushes progress over `/hubs/analysis` (SignalR); a fallback poll synthesises the same frames when the hub is unreachable. UI shows live tallies (`LinesCounted`, throughput, ETA), never a percentage alone.
4. **Read the dashboard** — `Repositories` page lists the portfolio with totals. Drill into `RepositoryDetail` for line history, extension percentages, contributor stats, **code health (radar + cards)**, **activity-rhythm punchcard**, recent activity.
5. **Export the portfolio** *(added 2026-09-20)* — `GET /api/repositories/export` returns JSON by default; `?format=csv` returns `text/csv`. Discoverable endpoint, no client affordance in the danger zone yet (see §13.7).
6. **See what changed since last visit** — the digest banner (12h–90d window; trailing-7d fallback outside that) and the `Insights` page, with language mix measured in **share, not lines**.
7. **Survive a network drop** *(added 2026-09-20)* — The PWA shell is cached; the `OfflineIndicator` shows network status when the connection is gone; on reconnect, the indicator clears and the next server fetch resumes. Read-only when offline.

*(The webhook-driven auto re-analysis journey added 2026-09-20 was removed 2026-09-21 — see §D.1 note. Repositories are added via the Add Repository dialog only.)*

Secondary paths: settings (`/api/settings/user-preferences` for counted extensions), diagnostics (`/api/diagnostics`), PWA install (`beforeinstallprompt` event captured at boot), re-analyse a single repo.

## 3. Pinned tech stack (versions)

- **.NET 10** SDK (`global.json`: `10.0.100`, `rollForward: latestMinor`, no prerelease). `<LangVersion>preview</LangVersion>` to follow the SDK forward.
- **Blazor WebAssembly** client, `<PublishTrimmed>true</PublishTrimmed>`, `<IsTrimmable>true</IsTrimmable>`, `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`. AOT off (separate switch; no rule requires it).
- **ASP.NET Core 10** minimal-API host (`WasmBlazorWebAssemblyHost=true`). Scalar UI in Dev for the OpenAPI surface.
- **Centralised package management** — all versions in `Directory.Packages.props`; no `<Version>` on `<PackageReference>`.
- **Compiler guards** — `Directory.Build.props` sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<Nullable>enable</Nullable>`.
- **Git-driven versioning** — MinVer 6.0.0 derives `AssemblyVersion`/`FileVersion`/`InformationalVersion` from the nearest `v*` tag.
- **Key runtime packages** — Radzen.Blazor 8.4.2 (UI), HybridCache 10.0.0, OpenTelemetry 1.15.3 + OTLP exporter, Serilog.AspNetCore 10.0.0, Azure.Data.Tables 12.11.0, Azure.Identity 1.21.0, Azure.Extensions.AspNetCore.Configuration.Secrets 1.5.1, Azure.Monitor.OpenTelemetry.AspNetCore 1.4.0, Microsoft.AspNetCore.SignalR.Client 10.0.13, LibGit2Sharp 0.31.0, Microsoft.CodeAnalysis.CSharp 4.14.0 (syntax-only, no Workspaces.MSBuild), AspNet.Security.OAuth.GitHub 9.3.0, Scalar.AspNetCore 2.13.20, Microsoft.OpenApi 2.12.2. (MediatR and FluentValidation were removed 2026-09-21 — 17 single-caller handlers are injected directly and the four bulk-add rules are plain checks in `Shared/RepositoryValidators.cs`.)
- **Test stack — single stack** — xunit 2.9.3 + NSubstitute 5.3.0 + FluentAssertions 8.8.0 + Xunit.SkippableFact 1.5.23 + Microsoft.Playwright 1.55.0 + Testcontainers.Azurite 4.6.0. SSH.NET pinned past advisory GHSA-q939-rpr3-3284 via central packages.
- **Local dependencies** — Docker Compose: Azurite (table 10002, blob 10000, queue 10001) + Jaeger all-in-one 1.62.0 (UI 16686, OTLP/gRPC 4317, OTLP/HTTP 4318). Start with `docker compose up -d`.

## 4. Build / Test / Lint / Run commands

```powershell
# build (every PR)
dotnet build PoRepoLineTracker.slnx /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

# publish (what CI ships)
dotnet publish src/PoRepoLineTracker.API /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

# tests, in dependency order
dotnet test tests/PoRepoLineTracker.Unit          # pure logic — required to pass before commit
dotnet test tests/PoRepoLineTracker.Integration   # Azurite via Testcontainers
dotnet test tests/PoRepoLineTracker.E2EAPI        # running host required
dotnet test tests/PoRepoLineTracker.E2EUI         # Playwright; first run needs `playwright.ps1 install`

# local run (https profile; Azurite must be up)
dotnet run --project src/PoRepoLineTracker.API --launch-profile https
```

**CI gate** — Unit only (commit `845c024`). Integration/E2E run on demand or locally. A skip in E2EUI means the app is not up, not that a fixture is missing.

## 5. Project structure

```
PoRepoLineTracker.slnx            ← solution
Directory.Build.props             ← TreatWarningsAsErrors, Nullable, LangVersion, MinVer
Directory.Packages.props          ← every package version (central management)
global.json                       ← .NET 10.0.100 SDK pin
docker-compose.yml                ← Azurite + Jaeger

src/
  PoRepoLineTracker.API/          ← Minimal-API host; hosts the WASM client; vertical-slice Features/
    Program.cs                    ← composition root + middleware order
    Auth/                         ← GitHub OAuth + FakeAuthHandler (throws in Production)
    Hubs/                         ← SignalR AnalysisHub (outside /api by design)
    Middleware/                   ← ApiNotFound, Antiforgery, ProductionAuthEnforcement, SecurityHeaders, ExceptionHandling, LogEnrichment
    Storage/                      ← Azure Data Tables repositories (RepositoryDataService, UserService, UserPreferencesService, CodeHealthSnapshotStore)
    Analysis/                     ← GitHubService (git CLI clone/pull), FileIgnoreFilter, SourceLineCounter
    Extensions/                   ← AddInfrastructure, AddAuth, AddTelemetry
    Platform/                     ← shared Po liveness, config keys
    Features/                     ← vertical slices; a folder per multi-file slice, single-file slices loose
      AntiforgeryEndpoints.cs     ← /api/antiforgery/token
      Auth/                       ← /auth/login, /auth/me, /signin-*, /signout-*
      CodeHealth/                 ← /api/code-health/{id} (six line-oriented factors; markup band separate)
      Contributors/               ← contributor stats weighted by lines added
      SeedEndpoints.cs            ← /api/dev/seed/repository (Development only)
      Diagnostics/                ← /diag, /api/diagnostics
      GitHubEndpoints.cs          ← /api/github/user-repositories
      Insights/                   ← /api/insights/portfolio, /api/insights/digest, /api/insights/digest/seen
      Repositories/               ← bulk add, list, charts, linehistory, extension %, contributors, analysis, re-analyse, delete, delete-all
      SettingsEndpoints.cs        ← /api/settings/user-preferences
    Telemetry/                    ← OpenTelemetry wiring + Application Insights

  PoRepoLineTracker.Client/       ← Blazor WASM (Radzen + scoped .razor.css)
    App.razor                     ← Router + CascadingAuthenticationState
    Layout/                       ← MainLayout (skip link via JS FocusAsync, not fragment nav)
    Pages/                        ← Login, Repositories, RepositoryDetail, CodeHealth, ExtensionsCounted, Diagnostics, Insights
    Components/                   ← AllReposComparisonChart, ContributorChart, Repositories/* (incl. CodeHealthRadar, CommitActivityHeatmap, RepositoriesGrid), Shared/* (incl. OfflineIndicator)
    Services/                     ← ApiAuthenticationStateProvider, RepositoryCommandClient, UserPreferencesClient, AntiforgeryHandler, AnalysisFeedClient, AnalysisWatcher, AppHttpJsonExtensions
    wwwroot/                      ← service-worker.js (dev no-op), service-worker.published.js (real cache, server-route prefix exceptions)

  PoRepoLineTracker.Shared/       ← DTOs, enums, interfaces, source-generated JSON contexts
    Domain/                       ← GitHubRepository, CommitLineCount, CommitStreaks, RepositoryTotals (single definition of "Total Lines"), StronglyTypedIds, User, UserPreferences
    Models/                       ← AnalysisProgressDto, AuthResponse, ConfigKeys, ErrorResponse, Dtos/
    Serialization/                ← AppJsonSerializerContext (trim-safe, reflection resolver unreachable from client)

tests/
  PoRepoLineTracker.Unit/         ← pure logic; the only CI-gate tier
  PoRepoLineTracker.Integration/  ← ASP.NET host + Testcontainers Azurite
  PoRepoLineTracker.E2EAPI/       ← HTTP contract tests
  PoRepoLineTracker.E2EUI/        ← Playwright (mobile + desktop)

infra/                            ← Bicep: main.bicep, resources.bicep, availability-test.bicep, keyvault-access.bicep, storage-role.bicep
.github/workflows/deploy.yml      ← lint + build → package → webapp deploy; manual `deploy_infra` input applies Bicep
```

### 5.1 Tracked files that look deletable but aren't

These are referenced from production paths; a lazy cleanup must keep them.

- `src/PoRepoLineTracker.API/startup.sh` — App Service Linux startup; installs `git` (LibGit2Sharp's network stack SIGABRTs in the container, so `GitClient` shells out). Configure via `az webapp config set --startup-file`.
- `src/PoRepoLineTracker.API/PrefixKeyVaultSecretManager.cs` — `KeyVaultSecretManager` that strips the `PoRepoLineTracker--` prefix and treats the rest as `Section:Key`. Wired in `Program.cs` (search `AddAzureKeyVault`).
- `src/PoRepoLineTracker.API/appsettings.Development.local.json.template` — copy-rename template for the gitignored per-machine OAuth credentials. The deployed site never reads it; Program.cs loads the local file *after* Key Vault so dev credentials win locally without changing prod.
```

## 6. Code-style & conventions

- **`AGENTS.md` + `.github/copilot-instructions.md`** are the writing ruleset (YAGNI ladder; never cut validation, error handling, security, a11y). Comments explain *why*, not *what* — match the tone of the inline rationale already in this repo.
- **Vertical Slice Architecture** under `Features/{FeatureName}/` — endpoints, handlers, and queries co-located; single-file slices are flat files under `Features/`. Slices **may not reference each other**. Shared code that more than one slice needs goes to `Storage/`, `Analysis/`, or `Shared/`.
- **Source-generated JSON** in `Shared/Serialization/AppJsonSerializerContext`. The reflection resolver is deliberately unreachable from the client — every wire type needs a `[JsonSerializable]` entry, otherwise trim kills it.
- **No single-implementation interfaces** — only abstract when a test or alternate impl already proves the seam; otherwise use the concrete type.
- **AuthZ on repositories** — any route that takes a repository id from the URL uses `Auth/RepositoryOwnership`. The check lives outside slices so the second slice to need it cannot copy a stale copy.
- **Scoped CSS** — root the `.razor.css` at a plain `<div>`; Radzen components don't carry the `[b-xxx]` attribute. `::deep` for anything Radzen renders. When markup moves between components, its scoped CSS moves with it.
- **`<ChartCard>` requires an explicit `<ChildContent>` when paired with `<Toolbar>`** — passing the page body as a value-type child fires RZ9996 and the toolbar silently disappears. The wrapper sets `ChildContent` first and `Toolbar` last via separate parameters, never one inside the other.
- **Headers/landmarks** — every page owns an `<h1>`, a `<PageTitle>`, and lives inside `<main>` in the layout. The page title is a real `<h1 class="page-hero__title">` written in the page's own markup (`PageHero` can't change the tag of a fragment handed to it).
- **Skip link** moves focus in code (`ElementReference.FocusAsync` on click with `preventDefault`), not via fragment navigation — Blazor's router intercepts anchor clicks.
- **Install button** — driven by the `beforeinstallprompt` event, captured in `index.html` (not in a module imported later). `registerInstallListener` returns the current availability *and* subscribes.
- **Config keys** go in `ConfigKeys` (Shared) — no magic strings.
- **Comments** explain the bug they prevent (the codebase already follows this convention; match it).

## 7. Testing strategy

- **Single stack** — xunit + NSubstitute + FluentAssertions. No other assertion or mocking library.
- **Coverage** — coverlet with `tests/coverlet.runsettings` on the Unit tier; CI does not currently enforce a threshold.
- **Dev/test auth** — `X-Fake-User` header (GUID verbatim; any other string hashes to a stable GUID). `FakeAuthHandler.ThrowIfProduction` makes registering it in Production a startup crash.
- **Seeding chart data** — `POST /api/dev/seed/repository` writes synthetic history (Development only, idempotent). Without it every chart test in E2EUI skips and the suite reports false health.
- **InternalsVisibleTo** — `PoRepoLineTracker.Unit` and `PoRepoLineTracker.Integration` see internals; prefer an `internal` helper over reflection.
- **E2EUI first run** — `pwsh tests/PoRepoLineTracker.E2EUI/bin/Debug/net10.0/playwright.ps1 install`.
- **Unit tests are not enough before commit** — they pass against changes that break the integration tier (composition-root changes in particular are invisible at unit scope and load-bearing for integration). Run Unit + Integration at minimum.

## 8. Boundaries

### Always (no further approval)

- **Security** — HTTPS-only cookies outside Development; `__Host-` antiforgery cookie gated by `Security:RequireSecureCookies`. `SecurityHeadersMiddleware` is non-negotiable. `ApiNotFoundMiddleware` answers 404 for unmatched `/api` GETs (middleware, not a route — a catch-all `/api/{**rest}` would win precedence against real routes and break authorization).
- **Auth in Production** — `ProductionAuthEnforcementMiddleware` redirects unauthenticated requests to the app's own `/login` (same origin, PWA `start_url: "/"`) and is unit-tested against a substituted `IWebHostEnvironment` (the only place its branch runs).
- **Validation at trust boundaries** — plain checks in `Shared/RepositoryValidators.cs` on the bulk-add slice (owner/name restricted to GitHub's character set — the server builds the clone URL from them and never accepts one from a caller); `Authorization` and `RepositoryOwnership` checked before any storage call.
- **Trim-safe JSON** — every wire type has a `[JsonSerializable]` entry; the reflection resolver is unreachable from the client.
- **Single source of truth** for "Total Lines" (`Shared/Domain/RepositoryTotals`), commit streaks (`Shared/Domain/CommitStreaks`), extension snapshots (`RepositoryTotals.LinesByFileTypeAsOf`), and progress frames (`Shared/Models/AnalysisProgressDto`). Three pages can show a streak; one home avoids three numbers that drift.
- **Progress frames are immutable in transit** — the loop hands the hub a fresh list each tick; appending to a shared list mid-serialize throws.

### Ask first

- New identity provider (Microsoft/Entra was removed because a Microsoft principal carries no GitHub credential — reintroducing it is a different conversation).
- New external SaaS, any AI/ML model API, or new analytics backend.
- Any new persistent table, new Bicep resource, or new App Insights query.
- Changes to `appsettings.json` schema (key renames break deployed instances).
- `dotnet format` / formatter version bumps.
- New test framework or assertion library (the stack is xunit + NSubstitute + FluentAssertions — period).

### Never

- **Microsoft/Entra sign-in, WebGL backdrop, Web Audio feedback, commit tagging, single-add `POST /api/repositories`, SmartAlerts, Failed Operations, AI model selector** — each was removed for a stated reason (see §9; if reinstated, treat it as a new feature with its own SPEC delta).
- **AI/ML analytics** — sentiment, AI-share detection, ML on commits. Out of scope unless explicitly added.
- **Catch-all endpoint under `/api`** — `MapFallback("/api/{**rest}", ...)` was tried and reverted (caught real routes, passed authorization anonymously, came back 400 from the antiforgery gate).
- **Per-item storage lookup inside a loop** — `CommitExistsAsync` was removed for this; pre-load the set the loop needs (`GetCommitLineCountsByRepositoryIdAsync`) and look SHAs up in memory.
- **Mock-data banner / live mock mode** — retired. Data is real or absent.
- **Reflection resolver reachable from the trimmed client** — defeats the source-generated JSON contract.
- **`__Host-` cookie without `Secure`** — browsers reject it; the integration tier runs over plain HTTP as `"Test"` and opts out via `Security:RequireSecureCookies=false`.

## 9. Out of scope (explicit non-goals)

1. Microsoft/Entra sign-in (no GitHub credential → can't read repos).
2. WebGL backdrop + Web Audio feedback (UI; removed in cleanup).
3. Commit tagging (`CommitTaggerService`, the badge row it fed).
4. Single-add `POST /api/repositories` — `/bulk` is the only write path.
5. SmartAlerts, Failed Operations, AI model selector (removed; reasons documented).
6. AI/ML analytics (sentiment, AI-share, ML on commits).
7. Multi-tenant / org-level portfolios (single-user model; the user's repositories only).
8. Per-repo auto-prune TTL / auto-prune of aggregates (retention is "forever, manual purge" per agreement).
9. Custom code health weights or scoring changes (the bands are pinned: 6 line-oriented factors; markup band separate; nesting = 90th-percentile line depth; weights frozen).

## 10. Edge cases (load-bearing)

- **`TotalLines` on a commit is a snapshot, not a delta.** Repository size = newest commit's value, never summed, never windowed. `RepositoryTotals` is the single definition; two pages each grew their own version and printed different numbers under the same label.
- **Vendored third-party code is excluded, and one rule is by CONTENT not name.** `FileIgnoreFilter.ShouldIgnoreDirectory(path, entryNames)` prunes a directory whose immediate children include two or more root markers (`LICENSE*`, `.github`, `.gitmodules`, `CODEOWNERS`, `CODE_OF_CONDUCT.md`, `.pre-commit-config.yaml`, …) — whole other project copied in, whatever folder name. Filter changes only affect new analysis; stored counts need a re-analyse.
- **Comment syntax is per language family, and CSS is not C.** Plain `.css` has no `//` line comment (only `/* */`); it was registered with `//`, truncating `url(https://…)`. `.scss`/`.less` *do* have `//` — they cannot share an entry with `.css`. `.razor`/`.cshtml` use `@* *@` and `<!-- -->`. All registrations live in one table (`CommentSyntax`) read by both `SourceLineCounter` and `CodeMetricsAnalyzer` — two copies would drift.
- **Nesting is the 90th-percentile line depth, not the maximum.** One wrapped argument list measures far past the structure around it; using max let a 40-line file with a wrapped CSS string outrank genuinely tangled ones.
- **Markup is judged on its own nesting band.** Nested components aren't a smell; scoring `.razor` against the code band gave this repo's markup 48 against C# 94 — a fact about the format, not the code.
- **Code health is proxies, not a parser** — say so wherever it is shown. `CodeHealthCard` states this on the card, not in a tooltip. Measuring and judging are separate types (`CodeMetricsAnalyzer` counts; `CodeHealthScoring` judges).
- **Contributor share is weighted by lines added**, not averaged per commit. (A one-line commit and a 2,000-line refactor are not the same event.)
- **Analysis progress has one path.** The SignalR hub pushes frames; the fallback poll exists *only* for hub-unreachable and synthesises the same frames rather than handling completion itself. Do not add a second completion path — there used to be three and they had drifted.
- **The digest's read and its "mark seen" write are separate calls.** `GET /api/insights/digest` never records the visit; `POST /api/insights/digest/seen` does, and the banner only calls it once it has actually rendered. Merging them makes a page opened and closed without looking consume the window. The write is read-modify-write because `SavePreferencesAsync` upserts with `TableUpdateMode.Replace` — a partial preferences payload would blank the counted-extensions list.
- **A last visit is only used when it is between 12 hours and 90 days old.** More recent → trailing 7 days. Older → trailing 7 days too. A year is not "since you were last here".
- **Language drift is measured in share, not lines.** A file type's share can grow while the type shrinks — everything else shrank faster. A line-count delta cannot show it.
- **Cookie hardening is keyed on HTTPS, not the environment name.** `Security:RequireSecureCookies` decides `__Host-` + `Secure` for the antiforgery cookie. Integration runs as `"Test"` over plain HTTP and opts out explicitly.
- **Registration-time config cannot come from `ConfigureAppConfiguration`.** Those delegates run *after* top-level statements in `Program.cs`; anything read during `AddInfrastructure(builder.Configuration, …)` won't see them. `CustomWebApplicationFactory` uses `builder.UseSetting(...)` for exactly this.
- **A running app locks the build** — `dotnet build` fails with MSB3027. Stop the host first: `Get-Process -Name PoRepoLineTracker.API | Stop-Process -Force`.
- **Tree memoization** — `GitHubService` caches per-tree/per-blob counts for the lifetime of its scoped instance (one analysis run). Keyed on object id **and path** (filter answers depend on location). Dropped when the counted-extension set changes. `CountLinesInCommitAsync` returns a **copy** — a caller mutating a memoised instance would poison every later commit sharing that tree.
- **`SanitizeRepoName` keeps backslashes on Linux** — where this app deploys. The fix is in the helper, not in callers.
- **`libgit2` SIGABRT** under the App Service Linux container is reproducible; the runtime calls `git` CLI, not `LibGit2Sharp`, for clone/pull.

## 11. Error states

- **Unauthenticated `/api` request** → `ApiNotFoundMiddleware` answers 404 for unmatched GETs first; matched authenticated endpoints pass through `ProductionAuthEnforcementMiddleware`. In Production, unmatched authenticated requests on protected routes redirect to `/login`. In Development, `FakeAuthHandler` lets `X-Fake-User` through.
- **Antiforgery missing or wrong** on a write → 400 (after `UseAuthorization`, so unauthenticated writes still get 401).
- **Unmatched `/api` GET** → 404 (middleware, not the SPA fallback — the fallback used to return 200 carrying HTML to JSON callers).
- **Telemetry unavailable** (App Insights connection string absent) → app runs fine; `/diag` reports App Insights as "Not configured" (accurate, not a bug). `TelemetrySettings` is the single resolver both `AddTelemetry` and `/diag` use, so the page cannot disagree with what is actually registered.
- **OTLP endpoint absent** → spans built and dropped; `/diag` reports "OTLP Exporter — Not configured".
- **Repository id mismatch** between caller and stored record → 403 via `Auth/RepositoryOwnership`; slices never copy the check.
- **Stale `appsettings.Development.json`** without a `KeyVault:Uri` or `ApplicationInsights:ConnectionString` → app boots with secrets from user-secrets/env/local-override; `/diag` reflects what's actually registered.
- **First E2EUI run on a new machine** → Playwright browsers missing; `playwright.ps1 install` once per repo checkout.

## 12. Success criteria (numbered, measurable)

1. **Unit tier green on every commit.** `dotnet test tests/PoRepoLineTracker.Unit` returns 0.
2. **E2EUI tier zero skips.** A skip means the app is not up — not a fixture gap. `dotnet test tests/PoRepoLineTracker.E2EUI` exits 0 with no `Skip` entries.
3. **Bicep deploy is idempotent.** Re-applying `infra/main.bicep` against the existing RG succeeds; `RoleAssignmentExists` is tolerated.
4. **`/health` returns 200 with no auth** when the host is up (deploy smoke test + Azure probe).
5. **`/diag` is honest about telemetry** — App Insights and OTLP statuses match what `AddTelemetry` actually registered (single resolver). No "Not configured" claim that disagrees with registration.
6. **Analysis completes end-to-end on a real GitHub repository** with a known seed (e.g. a 30-day synthetic history from `/api/dev/seed/repository`) without skipping the per-commit count step.
7. **No Critical or Important `/security-review` or `/code-review` findings** at every release commit.
8. **Mock fallbacks work without keys** — app boots without `GitHub:ClientId`, `KeyVault:Uri`, or `ApplicationInsights:ConnectionString`; `/diag` reflects absence, doesn't crash.
9. **`AGENTS.md` + `.github/copilot-instructions.md` carry v4.10.0 ruleset** verbatim — the two files share the same ruleset text.
10. **Repository ownership check is single-sourced** — `Auth/RepositoryOwnership` is the only place that decides "is this repo the caller's"; a grep for `RepositoryId` in `Features/` finds it referenced but never re-implemented.
11. **Coverage gate is informational, not a hard rule** (per agreement — the bar is "zero skips, no Critical/Important findings" not "X% line coverage").

## 13. Open questions

1. ~~Should the recap gain a "compare to previous year" view?~~ No recap exists in the code (no `/api/recap`, no page); a year-in-review would be a new feature with its own SPEC delta.
2. ~~Is there a desire for an offline read mode (PWA cached shell + cached API responses)?~~ **Resolved 2026-09-20**: shipped via `wwwroot/service-worker.published.js` (cached shell, server-route prefix exceptions) and `Components/Shared/OfflineIndicator.razor` (online/offline badge wired through `index.html`'s `registerNetworkStatusListener`).
3. Should the `POST /api/dev/seed/repository` endpoint move under an env flag (e.g. `Hosting:EnableDevSeed`) instead of relying on `IsDevelopment()` — for cases where a non-Development environment needs synthetic data (e.g. a staging slot)?
4. Code health weights are frozen. If a band needs tuning later, the change must be additive (a new factor) — not a re-weight of existing ones, or stored scores become incomparable. **Note 2026-09-20**: a sixth visualisation, `Components/Repositories/CodeHealthRadar.razor`, was added; weights are unchanged.
5. MinVer is at 6.0.0; bumping to 7.x is on the table for .NET 10 SDK compat but is a one-line change with no current need.
6. ~~Webhook allowlist / rate-limit~~ — moot; the webhook receiver was removed 2026-09-21 (§D.1).
7. **New 2026-09-20** — The portfolio export currently lives only at `/api/repositories/export`. Should the client expose a one-click "Export" affordance in `Repositories.razor`'s danger zone or hero, or keep it as a discoverable endpoint for tools?

---

## Migration (added 2026-09-19)

- **Ponytail v4.10.0** installed at repo root (`AGENTS.md`) and `.github/copilot-instructions.md`. Both are verbatim copies from `DietrichGebert/ponytail` and carry the cross-agent YAGNI/reuse/stdlib/platform/dep/one-liner/minimum ruleset.
- **`CLAUDE.md`, `NET_RULES.md`, `.claude/settings.json` retired.** Po platform conventions → ponytail's ruleset. "Things that will bite you" / domain rules → §6, §8, §10 of this SPEC.
- **`SPEC.md` is the new durable reference** for contracts, journeys, boundaries, success criteria. `AGENTS.md` is the writing ruleset. Both must be kept in sync — when a decision in §8/§10 changes, the matching rule here updates; when ponytail upgrades, the rule files sync verbatim.

---

## Delta: features landed 2026-09-20 (commits `8a439ed` + `307c20b`)

These features landed on `origin/master` after this SPEC's initial freeze. They are now **in-scope**; the non-goals in §9 are unchanged (AI/ML, SmartAlerts, Microsoft/Entra, etc. remain out). Each ships with its own unit tests; the unit tier is green.

### D.1 — In-scope features added

1. **GitHub webhook receiver** — **REMOVED 2026-09-21.** Nothing in-repo configured `GitHub:WebhookSecret` (absent from every appsettings) and no GitHub-side wiring existed in infra, so the endpoint, `GitHubWebhookPayload`, its config key and its unit tests were deleted. Restore from git history if a signed webhook is ever wanted — the HMAC verification contract below documents what it did.
   - *(Historical contract)* `POST /api/webhooks/github` accepted a GitHub `push` payload, verified HMAC-SHA256 against `GitHub:WebhookSecret` (constant-time comparison via `CryptographicOperations.FixedTimeEquals`), and on a default-branch push for a tracked repository re-queued `AnalyzeRepositoryCommitsCommand` on a background task; `AllowAnonymous` + `SkipAntiforgeryAttribute`.

2. **Punchcard / activity-rhythm chart** (`/api/repositories/{id}/punchcard`, `Features/Repositories/GetRepositoryPunchcardQuery.cs`; `Components/Repositories/CommitActivityHeatmap.razor`)
   - 7×24 grid of `(dayOfWeek, hour)` buckets. `PunchcardItemDto` carries `CommitCount` + `LinesAdded` + `LinesRemoved`. Default range is 365 days; `Days` parameter is the knob.
   - Pure CSS-grid SVG-less heatmap — no chart library dependency added.
   - Rendered on `RepositoryDetail.razor`; `EmptyHint` points the user at the range selector.

3. **Code health radar chart** (`Components/Repositories/CodeHealthRadar.razor`)
   - Pure-SVG 6-axis radar visualising the existing six line-oriented code-health factors (SPEC §10). **Scoring weights unchanged** — radar is a visualisation, not a re-weighting. Markup confirms this.

4. **PWA offline indicator** (`Components/Shared/OfflineIndicator.razor` + `wwwroot/service-worker.published.js` + `wwwroot/index.html`)
   - The published service worker caches the Blazor shell; the dev worker remains a no-op (SPEC §10 "the swap happens via `<ServiceWorker>` item in the client csproj"). Server-route prefix exceptions (`/api/`, `/auth/`, `/hubs/`, `/health`, `/signin-`, `/signout-`) stay correct.
   - The `OfflineIndicator` calls `registerNetworkStatusListener` (captured at boot in `index.html`, not in a module imported later, per SPEC §6).
   - **The "mock-data banner" remains retired.** Offline is a different concept — the indicator shows network status, not data provenance. SPEC §8 explicit "never" stays.

5. **Portfolio export** (`/api/repositories/export`, `Features/Repositories/PortfolioExportEndpoints.cs`)
   - `GET /api/repositories/export` returns JSON; `?format=csv` returns `text/csv` with header `Owner,Repository,TotalLines,LastAnalyzedUtc,CloneUrl`. CSV writer lives next to the endpoint and is exported (not private) so the `PortfolioExportTests` suite can pin it.
   - Uses `RepositoryTotals.LatestTotalLines` (SPEC §10 single source of truth) for the totals.

6. **Custom glob support for counted extensions** (`Analysis/FileIgnoreFilter.cs`, `Storage/UserPreferencesEntity.cs`, `Models/UserPreferences.cs` in Shared)
   - The user-preferences schema now stores counted files as globs (e.g. `*.cs`, `**/*.razor`) in addition to literal extensions. `FileIgnoreFilter` accepts both shapes.

7. **RepositoriesGrid extraction** (`Components/Repositories/RepositoriesGrid.razor`, `Models/RepositoryGridRow.cs`, slim `Pages/Repositories.razor`)
   - The bespoke table lifted out of the 1,200-line `Repositories.razor` into a reusable grid component. Five columns: Repository / Total Lines / Last Analyzed / Status / Actions. CSS moved into `RepositoriesGrid.razor.css`. Phase 3 pick **A7** (tiles-above-grid, single visual seam) preserved.

### D.2 — SPEC sections updated to acknowledge the delta

- The webhook-related additions to §2, §5, §8, §11 and §12 recorded here on 2026-09-20 were withdrawn with the receiver (D.1 #1).
- **§5 Project Structure** — `Components/Repositories/CommitActivityHeatmap.razor`, `Components/Repositories/CodeHealthRadar.razor`, `Components/Shared/OfflineIndicator.razor`; `Models/Dtos/PortfolioExportRowDto.cs`, `PunchcardItemDto.cs`.
- **§8 Boundaries — Always** — `JsonSerializerContext` registration for every wire type that crosses the trim boundary.
- **§8 Boundaries — Never** — Unchanged. The mock-data banner stays retired. Offline indicator ≠ mock-data banner (see §D.1 #4).
- **§11 Error states** — export 401 (unauthenticated).
- **§12 Success criteria** — Add:
  13. **Portfolio export serves both formats.** `/api/repositories/export` returns JSON for the default and `text/csv` for `?format=csv`; both pin `RepositoryTotals.LatestTotalLines` as the totals source.
  14. **PWA offline indicator survives a network drop.** A browser session that goes offline renders the indicator within the next paint cycle and recovers when the network returns.

---

## Delta: audit fixes 2026-10-03

Security and correctness changes that alter contracts stated above.

- **The clone URL is never caller-supplied.** `BulkRepositoryDto` has no `CloneUrl`; the server builds `https://github.com/{owner}/{name}.git` at clone time. The user's token is embedded in whatever URL git is handed, so an accepted URL was a way to send that token to any host.
- **Deleting is per repository and complete.** Delete and "remove all" remove the rows, the code-health snapshots and the `repo_{id}` clone directory — only the caller's. "Remove all" used to delete the shared clone root; single delete used to leave the clone on disk.
- **A full re-analysis clears stored commits only after the clone/pull succeeds.**
- **`LastAnalysisAttemptUtc` / `LastAnalysisError`** on the repository row record the last run's outcome. The startup resume sweep takes only rows with no analysed commit *and* no attempt, so an empty or unreachable repository is not re-cloned on every restart.
- **Re-analyse and delete answer 409** (`analysis_in_progress`) while an analysis holds the repository's lock.
- **GitHub access tokens are encrypted at rest** (`IDataProtector`, in `UserService`); rows written before this are read as-is and re-encrypted at the next sign-in. `SaveTokens` is off.
- **The server PAT (`GitHub:PAT`) is used only in Development and Test.** `FakeAuthHandler` is registered only in Development and Test (an allow-list, not "anything but Production").
- **`returnUrl` on `/auth/login` must be a local path.**
- **CSP outside Development**: `script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'`; styles and fonts same-origin only (the Google Fonts links were redundant with the fonts Radzen ships and are gone); `connect-src 'self' wss:`.
- **Rate limits**: 30/min per user on bulk-add and re-analyse, 60/min per address on `/health`; bulk-add takes at most 200 repositories.
- **Merge commits record 0 added / 0 removed** (their lines were already reported by the branch's own commits) and **commit dates are stored in UTC** (they were the author's local wall-clock time labelled UTC). Both apply to newly analysed commits; stored rows change on re-analysis.
- **The final progress frame carries its owner** through the channel; it used to be dropped.

---

## Delta: owner-approved reversals 2026-10-04

Three items listed under §8 "Never" / §9 were reinstated at the owner's explicit request. Each is off unless switched on, and the app behaves exactly as before when it is.

- **Claude integration (replaces "no AI/ML model API").** `Analysis/ClaudeAssistant` is the only caller. It is active only when `Anthropic:ApiKey` is configured (Key Vault secret `PoRepoLineTracker--Anthropic--ApiKey`, or `appsettings.Development.local.json` locally); otherwise no client is built and nothing is sent. `Anthropic:Model` overrides the default model.
  - `GET /api/insights/assistant` — whether it is configured; the client renders no assistant UI when false.
  - `GET /api/insights/digest/narrative` — one or two sentences reading the digest. **Only numeric aggregates are sent**; the busiest repositories go as `R1..R3` and their names are substituted after the reply.
  - `POST /api/repositories/ask` — a question (≤200 characters) mapped to the grid's existing search / status / sort. Only the question is sent; the reply is schema-constrained and each field is re-validated against its allowed values.
  - Never sent: repository names, author names, commit messages, source code. That text is attacker-writable and is the prompt-injection route.
  - Rate limit: 10/min per user on both calling routes. A failed or declined call shows nothing; it is never an error on the page.
- **Web Audio cue.** A synthesised completion/failure tone, off by default, toggled in the header and stored in `localStorage["sound"]`.
- **WebGL backdrop.** On by default with no settings switch (owner's choice, 2026-10-04); `localStorage["backdrop"] = "off"` disables it. Never runs under `prefers-reduced-motion` or without WebGL2.
- **Settings page.** The custom ignore-patterns editor was removed from the UI; stored patterns are still honoured by analysis and preserved on save. The Diagnostics page has no sidebar link (still at `/diag`).

Still out of scope: sentiment, AI-share detection, ML over commit content, an AI model selector in the UI.

---

## Delta: features landed 2026-10-23

Five owner-requested features (F2, F3, F5, F9, F10). No new table, Bicep resource or config key; the repository row gains three columns (`AnalysisQueuedUtc`, `QueuedFullReanalysis`, `IsPublic`), which Table Storage takes without a migration.

- **Analysis queue (F2).** `Features/Repositories/AnalysisQueue` is the only way an analysis runs: bulk-add and re-analyse enqueue, two workers drain (per process; `ponytail:` comment names the ceiling). It replaces `ResumePendingAnalysis` and the endpoints' fire-and-forget `Task.Run`.
  - A re-analysis stamps `AnalysisQueuedUtc` (+ `QueuedFullReanalysis`) on the row *before* it is queued; the worker clears it after the job ends, from a fresh read. The startup sweep (`GetResumableRepositoriesAsync`) queues rows never attempted **or** still stamped, so a restart loses neither kind. A job cancelled by shutdown keeps its stamp and records no attempt.
  - Re-analyse, delete, remove-all, account delete and visibility changes answer 409 while a job is **queued or running** (`AnalysisQueue.IsPending`), not only while running.
  - Clone/pull is retried twice (15 s, 60 s) when the failure is transient; auth failures fail at once. Classification lives in one place, `Shared/Domain/AnalysisFailure`, read by the server's retry and by the grid.
  - New grid status `Reauth` (`AnalysisStatus.All`), shown as "Sign-in needed" with a link to `/auth/login`. The assistant's Ask schema offers it.
- **Account lifecycle (F3).** `DELETE /api/account` removes repositories (rows, snapshots, clones), preferences and the user row (token included), signs the cookie out, then revokes the OAuth grant (`DELETE /applications/{client_id}/grant`, app-authenticated). Revocation is best-effort and never attempted with the server PAT; `AccountDeletedDto.GrantRevoked` tells the client to point the user at GitHub's settings when it fails. Settings gains a "Your data" card: JSON/CSV export (resolves §13.7) and a typed-`DELETE` confirmation.
- **Hotspots (F5).** `GET /api/repositories/{id}/hotspots/{days}` (days clamped 1–3650). Change counts come from `git log --numstat --no-renames` on the clone; sizes and complexity come from `CodeMetricsAnalyzer` over the same walk Code Health uses, so the two tabs agree. Ranked by commits × code lines, top 60, cached for an hour per (repo, days, newest SHA). No clone on disk → `HasClone: false`, not an error. Rendered as a pure-SVG squarified treemap plus a ranked list (Hotspots tab).
- **Year in Review (F9).** `GET /api/insights/year/{year}?offset=<local−UTC minutes>` (year 1970..next year), page `/year-in-review[/{year}]`. Totals are `RepositoryTotals` snapshots at the local year's bounds; streak is `CommitStreaks.Longest`; language growth is share, not lines. Days and hours are local to the viewer (fixed offset for the year; `ponytail:` on DST). The closing card exports to PNG in the browser (serialised SVG → canvas, `data:` URL because CSP has no `blob:`), with no library. Optional Claude caption at `/year/{year}/narrative`: numbers only, the top repository sent as `R1`, same rate limit as the digest narrative.
- **Public badges and share page (F10).** Opt-in per repository (`PUT /api/repositories/{id}/visibility`, off by default). Anonymous `GET /badge/{id}/lines.svg`, `/badge/{id}/health.svg` and `GET /share/{id}` (server-rendered HTML with OG tags; no OG image, which would need a raster library).
  - Exposes only owner/name, latest total lines, the **memoised** code-health grade (an anonymous request never triggers a clone walk) and the newest commit date. Missing and private answer the same 404.
  - Outside `/api` by design; `/badge` and `/share` are on `ProductionAuthEnforcementMiddleware`'s public list and the published service worker's server-route list. Rate limit `public`: 120/min per address. Cards cached 10 min and evicted when visibility changes.
  - This is the first anonymous data surface: §8 "Validation at trust boundaries" now includes it.
