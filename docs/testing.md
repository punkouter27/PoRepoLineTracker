# Tests

Four tiers (counts as of 2026-11-21):

| Tier | Tests | What | Where it runs |
| --- | --- | --- | --- |
| `PoRepoLineTracker.Unit` | 151 | Pure logic, no I/O | CI + locally |
| `PoRepoLineTracker.Integration` | 40 | API and storage behaviour through the ASP.NET host | CI + locally |
| `PoRepoLineTracker.E2EAPI` | 25 | API contract against a running host | locally |
| `PoRepoLineTracker.E2EUI` | 26 | Playwright browser flows (mobile + desktop) | locally, against the trimmed Release publish (`pwsh scripts/run-e2e.ps1`) |

- **Integration** uses Testcontainers Azurite when Docker is running. Without Docker it prints
  "Docker is not running, so the Integration tier uses in-memory storage substitutes…" and runs against substitutes
  (a local Azurite on 127.0.0.1:10002 is used if one is listening).
- **E2E tiers** SKIP every test, with "No app instance reachable at <url>", when nothing answers at `E2E_BASE_URL`
  (API default http://localhost:5002, UI default https://localhost:5003; the UI tier needs HTTPS because of
  `upgrade-insecure-requests`). E2EUI also skips with "Playwright browser unavailable" until
  `playwright.ps1 install` has run once. A run that "passes" in seconds skipped.

Coverage is configured in [../tests/coverlet.runsettings](../tests/coverlet.runsettings) (Unit tier only) and
collected with `--collect:"XPlat Code Coverage"`. No threshold: it is informational, and CI does not collect it.

Authenticating in Integration/E2E: send `X-Fake-User` (and optionally `X-Fake-Roles`) and `FakeAuthHandler`
authenticates the request. It refuses to start in Production.

```powershell
dotnet test tests/PoRepoLineTracker.Unit
dotnet test tests/PoRepoLineTracker.Integration
# E2E: start the app first, e.g.
#   $env:ASPNETCORE_URLS='https://localhost:7207;http://localhost:7208'; $env:KeyVault__Uri=''
#   dotnet run --project src/PoRepoLineTracker.Api --no-launch-profile
$env:E2E_BASE_URL='http://localhost:7208';  dotnet test tests/PoRepoLineTracker.E2EAPI
$env:E2E_BASE_URL='https://localhost:7207'; dotnet test tests/PoRepoLineTracker.E2EUI
```
