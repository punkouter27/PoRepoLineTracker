# PoRepoLineTracker — SPEC.md

What this app is, what it does, what it won't do, and what must stay true at every commit. The *how* of writing code (YAGNI ladder, the owner's project rules) is in `AGENTS.md`, which `.github/copilot-instructions.md` copies verbatim. This document is the *why* and the *contract*. Git history keeps the story of how each rule got here; this file states only the current rule.

---

## 1. Objective

A self-hosted **GitHub repository analytics** app. A user signs in with GitHub and imports their repositories. The app clones each one, replays its commit history, stores per-commit line counts, and shows line-count trends, file-type breakdowns, contributor statistics, code health, hotspots and a yearly recap. Pushes keep it current through GitHub webhooks.

One deployable: the ASP.NET Core API hosts the Blazor WebAssembly client, keeps state in Azure Table Storage, reads secrets from Azure Key Vault, and sends telemetry to Application Insights (Jaeger over OTLP locally). The external surface is **GitHub + Azure** only: no SaaS analytics, no AI/ML, no other identity provider.

## 2. User journeys

1. **Sign in.** `/auth/login` → GitHub OAuth → application cookie. Dev/Test tools send `X-Fake-User`; in a Development browser, `/auth/login?dev=true` (or no OAuth app configured) signs in a fixed "DevUser".
2. **Import.** The Add dialog is a one-click import: it lists the user's GitHub repositories, skips forks and archived ones, and posts the rest to `POST /api/repositories/bulk` (the only write path; at most 200 per request, so the client sends chunks). New repositories are queued for analysis. Already-tracked ones without a webhook get an incremental run, which also connects their hook.
3. **Watch analysis.** Progress frames go over SignalR (`/hubs/analysis`); a fallback poll synthesises the same frames when the hub is unreachable. The UI shows live tallies, never a percentage alone.
4. **Stay current.** After a successful analysis the app registers a GitHub push webhook on the repository. A push to the default branch queues an incremental run. Rows with a hook show a green bolt in the grid (§10 "Webhooks").
5. **Read the portfolio.** `/repositories` lists every repository with totals and 30-day movement. `/repositories/{id}` has tabs: activity, composition, health, hotspots.
6. **Insights.** One page, one picker: `/insights` opens on "All time"; the picker also offers each year (`/insights/{year}`) and "Code health" (`/insights/health`, every repository ranked by maintainability). 
7. **Share.** Anonymous badges (`/badge/{id}/lines.svg`, `/badge/{id}/health.svg`) and a share page (`/share/{id}`) for repositories made public. The Share tab was removed from the UI (owner, 2026-11-05); the endpoints stay so badges already in READMEs keep working, and opting in is now API-only (`PUT /api/repositories/{id}/visibility`).
8. **Own your data.** Settings exports the portfolio (JSON/CSV, `GET /api/repositories/export`) and deletes the account (`DELETE /api/account`).
9. **Survive a network drop.** The PWA shell is cached; `OfflineIndicator` shows when the connection is gone. Read-only offline.

Secondary: counted file types (`/api/settings/user-preferences`), appearance & effects switches (Settings), operator diagnostics (`/diagnostics`, not in the sidebar), PWA install, single-repository re-analyse.

## 3. Tech stack

- **.NET 10** (`global.json`, `rollForward: latestMinor`). Versions of every package are in `Directory.Packages.props` (central management, transitive pinning); no `Version` on a `PackageReference`.
- **Client:** Blazor WebAssembly + Radzen.Blazor, `PublishTrimmed`, trim analyzer on. Every wire type is in `Shared/Serialization/AppJsonSerializerContext`.
- **API:** ASP.NET Core minimal APIs. Not published as Native AOT (Key Vault configuration and some server dependencies are not AOT-safe), but built with `EnableTrimAnalyzer` + `EnableAotAnalyzer`, the Request Delegate Generator and the configuration-binding generator, so new reflection is a build error. Server-only JSON is in `ApiJsonContext`.
- **Git:** the `git` CLI only. `GitClient` runs clone, fetch, `log` and `--numstat`; `GitObjectReader` reads trees and blobs through one long-lived `git cat-file --batch`. No native git library (LibGit2Sharp crashed with SIGABRT on App Service Linux).
- **Key packages:** Azure.Data.Tables, Azure.Identity, Key Vault configuration, OpenTelemetry + Azure Monitor, Serilog, HybridCache, AspNet.Security.OAuth.GitHub, Microsoft.CodeAnalysis.CSharp (syntax only, for code health), Scalar (Dev OpenAPI UI), MinVer.
- **Compiler guards:** `Directory.Build.props` sets `TreatWarningsAsErrors` and `Nullable`. A new NuGet advisory (`NU1903`) fails the build; fix it by bumping the version, never by suppressing.
- **Versioning:** MinVer from `v*` tags for assemblies; the `vYYMMDD` label in the client's corner is the build date (`AssemblyMetadata` `BuildVersion`).
- **Tests:** xunit + NSubstitute + FluentAssertions (+ Xunit.SkippableFact, Playwright, Testcontainers.Azurite). No other assertion or mocking library.
- **Local dependencies:** `docker compose up -d` starts Azurite and Jaeger (UI 16686, OTLP 4317/4318). Without Docker, npm `azurite` works; the Integration tier falls back to it.

## 4. Commands

```powershell
dotnet build PoRepoLineTracker.slnx
dotnet run --project src/PoRepoLineTracker.API --launch-profile https    # https://localhost:5001

dotnet test tests/PoRepoLineTracker.Unit           # the CI gate
dotnet test tests/PoRepoLineTracker.Integration    # Testcontainers Azurite (or npm azurite)
dotnet test tests/PoRepoLineTracker.E2EAPI         # needs the app running
dotnet test tests/PoRepoLineTracker.E2EUI          # needs the app running + `playwright.ps1 install` once
```

A running app locks the build (MSB3027): `Get-Process -Name PoRepoLineTracker.API | Stop-Process -Force`.

## 5. Project structure

```
src/
  PoRepoLineTracker.API/          host; serves the WASM client
    Program.cs                    composition root; middleware order is load-bearing (§8)
    ApiJsonContext.cs             source-generated JSON for server-only payloads
    Analysis/                     GitClient, GitObjectReader, GitHubService, FileIgnoreFilter,
                                  SourceLineCounter, CommentSyntax, CodeMetricsAnalyzer, CodeHealthScoring
    Auth/                         GitHub OAuth, FakeAuthHandler (Dev/Test only), RepositoryOwnership
    Features/<Slice>/             vertical slices: Antiforgery, Auth, CodeHealth, Contributors, Dev,
                                  Diagnostics, GitHub, Insights, Repositories, Settings
    Hubs/                         SignalR analysis hub (outside /api by design)
    Middleware/                   ApiNotFound, Antiforgery, ProductionAuthEnforcement, SecurityHeaders,
                                  ExceptionHandling, LogEnrichment
    Storage/                      Table Storage services and entities
    Telemetry/                    OpenTelemetry wiring
  PoRepoLineTracker.Client/
    Pages/                        Root (→ /repositories), Repositories, RepositoryDetail, Insights,
                                  Settings, Login; Operator/Diagnostics
    Components/                   Account, Auth, Charts, Insights (AllTimeDashboard, CodeHealthDashboard),
                                  Repositories, Shared
    Models/                       AppRoutes (every route), AppNav (sidebar), RepositoryGridRow, ChartFormat
    Services/                     auth state, API clients, antiforgery handler, analysis feed
    wwwroot/js/                   backdrop.js, fx.js, city.js (decoration, §10 "Effects")
  PoRepoLineTracker.Shared/       domain types, DTOs, validators, ConfigKeys, AppJsonSerializerContext
tests/                            Unit, Integration, E2EAPI, E2EUI
infra/                            Bicep + setup/verify scripts
SCRIPTS/ensure-azure-access.ps1   every role the app and CI need
```

Files that look deletable but are not: `src/PoRepoLineTracker.API/startup.sh` (App Service startup; installs `git`), `PrefixKeyVaultSecretManager.cs` (strips the `PoRepoLineTracker--` secret prefix), `appsettings.Development.local.json.template` (copy for per-machine OAuth credentials; loads after Key Vault).

## 6. Conventions

- **Vertical slices.** `Features/<Slice>/` holds endpoints and handlers; handlers are injected directly (no mediator). Slices never reference each other. Code two slices need goes to `Storage/`, `Analysis/`, `Auth/` or Shared.
- **One repository type.** `GitHubRepository` is both the storage row's domain shape and the wire shape. Server-only state (`LocalPath`, `AnalysisQueuedUtc`, `QueuedFullReanalysis`, `WebhookSecret`) is `[JsonIgnore]`.
- **No single-implementation interfaces** unless a test already substitutes them.
- **Ownership.** Any route taking a repository id uses `Auth/RepositoryOwnership`.
- **Config keys** live in `Shared/Models/ConfigKeys`. Secrets come from Key Vault or the gitignored local override, never user-secrets. Optional features (App Insights, OTLP, webhooks) are off when their key is absent and the app still boots.
- **Scoped CSS** is rooted at a plain `<div>`; use `::deep` for anything Radzen renders. Markup moving between components takes its CSS with it.
- **`<ChartCard>`** needs an explicit `<ChildContent>` when it has a `<Toolbar>` (RZ9996 otherwise hides the toolbar).
- **Pages** own an `<h1 class="page-hero__title">` and a `<PageTitle>`. The skip link moves focus in code, not by fragment navigation.
- **Boot-time listeners** (`beforeinstallprompt`, network status, sound and effects switches) live in `index.html`, not in modules loaded later.
- **Comments** explain the bug they prevent; match the existing tone.

## 7. Testing

- **Tier caps:** at most 100 unit, 50 integration, 25 API (E2EAPI) and 25 UI (E2EUI) tests. Merge before adding.
- **CI gate:** Unit only. Integration and E2E run locally (`docker compose up -d` first). Composition-root changes (`Program.cs`, `AddInfrastructure`, `AddAuth`, middleware order, endpoint metadata) only fail in Integration, so run it for those.
- **Dev/test auth:** `X-Fake-User` (a GUID verbatim; any other string hashes to a stable GUID). Writes also need the antiforgery pair (`api-tests.http`). Registering `FakeAuthHandler` in Production crashes at startup.
- **Chart data:** `POST /api/dev/seed/repository` (Development only, idempotent). Without it E2EUI chart tests skip.
- **Git tests** build a real repository with the git CLI; nothing substituted can exercise object identity.
- **Tests run on any machine:** no drive letters, temp-folder or working-directory assumptions, no live config value as an expected result.

## 8. Boundaries

### Always

- **Middleware order:** static files → `ApiNotFoundMiddleware` (404 for unmatched `/api` GETs; a middleware, never a catch-all route) → authentication → authorization → rate limiting → `ProductionAuthEnforcementMiddleware` (redirects anonymous page requests to `/login` in Production) → `AntiforgeryMiddleware` (after authorization, so an anonymous write gets 401, not 400).
- **Security headers** (`SecurityHeadersMiddleware`) and CSP outside Development: `script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'`, same-origin styles and fonts, `connect-src 'self' wss:`.
- **Cookies:** HTTPS-only outside Development; the `__Host-` antiforgery cookie is keyed on `Security:RequireSecureCookies`, not the environment name.
- **Validation at trust boundaries:** owner/name restricted to GitHub's character set (`Shared/RepositoryValidators`); the clone URL is always built server-side (`GitHubRepository.CloneUrlFor`), never accepted from a caller, because the user's token is embedded in it. `returnUrl` on `/auth/login` must be local. Ownership and authorization are checked before storage.
- **Anonymous surface** is exactly: antiforgery token, `/auth/*`, `/health`, `/health/live`, `/badge/*`, `/share/*`, the webhook receiver and the SPA fallback. `AuthorizationFallbackTests` pins the list.
- **Rate limits:** 30/min per user on bulk-add and re-analyse; 60/min per address on `/health`; 120/min per address on badges, share pages and webhooks.
- **Tokens:** GitHub tokens are encrypted at rest (`IDataProtector`). The server PAT (`GitHub:PAT`) is used only in Development and Test.
- **Trim-safe JSON:** every wire type has a `[JsonSerializable]` entry; the reflection resolver is unreachable from the client.
- **Single sources of truth:** repository size (`RepositoryTotals`), streaks (`CommitStreaks`), failure classification (`AnalysisFailure`), progress frames (`AnalysisProgressDto`), routes (`AppRoutes`), ownership (`RepositoryOwnership`).
- **Progress frames are immutable in transit:** each tick hands the hub a fresh list.

### Ask first

- A new identity provider, external SaaS, AI/ML API or analytics backend.
- A new persistent table, Bicep resource or App Insights query.
- `appsettings.json` key renames (they break deployed instances).
- A new test framework or assertion library.

### Never

- A catch-all route under `/api` (it caught real routes, passed authorization anonymously and failed antiforgery).
- A per-item storage lookup inside a loop: pre-load the set (`GetCommitLineCountsByRepositoryIdAsync`) and look up in memory.
- A mock-data banner or live mock mode: data is real or absent.
- `Task.Run` for analysis work: everything goes through `AnalysisQueue`.
- A native git library (§3).
- Decoration on a data path (§10 "Effects").

## 9. Non-goals

1. Microsoft/Entra sign-in (no GitHub credential, so no repository access).
2. Commit tagging; SmartAlerts; Failed Operations; an AI model selector.
3. Single-add `POST /api/repositories` (`/bulk` is the only write path, because it dedupes).
4. AI/ML analytics: sentiment, AI-share detection, ML over commits.
5. Multi-tenant or organisation portfolios (one user's repositories only).
6. Automatic pruning of stored history (retention is forever, manual purge).
7. Re-weighting code health. The six line-oriented factors and their weights are frozen; a change must be a new, additive factor, or stored scores become incomparable.
8. A "since your last visit" digest (removed 2026-11-04; Insights covers it).

## 10. Load-bearing behaviour and edge cases

**Counting**
- `TotalLines` on a commit is a snapshot, not a delta. Repository size is the newest commit's value, never summed or windowed (`RepositoryTotals`).
- Commit dates are the author date in UTC. Merge commits record 0 added / 0 removed: their branch's own commits already counted those lines.
- Line stats come from one `git log --no-walk --stdin --numstat --no-renames -- <pathspecs>` over exactly the commits being analysed, limited to counted file types, so an incremental run diffs only new commits.
- Tree memoization: `GitHubService` caches counts per tree and per blob for its scoped lifetime (one analysis run), keyed on object id **and path** (ignore rules depend on location), dropped when the counted set changes. `CountLinesInCommitAsync` returns a copy; a caller mutating a memoised dictionary would poison every later commit sharing that tree. One `git cat-file` process serves the whole run and is disposed with the scope.
- Vendored code is excluded, and one rule is by content: a directory whose children include two or more project-root markers (`LICENSE*`, `.github`, `.gitmodules`, `CODEOWNERS`, …) is another project copied in. Filter changes apply to new analyses; stored counts need a re-analyse.
- C and C++ (`.cpp .h .hpp .cc .cxx .inl .c`, for Unreal projects) are default types with C-style comments. Unreal's `Intermediate/`, `Binaries/` and `DerivedDataCache/` are skipped at any depth (a plugin has its own), `Saved/` only at the root, `*.generated.h` everywhere; `ThirdParty/` was already a vendoring rule.
- New default types reach existing users once, through `DefaultsUpgrade` at startup: the saved list gains them (a `DefaultsVersion` marker on the preferences row, kept across saves, stops a removed type from coming back), and every repository whose clone holds such files is queued for a full re-analysis.
- Comment syntax is per language family (`CommentSyntax`, read by both the line counter and code metrics). Plain `.css` has no `//`; `.scss`/`.less` do; `.razor`/`.cshtml` use `@* *@` and `<!-- -->`.

**Code health**
- Proxies, not a parser; the card says so. Measuring (`CodeMetricsAnalyzer`) and judging (`CodeHealthScoring`) are separate types.
- Nesting is the 90th-percentile line depth, not the maximum. Markup is scored on its own nesting band.
- The portfolio ranking is the Insights "Code health" view; scoring every repository is a wait the user starts ("Analyze all"), not a load on navigation.
- The trend covers the whole history: every 1st of the month from the first commit, at most 48 points.

**Analysis queue**
- `AnalysisQueue` is the only way an analysis runs: a channel with two workers per process (`ponytail:` names the ceiling).
- A re-analysis (full, or incremental from a webhook) stamps `AnalysisQueuedUtc` on the row before it is queued; the worker clears it after the job, from a fresh read. The startup sweep re-queues rows never attempted or still stamped. A job cancelled by shutdown keeps its stamp.
- Re-analyse, delete, remove-all, account delete and visibility changes answer 409 while a job is queued or running.
- Clone/fetch retries twice (15 s, 60 s) on transient failures; auth failures fail at once and show "Sign-in needed". Classification is `Shared/Domain/AnalysisFailure`.
- A full re-analysis clears stored commits only after the clone/fetch succeeds. `LastAnalysisAttemptUtc` / `LastAnalysisError` record the last run, so an empty or unreachable repository is not re-cloned on every restart.
- Clones are `--no-checkout` and fetches move the branch ref with `update-ref`: a working tree would hit Windows' MAX_PATH and is never read. The token is never left in `.git/config`.
- Analysis progress has one completion path: the hub's frames. The fallback poll synthesises the same frames.

**Webhooks**
- Registration (`Features/Repositories/RepositoryWebhooks`) runs after each successful analysis without a hook, inside the pending window: `POST /repos/{owner}/{name}/hooks` (push events) with the owner's token and a random 40-hex secret per repository, stored on the row (`WebhookId`, `WebhookSecret`). A 422 means a hook for the URL exists with a lost secret; it is deleted and recreated. A refusal (not an admin) is logged and retried after the next analysis. Off when `WEBSITE_HOSTNAME` (`ConfigKeys.Hosting.PublicHostName`, set by App Service) is absent.
- Receiver `POST /api/webhooks/github/{id}`: anonymous, no antiforgery, 5 MB body cap. Unknown repository → 404; bad `X-Hub-Signature-256` (HMAC-SHA256, constant-time) → 401; `ping` and other events → 200; a non-default-branch push → 200, ignored; a default-branch push → incremental run. If a run is already pending, one more follows it (`ponytail:` in memory).
- Delete, remove-all and account delete remove the hook first (best-effort, while the token still exists).

**Insights**
- The year view (`GET /api/insights/year/{year}?offset=<minutes>`, 1970..next year) uses the viewer's local days and hours (fixed offset for the year; `ponytail:` on DST). Totals are `RepositoryTotals` snapshots at the year's bounds; the streak is `CommitStreaks.Longest`. The card exports to PNG in the browser (SVG → canvas, `data:` URL because CSP has no `blob:`).
- "All time" (`GET /api/insights/portfolio`) reads the whole history: lifetime totals, monthly trend (up to 36 points), the 10 largest commits ever, streaks, and language drift. The Repositories page reads its 30-day figures from the same response.
- Both views draw GitHub-style contribution calendars (`Components/Charts/ContributionCalendar`): the year view one for its year (from `DailyCommits`, viewer-local days), "All time" one per active year, newest first (from `Activity`, UTC days, ten-year cap). Fixed thresholds 1–2 / 3–5 / 6–10 / 11+, GitHub's greens, days after today not drawn.
- Language drift is measured in share, not lines: a type's share can grow while it shrinks.
- Contributor share is weighted by lines added, not averaged per commit.

**Hotspots**
- `GET /api/repositories/{id}/hotspots/{days}` (1–3650): change counts from `git log --numstat --no-renames`, sizes and complexity from the same walk Code Health uses. Ranked by commits × code lines, top 10, cached an hour per (repository, days, newest SHA). No clone → `HasClone: false`.

**Public badges**
- Opt-in per repository (`PUT /api/repositories/{id}/visibility`). They expose only owner/name, latest total lines, the *memoised* health grade (an anonymous request never triggers a clone walk) and the newest commit date. Missing and private answer the same 404. Cached 10 minutes and evicted on visibility change.

**Account**
- `DELETE /api/account` removes repositories (rows, snapshots, clones, hooks), preferences and the user row, signs out, then revokes the OAuth grant (best-effort; never with the server PAT). `GrantRevoked: false` tells the client to point the user at GitHub's settings.
- Delete is per repository and complete: rows, code-health snapshots and the `repo_{id}` clone, only the caller's.

**Repositories grid**
- The `RadzenDataGrid` sorts and filters itself; below 768px a card list replaces it and keeps its own sort controls, writing the same sort state (`localStorage["repoGrid"]`). The search box is the URL's `?q=`.
- The growth chart opens on "1yr"; other pages default to 180 days.

**Effects** (decoration, never on a data path)
- `backdrop.js` (WebGL aurora behind the page), `fx.js` (sound, particle overlay, SVG filters, pointer surfaces) and `city.js` (the year view's 3D city). Pages call them through interop inside try/catch and pass lists as comma-joined strings. A page behaves the same if they never load.
- Switches live in Settings → Appearance & effects: sound (`localStorage["sound"]`, off by default) and visual effects (`localStorage["fx"]` + `["backdrop"]`, applied on the next load). Nothing animates under `prefers-reduced-motion`; the backdrop is a still frame there.

**Configuration**
- Registration-time config cannot come from `ConfigureAppConfiguration` (those delegates run after `Program.cs`'s top-level statements); tests use `builder.UseSetting(...)`.
- `SanitizeRepoName` keeps backslashes on Linux, where the app deploys.

## 11. Error states

- Unmatched `/api` GET → 404 (middleware). Matched route, anonymous → 401. Anonymous page in Production → redirect to `/login`.
- Write without a valid antiforgery pair → 400 (after authorization, so anonymous writes get 401 first).
- Another user's repository id → 403 via `RepositoryOwnership`.
- Analysis queued or running → 409 `analysis_in_progress` for re-analyse, delete, remove-all, account delete and visibility.
- App Insights or OTLP not configured → the app runs; `/diagnostics` says "Not configured" (`TelemetrySettings` is the one resolver both use).
- Webhook with a bad signature → 401; unknown repository → 404.
- First E2EUI run on a machine → install Playwright browsers once.

## 12. Success criteria

1. Unit tier green on every commit; Integration green for composition-root changes.
2. E2EUI exits 0 with no skips when the app is up.
3. `/health/live` and `/health` answer 200 after a deploy (the deploy's health gate checks both).
4. The app boots without `GitHub:ClientId`, `KeyVault:Uri`, `ApplicationInsights:ConnectionString` or `WEBSITE_HOSTNAME`; `/diagnostics` reflects what is absent.
5. Re-applying `infra/main.bicep` succeeds.
6. The API builds clean with the trim and AOT analyzers on.
7. Test tiers stay within their caps (§7).
8. No Critical or Important findings from `/security-review` or `/code-review` at a release commit.
9. `AGENTS.md` and `.github/copilot-instructions.md` are identical.

## 13. Open questions

1. Should `POST /api/dev/seed/repository` move behind a config flag (e.g. for a staging slot) instead of `IsDevelopment()`?
2. Should a scheduled sweep pick up newly created GitHub repositories and refresh repositories that have no webhook?
