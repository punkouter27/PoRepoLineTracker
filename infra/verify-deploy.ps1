# verify-deploy.ps1 — Smoke-test the live Azure deployment. The ONE copy of these checks: the
# deploy job in .github/workflows/deploy.yml runs this file after every deploy.
#
# /health answers 200, the sign-in page loads, a protected API route refuses an anonymous caller
# with 401, an unknown API route is a 404, and "/" never redirects off-origin. Each path has ONE
# acceptable status; anything else fails. /health is what Azure's own probe uses; the others prove
# the static shell and the auth layer are wired correctly — a /health that answers 200 from a
# half-deployed package would still 500 on the rest.
#
# Usage: .\infra\verify-deploy.ps1                 # one pass, from a developer machine
#        .\infra\verify-deploy.ps1 -Attempts 18    # CI: keep retrying through the container swap
#
# The target URL is hard-coded so a developer cannot accidentally point this at staging or a
# colleague's fork. Override $env:PoRepoLineTracker_VerifyUrl if you genuinely need to (CI does).

param(
    # Tries per probe, 10 s apart. Straight after a deploy the OLD process answers until App
    # Service swaps the container, and for the half-minute or more of that swap every route
    # answers 5xx — a single-shot check failed good deploys on exactly that. A wrong status that
    # persists for the whole window still fails.
    [int]$Attempts = 1
)

$ErrorActionPreference = 'Stop'

$Url = if ($env:PoRepoLineTracker_VerifyUrl) {
    $env:PoRepoLineTracker_VerifyUrl.TrimEnd('/')
} else {
    'https://app-porepolinetracker2.azurewebsites.net'
}

function Get-Probe([string]$Path) {
    try {
        Invoke-WebRequest -Uri "$Url$Path" -UseBasicParsing -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 20
    } catch {
        $null
    }
}

# /api/repositories and /api/diagnostics: 401 when anonymous — the API auth layer rejects, it
# does not redirect, so callers can tell "you need to log in" from "this URL is wrong". The
# unknown route must be 404 — not 401, and above all not 200 carrying the HTML shell. GET only:
# see the known limit documented on ApiNotFoundMiddleware.
$paths = @(
    @{ Path = '/health';                        Want = 200 },
    @{ Path = '/login';                         Want = 200 },
    @{ Path = '/api/repositories';              Want = 401 },
    @{ Path = '/api/diagnostics';               Want = 401 },
    @{ Path = '/api/no-such-route-smoke-test';  Want = 404 }
)

$failed = $false
foreach ($entry in $paths) {
    $code = 0
    for ($i = 1; $i -le $Attempts; $i++) {
        $code = [int](Get-Probe $entry.Path)?.StatusCode
        if ($code -eq $entry.Want) { break }
        if ($i -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    $marker = if ($code -eq $entry.Want) { 'OK ' } else { $failed = $true; 'BAD' }
    Write-Host ("{0} {1,-6} {2}{3}" -f $marker, $code, $Url, $entry.Path)
}

# An unauthenticated page must land on THIS origin's login page, not at the OAuth provider — the
# installed PWA starts at "/" and would otherwise leave its own scope.
$location = [string](Get-Probe '/')?.Headers.Location
if ($location -and $location -notlike '/*' -and $location -notlike "$Url*") {
    Write-Host "BAD /      redirected off-origin to $location"
    $failed = $true
} else {
    Write-Host "OK  /      $(if ($location) { "-> $location" } else { 'served' })"
}

if ($failed) {
    Write-Host "::error::verify-deploy.ps1: one or more probes failed against $Url"
    exit 1
}

Write-Host "`nverify-deploy.ps1: deployment is alive at $Url" -ForegroundColor Green
