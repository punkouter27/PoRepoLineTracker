# Runs the two browser/API end-to-end tiers against the TRIMMED Release publish (the same `dotnet publish -c Release`
# output the deploy ships), then stops everything it started.
#
#   pwsh scripts/run-e2e.ps1                 # E2EAPI + E2EUI
#   pwsh scripts/run-e2e.ps1 -Tier E2EUI     # one tier
#
# .NET 10 bakes the WASM environment in at publish time, so the client is published as "Test" (same trimming as the
# deploy, but the dev-only test sign-in is allowed). The server runs as ASPNETCORE_ENVIRONMENT=Test on
# https://localhost:7207 (UI tier, HTTPS because of upgrade-insecure-requests) and http://localhost:7208 (API tier).
# Needs Docker (Azurite on :10002; started with `docker compose up -d azurite` and stopped again only if this script
# started it) and Playwright browsers (`pwsh tests/PoRepoLineTracker.E2EUI/bin/Release/net10.0/playwright.ps1 install`).
param(
    [ValidateSet('All', 'E2EAPI', 'E2EUI')] [string] $Tier = 'All',
    [string] $PublishDir = (Join-Path ([IO.Path]::GetTempPath()) 'porepolinetracker-e2e-publish')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet build PoRepoLineTracker.slnx -c Release
if ($LASTEXITCODE) { throw 'build failed' }
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish src/PoRepoLineTracker.Api -c Release -o $PublishDir -p:WasmApplicationEnvironmentName=Test
if ($LASTEXITCODE) { throw 'publish failed' }

$azuriteWasRunning = [bool](docker ps -q --filter 'name=^porepolinetracker-azurite$')
if (-not $azuriteWasRunning) { docker compose up -d azurite; if ($LASTEXITCODE) { throw 'could not start Azurite' } }

$env:ASPNETCORE_ENVIRONMENT = 'Test'
$env:ASPNETCORE_URLS = 'https://localhost:7207;http://localhost:7208'
$env:KeyVault__Uri = ''
$log = Join-Path ([IO.Path]::GetTempPath()) 'porepolinetracker-e2e-app.log'
$app = Start-Process dotnet -ArgumentList (Join-Path $PublishDir 'PoRepoLineTracker.Api.dll') -WorkingDirectory $PublishDir `
    -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
$failed = $false
try {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 1
        try { $ok = (Invoke-WebRequest http://localhost:7208/health/live -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $ok = $false }
    } until ($ok -or (Get-Date) -gt $deadline -or $app.HasExited)
    if (-not $ok) { throw "the published app did not answer on :7208 (log: $log)" }

    if ($Tier -in 'All', 'E2EAPI') {
        $env:E2E_BASE_URL = 'http://localhost:7208'
        dotnet test tests/PoRepoLineTracker.E2EAPI -c Release --no-build
        if ($LASTEXITCODE) { $failed = $true }
    }
    if ($Tier -in 'All', 'E2EUI') {
        $env:E2E_BASE_URL = 'https://localhost:7207'
        dotnet test tests/PoRepoLineTracker.E2EUI -c Release --no-build
        if ($LASTEXITCODE) { $failed = $true }
    }
}
finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    if (-not $azuriteWasRunning) { docker compose stop azurite | Out-Null; docker compose rm -f azurite | Out-Null }
    Remove-Item Env:E2E_BASE_URL -ErrorAction SilentlyContinue
}
if ($failed) { Write-Error 'an end-to-end tier failed' ; exit 1 }
Write-Host 'E2E tiers passed against the trimmed Release publish.'
