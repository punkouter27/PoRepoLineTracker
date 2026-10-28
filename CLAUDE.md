# CLAUDE.md

@AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` (writing ruleset + owner's project rules; `.github/copilot-instructions.md` is a verbatim copy, so keep the two in sync) and `SPEC.md` (contracts, boundaries, load-bearing edge cases) are the source of truth. Read SPEC §8 "Boundaries" and §10 "Edge cases" before changing behaviour; the dated "Delta" sections at the end of SPEC override earlier sections.

## Commands

```powershell
docker compose up -d                                          # Azurite (tables) + Jaeger (OTLP, UI :16686)
dotnet build PoRepoLineTracker.slnx                           # warnings are errors (Directory.Build.props)
dotnet run --project src/PoRepoLineTracker.API --launch-profile https   # https://localhost:5001, hosts the WASM client too

dotnet test tests/PoRepoLineTracker.Unit                      # the only CI gate
dotnet test tests/PoRepoLineTracker.Integration               # needs Docker (Testcontainers/Azurite)
dotnet test tests/PoRepoLineTracker.E2EAPI                    # needs the app running
dotnet test tests/PoRepoLineTracker.E2EUI                     # needs the app running + `playwright.ps1 install` once

dotnet test tests/PoRepoLineTracker.Unit --filter "FullyQualifiedName~FileIgnoreFilter"   # single class/test
```

- A running app locks the build (MSB3027). Stop it first: `Get-Process -Name PoRepoLineTracker.API | Stop-Process -Force`.
- A build that worked yesterday can fail today with `NU1903` (a new NuGet security advisory, which warnings-as-errors turns into a failure). Fix it by bumping the version in `Directory.Packages.props` (central management with transitive pinning). Don't suppress it.
- Without Docker, run Azurite from npm (`azurite --silent --location <dir> --skipApiVersionCheck`). The Integration tier falls back to it automatically.
- Composition-root changes (`Program.cs`, `AddInfrastructure`, `AddAuth`, middleware order) only fail in the Integration tier, not Unit.
- Dev auth without GitHub: send `X-Fake-User: <guid>` (Development/Test only), or open `/auth/login?dev=true` in a browser. Writes also need the antiforgery pair; see `src/PoRepoLineTracker.API/api-tests.http`.
- `POST /api/dev/seed/repository` writes synthetic chart history (Development only). Without it, E2EUI chart tests skip.

## Architecture

One deployable: `PoRepoLineTracker.API` (ASP.NET Core 10 minimal APIs) serves the Blazor WebAssembly client (`PoRepoLineTracker.Client`, trimmed). `PoRepoLineTracker.Shared` holds domain types, DTOs, validators and the source-generated JSON context used on both sides.

- **API layout.** `Features/<Slice>/` holds vertical slices (endpoints + handler/query classes, injected directly; no MediatR). Slices must not reference each other. Shared code lives in `Storage/` (Azure Table Storage services and entities), `Analysis/` (git + line counting + code health), `Auth/`, `Middleware/`, `Telemetry/`. `Extensions/ApiEndpointExtensions.MapApiEndpoints` registers every slice.
- **Middleware order in `Program.cs` is load-bearing.** Static files come before auth, then `ApiNotFoundMiddleware` (404 for unmatched `/api` GETs, never a catch-all route), then authentication, authorization, rate limiting, `ProductionAuthEnforcementMiddleware`, and `AntiforgeryMiddleware` (after authorization, so unauthenticated writes get 401 rather than 400).
- **Analysis pipeline.** Bulk-add (`/api/repositories/bulk`, the only write path) and re-analyse enqueue `AnalyzeRepositoryCommits` on `AnalysisQueue` (a channel with two workers; never `Task.Run`). `GitClient` shells out to the `git` CLI (libgit2 crashes on App Service Linux) to clone/pull into `repo_{id}`. `GitHubService` walks commits with per-tree memoization, `SourceLineCounter` + `FileIgnoreFilter` + `CommentSyntax` count lines, and `RepositoryDataService` writes per-commit snapshots. Progress goes through `AnalysisProgressService` to the SignalR hub `/hubs/analysis`. The client's fallback poll synthesises the same frames, so there is only one completion path. At startup `AnalysisQueue` re-queues unfinished work (rows never attempted, or carrying `AnalysisQueuedUtc`).
- **Single sources of truth.** Repository size is the newest commit's `TotalLines` snapshot, never a sum (`Shared/Domain/RepositoryTotals`). Streaks are in `CommitStreaks`, repo ownership checks in `Auth/RepositoryOwnership`, and config keys in `Shared/Models/ConfigKeys`.
- **Client.** Radzen.Blazor UI. Pages are in `Pages/`, feature components in `Components/Repositories/`, and reusable pieces in `Components/Shared/`. Scoped `.razor.css` must be rooted at a plain `<div>`; use `::deep` for anything Radzen renders. Every wire type needs a `[JsonSerializable]` entry in `AppJsonSerializerContext`, otherwise trimming breaks it at runtime. The `vYYMMDD` label in the bottom-right corner comes from an `AssemblyMetadata` `BuildVersion` item in the client csproj (the build date). It is separate from MinVer, which versions the assemblies from `v*` git tags.
- **Effects.** Decoration lives in `wwwroot/js/backdrop.js`, `fx.js` (sound, particle overlay, SVG filters, pointer surfaces) and `city.js` (Year in Review), styled by `effects.css`. Never on a data path: call them through interop in try/catch, and pass lists as comma-joined strings. See SPEC delta "GFX & sound".
- **Config/secrets.** Key Vault secrets are prefixed `PoRepoLineTracker--` (`PrefixKeyVaultSecretManager`). Local overrides go in the gitignored `appsettings.Development.local.json` (copy the `.template`), which loads after Key Vault. Never use user-secrets. Optional features (App Insights, OTLP) are off when their key is absent, and the app must still boot.
- **Infra/CI.** Bicep files are in `infra/`. `.github/workflows/deploy.yml` builds, runs Unit tests, and deploys to a Linux App Service on push to `master`; `infra/verify-deploy.ps1` smoke-tests the result.
