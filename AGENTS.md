# AGENTS.md — PoRepoLineTracker

## 1. Standard

This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
may be shared.

The Po Web App Standard follows (repo-specific exceptions are under "Repo-specific notes").


This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

Several sessions (people and agents) commit to this repo, sometimes at the same time.
- Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
- Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
- Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
  removed on purpose stay removed).
- Don't commit or sync from the IDE while an agent is working in this folder.
- Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

Tested 2026-11-20 (.NET SDK 10.0.4xx per `global.json`, `rollForward: latestFeature`).

| Step | Command | Needs / without it |
| --- | --- | --- |
| Format | `dotnet format PoRepoLineTracker.slnx --verify-no-changes` | — |
| Build | `dotnet build PoRepoLineTracker.slnx -c Release` (warnings are errors) | — |
| Unit (142) | `dotnet test tests/PoRepoLineTracker.Unit -c Release --no-build` | nothing |
| Integration (38) | `dotnet test tests/PoRepoLineTracker.Integration -c Release --no-build` | Docker for Testcontainers Azurite; without it prints "Docker is not running…" and runs on in-memory substitutes |
| E2E API (25) | `E2E_BASE_URL=http://localhost:7208 dotnet test tests/PoRepoLineTracker.E2EAPI -c Release --no-build` | a running app; otherwise every test skips ("No app instance reachable at …") |
| E2E UI (21) | `E2E_BASE_URL=https://localhost:7207 dotnet test tests/PoRepoLineTracker.E2EUI -c Release --no-build` | a running app over HTTPS + Playwright browsers; otherwise skips |
| Run locally | `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="https://localhost:7207;http://localhost:7208" KeyVault__Uri= dotnet run --project src/PoRepoLineTracker.Api --no-launch-profile` | Azurite on :10002 (`docker compose up -d`) or `AzureTableStorage__ConnectionString`; → https://localhost:7207 |
| Deploy | push to `master` → `.github/workflows/deploy.yml` (format, build, Unit, Integration, publish, deploy); `*.md` and `SCREENSHOTS/**` are paths-ignored | repo variables for OIDC |
| Deploy check | `curl -sI https://app-porepolinetracker2.azurewebsites.net/health/live` → `X-Commit: <pushed sha>`; `pwsh scripts/post-deploy-smoke.ps1 -BaseUrl https://app-porepolinetracker2.azurewebsites.net` | — |
| Live | https://app-porepolinetracker2.azurewebsites.net and https://app-porepolinetracker2.azurewebsites.net/diag | — |

## Repo-specific notes

### Exceptions to the standard (deliberate)

- **Sign-in is GitHub OAuth, not Microsoft.** The app reads the user's repositories with the user's GitHub token, so
  GitHub is the only provider that works (a Microsoft account has no repository access). The header shows the
  account's GitHub email (`user:email` scope) and falls back to the GitHub login when GitHub shares no email.
- **Azure names predate the naming rule.** Live: App Service `app-porepolinetracker2`, storage `stporepolinetracker2`,
  plan `asp-porepolinetracker-f1` (F1 Free, West US 2, no Always On), resource group `PoRepoLineTracker`; Key Vault
  `kv-poshared2` and App Insights are shared in `PoShared`. Renaming means recreating the site and the storage
  account and moving the GitHub OAuth callback URL, so `infra/main.bicepparam` describes the live names.
- **Navigation.** A left sidebar on wide screens and a bottom bar on phones (the version stamp sits above it through
  `--po-stamp-lift` in `app.css`).
- `SCREENSHOTS/` holds before/after UI mockups (owner's rule below) and is referenced from README.md.

### Documents

- `README.md`: what the app is, live URL, local run, deploy.
- `docs/spec.md`: contracts, boundaries and load-bearing edge cases. Read §8 "Boundaries" and §10 "Load-bearing
  behaviour and edge cases" before changing behaviour.
- `docs/testing.md`: the four test tiers and how to run them.

### Commands

```powershell
docker compose up -d                                          # Azurite (tables) + Jaeger (OTLP, UI :16686)
dotnet build PoRepoLineTracker.slnx                           # warnings are errors (Directory.Build.props)
dotnet run --project src/PoRepoLineTracker.Api --launch-profile https   # https://localhost:5001, hosts the WASM client too

dotnet test tests/PoRepoLineTracker.Unit                      # 142 tests; CI runs it
dotnet test tests/PoRepoLineTracker.Integration               # 38 tests; CI runs it. Testcontainers Azurite when Docker runs, in-memory substitutes (with a message) when not
dotnet test tests/PoRepoLineTracker.E2EAPI                    # 25 tests; needs the app running
dotnet test tests/PoRepoLineTracker.E2EUI                     # 21 tests; needs the app running + `playwright.ps1 install` once
# Both E2E tiers SKIP every test when nothing answers at E2E_BASE_URL (API default http://localhost:5002,
# UI default https://localhost:5003; the UI tier needs HTTPS because of upgrade-insecure-requests), e.g.
# ASPNETCORE_URLS=https://localhost:7207 KeyVault__Uri= dotnet run --project src/PoRepoLineTracker.Api --no-launch-profile
# then E2E_BASE_URL=https://localhost:7207 dotnet test tests/PoRepoLineTracker.E2EUI. A run that "passes" in seconds skipped.

dotnet test tests/PoRepoLineTracker.Unit --filter "FullyQualifiedName~FileIgnoreFilter"   # single class/test
```

- A running app locks the build (MSB3027). Stop it first: `Get-Process -Name PoRepoLineTracker.Api | Stop-Process -Force`.
- A build that worked yesterday can fail today with `NU1903` (a new NuGet security advisory, which warnings-as-errors turns into a failure). Fix it by bumping the version in `Directory.Packages.props` (central management with transitive pinning). Don't suppress it.
- Without Docker, run Azurite from npm (`azurite --silent --location <dir> --skipApiVersionCheck`). The Integration tier falls back to it automatically.
- Composition-root changes (`Program.cs`, `AddInfrastructure`, `AddAuth`, middleware order) only fail in the Integration tier, not Unit.
- Dev auth without GitHub: send `X-Fake-User: <guid>` (Development/Test only), or open `/auth/login?dev=true` in a browser. Writes also need the antiforgery pair; see `src/PoRepoLineTracker.Api/api-tests.http`.
- `POST /api/dev/seed/repository` writes synthetic chart history (Development only). Without it, E2EUI chart tests skip.

### Architecture

One deployable: `PoRepoLineTracker.Api` (ASP.NET Core 10 minimal APIs) serves the Blazor WebAssembly client (`PoRepoLineTracker.Client`, trimmed). `PoRepoLineTracker.Shared` holds domain types, DTOs, validators and the source-generated JSON context used on both sides.

- **API layout.** `Features/<Slice>/` holds vertical slices (endpoints + handler/query classes, injected directly; no MediatR). Slices must not reference each other. Shared code lives in `Storage/` (Azure Table Storage services and entities), `Analysis/` (git + line counting + code health), `Auth/`, `Middleware/`, `Telemetry/`. `Extensions/ApiEndpointExtensions.MapApiEndpoints` registers every slice.
- **Middleware order in `Program.cs` is load-bearing.** Static files come before auth, then `ApiNotFoundMiddleware` (404 for unmatched `/api` GETs, never a catch-all route), then authentication, authorization, rate limiting, `ProductionAuthEnforcementMiddleware`, and `AntiforgeryMiddleware` (after authorization, so unauthenticated writes get 401 rather than 400).
- **Analysis pipeline.** Bulk-add (`/api/repositories/bulk`, the only write path) and re-analyse enqueue `AnalyzeRepositoryCommits` on `AnalysisQueue` (a channel with two workers; never `Task.Run`). Everything git is the `git` CLI (no native library; libgit2 crashed on App Service Linux): `GitClient` clones/fetches into `repo_{id}` and runs `git log`, and `GitObjectReader` reads trees and blobs through one `git cat-file --batch`. `GitHubService` walks commits with per-tree memoization, `SourceLineCounter` + `FileIgnoreFilter` + `CommentSyntax` count lines, and `RepositoryDataService` writes per-commit snapshots. Progress goes through `AnalysisProgressService` to the SignalR hub `/hubs/analysis`. The client's fallback poll synthesises the same frames, so there is only one completion path. At startup `AnalysisQueue` re-queues unfinished work (rows never attempted, or carrying `AnalysisQueuedUtc`).
- **Single sources of truth.** Repository size is the newest commit's `TotalLines` snapshot, never a sum (`Shared/Domain/RepositoryTotals`). Streaks are in `CommitStreaks`, repo ownership checks in `Auth/RepositoryOwnership`, and config keys in `Shared/Models/ConfigKeys`.
- **Client.** Radzen.Blazor UI. Pages and their components are in `Features/<Feature>/`, layout in `Layout/`. Scoped `.razor.css` must be rooted at a plain `<div>`; use `::deep` for anything Radzen renders. Every wire type needs a `[JsonSerializable]` entry in `AppJsonSerializerContext`, otherwise trimming breaks it at runtime. The API builds with the trim/AOT analyzers and the Request Delegate Generator, so reflection-based JSON or binding is a build error there too; server-only payloads go in `ApiJsonContext`. The `vYYMMDD` label in the bottom-right corner (`Layout/BuildVersion`, `.po-version`, rendered once by `App.razor`) is the UTC date of the HEAD commit (the build date when git is absent), the `BuildVersion` `AssemblyMetadata` item that `Directory.Build.props` adds to every project (the only version key), so `/api/diag` and `/health/live` report the same value. It is separate from MinVer, which versions the assemblies from `v*` git tags.
- **Po shell (2026-11-18).** One header on every page, signed in or out (`Layout/PoHeader`, in `MainLayout` and in `BareLayout` above the sign-in card): the name top-left; top-right the signed-in account's email (GitHub's, via the `user:email` scope; the GitHub login when GitHub shares none), or Sign in, then the Settings gear (`data-testid=header-settings`). Sign out and the dev persona picker are in Settings' account card. No theme toggle and no drawer toggle: light and dark follow the device (`prefers-color-scheme` picks Radzen's material or material-dark sheet in `index.html`), the sidebar stays open on wide screens and phones use the bottom bar. `css/po-tokens.css` (tokens, header, `.po-link-btn`, version stamp, /diag cards and pills), `fonts/`, `js/po-devwindow.js`, `js/po-dev.js` and `po-dev.html` are this repo's own copies of the Po look; edit them here like any other file. `app.css` aliases this app's variables to the `--po-*` tokens (indigo accent, Inter text, Space Grotesk headings). `/diag` (`Features/Diag/Diag.razor`, title and h1 "System status") shows a plain-words banner, then API / Data / Services as `.po-diag__card`s with `.po-badge`s, from `GET /api/diag` (`DiagReportDto`). Tab while nothing has focus opens `/po-dev.html`, which reads `/health/live` (`X-Commit`) and `/api/diag`.
- **Effects.** Decoration lives in `wwwroot/js/backdrop.js`, `fx.js` (sound, particle overlay, SVG filters, pointer surfaces) and `city.js` (the Insights year view), styled by `effects.css`. Never on a data path: call them through interop in try/catch, and pass lists as comma-joined strings. The sound and effects switches are in Settings. See docs/spec.md §10 "Effects".
- **Config/secrets.** Key Vault secrets are prefixed `PoRepoLineTracker--` (`PrefixKeyVaultSecretManager`). Local overrides go in the gitignored `appsettings.Development.local.json` (copy the `.template`), which loads after Key Vault. Never use user-secrets. Optional features (App Insights, OTLP) are off when their key is absent, and the app must still boot.
- **Infra/CI.** Bicep is `infra/main.bicep` + `infra/main.bicepparam` (modules in `infra/modules/`), applied by hand (`az deployment sub what-if` first), never by CI. See "CI/CD and Azure" below.

### CI/CD and Azure

- `.github/workflows/deploy.yml` is the only workflow. A push to `master` checks formatting
  (`dotnet format --verify-no-changes`), builds Release (warnings are errors), runs the Unit tier (142 tests) and
  the Integration tier (38 tests, Testcontainers Azurite on the runner's Docker), publishes, deploys and waits for
  `/health/live` to report the pushed commit in `X-Commit`. A red check stops the deploy and the live site keeps its
  last good build. E2E tiers, Bicep (`scripts/ci-checks.ps1`) and `scripts/post-deploy-smoke.ps1` run locally.
  There is no infra job, rollback or PR build. Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`; the App Service name is `AZURE_WEBAPP_NAME`. Never move them to
  secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not
  added there is a bug: the next re-provision loses it.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value
  hard-coded as an expected result (read it from the same config the app reads).

### Owner's working rules

### Ponytail, lazy senior dev mode

You are a lazy senior developer. Lazy means efficient, not careless. The best code is the code never written.

Before writing any code, stop at the first rung that holds:

1. Does this need to be built at all? (YAGNI)
2. Does it already exist in this codebase? Reuse the helper, util, or pattern that's already here, don't re-write it.
3. Does the standard library already do this? Use it.
4. Does a native platform feature cover it? Use it.
5. Does an already-installed dependency solve it? Use it.
6. Can this be one line? Make it one line.
7. Only then: write the minimum code that works.

The ladder runs after you understand the problem, not instead of it: read the task and the code it touches, trace the real flow end to end, then climb.

Bug fix = root cause, not symptom: a report names a symptom. Grep every caller of the function you touch and fix the shared function once — one guard there is a smaller diff than one per caller, and patching only the path the ticket names leaves a sibling caller still broken.

Rules:

- No abstractions that weren't explicitly requested.
- No new dependency if it can be avoided.
- No boilerplate nobody asked for.
- Deletion over addition. Boring over clever. Fewest files possible.
- Shortest working diff wins, but only once you understand the problem. The smallest change in the wrong place isn't lazy, it's a second bug.
- Question complex requests: "Do you actually need X, or does Y cover it?"
- Pick the edge-case-correct option when two stdlib approaches are the same size, lazy means less code, not the flimsier algorithm.
- Mark deliberate simplifications that cut a real corner with a known ceiling (global lock, O(n²) scan, naive heuristic) with a `ponytail:` comment naming the ceiling and upgrade path.

Not lazy about: understanding the problem (read it fully and trace the real flow before picking a rung, a small diff you don't understand is just laziness dressed up as efficiency), input validation at trust boundaries, error handling that prevents data loss, security, accessibility, the calibration real hardware needs (the platform is never the spec ideal, a clock drifts, a sensor reads off), anything explicitly requested. Lazy code without its check is unfinished: non-trivial logic leaves ONE runnable check behind, the smallest thing that fails if the logic breaks (an assert-based demo/self-check or one small test file; no frameworks, no fixtures). Trivial one-liners need no test.

(Yes, this file also applies to agents working on the ponytail repo itself. Especially to them.)

### Project rules (NET_AGENTS)

These are the owner's standing rules for this repository. Where one conflicts with the ponytail text above, the rule here wins.

#### 1. Git & workflow

- **Stay on `master`:** commit directly to `master`. Create branches only when specifically asked.
- **Casual commits:** write short, natural commit messages using informal American phrasing. Auto-commit and push on sync.

#### 2. Running, testing & clean code

- **Run only related tests:** test only what was touched. Skip tests for minor edits; never run the whole test suite.
- **Zero warnings:** fix all build warnings immediately. Treat every warning as a failure.

#### 3. Architecture & setup

- **Keep docs updated:** check `docs/` and `README.md` first to get a quick overview of the app (if they exist).
- **Fully automated:** run all builds, scripts and setup commands directly. Do not ask the user to run terminal commands.

#### 4. UI & output rules

- **UI previews:** for visual changes, save before-and-after HTML mockups to `SCREENSHOTS/{YYMMDD}-{feature}.html`, plus a single annotated PNG showing the before and after with notes.
- **Clarifying questions:** if modifying more than 100 lines and no pending questions exist, list 3 key follow-up questions about future direction or logic.
- **Required footers:**
  - **Short recap:** if your response is over 100 words, add a recap under 20 words at the very end.
  - **3-level summary:** if a code change is over 20 lines, add 3 single-sentence summaries:
    - *Toddler:* explain the idea simply.
    - *Child:* explain what the app actually does now.
    - *Adult:* explain how it was built.
