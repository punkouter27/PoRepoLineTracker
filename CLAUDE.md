# CLAUDE.md

@AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` (writing ruleset + owner's project rules; `.github/copilot-instructions.md` is a verbatim copy, so keep the two in sync) and `SPEC.md` (contracts, boundaries, load-bearing edge cases) are the source of truth. Read SPEC §8 "Boundaries" and §10 "Load-bearing behaviour and edge cases" before changing behaviour.

## Commands

```powershell
docker compose up -d                                          # Azurite (tables) + Jaeger (OTLP, UI :16686)
dotnet build PoRepoLineTracker.slnx                           # warnings are errors (Directory.Build.props)
dotnet run --project src/PoRepoLineTracker.Api --launch-profile https   # https://localhost:5001, hosts the WASM client too

dotnet test tests/PoRepoLineTracker.Unit                      # run before pushing; CI runs no tests (tier caps: 100 unit / 50 integration / 25 API / 25 UI)
dotnet test tests/PoRepoLineTracker.Integration               # needs Docker (Testcontainers/Azurite)
dotnet test tests/PoRepoLineTracker.E2EAPI                    # needs the app running
dotnet test tests/PoRepoLineTracker.E2EUI                     # needs the app running + `playwright.ps1 install` once
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

## Architecture

One deployable: `PoRepoLineTracker.Api` (ASP.NET Core 10 minimal APIs) serves the Blazor WebAssembly client (`PoRepoLineTracker.Client`, trimmed). `PoRepoLineTracker.Shared` holds domain types, DTOs, validators and the source-generated JSON context used on both sides.

- **API layout.** `Features/<Slice>/` holds vertical slices (endpoints + handler/query classes, injected directly; no MediatR). Slices must not reference each other. Shared code lives in `Storage/` (Azure Table Storage services and entities), `Analysis/` (git + line counting + code health), `Auth/`, `Middleware/`, `Telemetry/`. `Extensions/ApiEndpointExtensions.MapApiEndpoints` registers every slice.
- **Middleware order in `Program.cs` is load-bearing.** Static files come before auth, then `ApiNotFoundMiddleware` (404 for unmatched `/api` GETs, never a catch-all route), then authentication, authorization, rate limiting, `ProductionAuthEnforcementMiddleware`, and `AntiforgeryMiddleware` (after authorization, so unauthenticated writes get 401 rather than 400).
- **Analysis pipeline.** Bulk-add (`/api/repositories/bulk`, the only write path) and re-analyse enqueue `AnalyzeRepositoryCommits` on `AnalysisQueue` (a channel with two workers; never `Task.Run`). Everything git is the `git` CLI (no native library; libgit2 crashed on App Service Linux): `GitClient` clones/fetches into `repo_{id}` and runs `git log`, and `GitObjectReader` reads trees and blobs through one `git cat-file --batch`. `GitHubService` walks commits with per-tree memoization, `SourceLineCounter` + `FileIgnoreFilter` + `CommentSyntax` count lines, and `RepositoryDataService` writes per-commit snapshots. Progress goes through `AnalysisProgressService` to the SignalR hub `/hubs/analysis`. The client's fallback poll synthesises the same frames, so there is only one completion path. At startup `AnalysisQueue` re-queues unfinished work (rows never attempted, or carrying `AnalysisQueuedUtc`).
- **Single sources of truth.** Repository size is the newest commit's `TotalLines` snapshot, never a sum (`Shared/Domain/RepositoryTotals`). Streaks are in `CommitStreaks`, repo ownership checks in `Auth/RepositoryOwnership`, and config keys in `Shared/Models/ConfigKeys`.
- **Client.** Radzen.Blazor UI. Pages are in `Pages/`, feature components in `Components/Repositories/`, and reusable pieces in `Components/Shared/`. Scoped `.razor.css` must be rooted at a plain `<div>`; use `::deep` for anything Radzen renders. Every wire type needs a `[JsonSerializable]` entry in `AppJsonSerializerContext`, otherwise trimming breaks it at runtime. The API builds with the trim/AOT analyzers and the Request Delegate Generator, so reflection-based JSON or binding is a build error there too; server-only payloads go in `ApiJsonContext`. The `vYYMMDD` label in the bottom-right corner (`Layout/BuildVersion`, `.po-version`, rendered once by `App.razor`) is the UTC build date, a `BuildStamp` `AssemblyMetadata` item that `Directory.Build.props` adds to every project, so `/api/diag` reports the same value. It is separate from MinVer, which versions the assemblies from `v*` git tags.
- **Po shell (2026-11-18).** One header on every page, signed in or out (`Layout/PoHeader`, in `MainLayout` and in `BareLayout` above the sign-in card): the name top-left; top-right the signed-in account's email (GitHub's, via the `user:email` scope; the GitHub login when GitHub shares none), or Sign in, then the Settings gear (`data-testid=header-settings`). Sign out and the dev persona picker are in Settings' account card. No theme toggle and no drawer toggle: light and dark follow the device (`prefers-color-scheme` picks Radzen's material or material-dark sheet in `index.html`), the sidebar stays open on wide screens and phones use the bottom bar. `css/po-tokens.css`, `fonts/`, `js/po-devwindow.js`, `js/po-dev.js` and `po-dev.html` are the Po kit, byte-identical in every Po app: never edit them here; `app.css` aliases this app's variables to the `--po-*` tokens (indigo accent, Inter text, Space Grotesk headings). `/diag` shows API / Data / Services as `.po-diag__card`s with `.po-badge`s. Tab while nothing has focus opens `/po-dev.html`, which reads `/health/live` (`X-Commit`) and `/api/diag/status` (the same report as `/api/diag`).
- **Effects.** Decoration lives in `wwwroot/js/backdrop.js`, `fx.js` (sound, particle overlay, SVG filters, pointer surfaces) and `city.js` (the Insights year view), styled by `effects.css`. Never on a data path: call them through interop in try/catch, and pass lists as comma-joined strings. The sound and effects switches are in Settings. See SPEC §10 "Effects".
- **Config/secrets.** Key Vault secrets are prefixed `PoRepoLineTracker--` (`PrefixKeyVaultSecretManager`). Local overrides go in the gitignored `appsettings.Development.local.json` (copy the `.template`), which loads after Key Vault. Never use user-secrets. Optional features (App Insights, OTLP) are off when their key is absent, and the app must still boot.
- **Infra/CI.** Bicep files are in `infra/`, applied by hand (`az deployment`), never by CI. `.github/workflows/deploy.yml` (identical in every Po repo; this repo owns its copy) builds, publishes and deploys each push to `master`, then waits for `/health/live` to answer with the pushed commit. It runs no tests, no infra and no rollback, and has no PR runs: run the Unit tier, `scripts/ci-checks.ps1` and, after a deploy, `scripts/post-deploy-smoke.ps1` locally.
