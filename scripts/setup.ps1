# setup.ps1 — First-run setup for PoRepoLineTracker on a new machine.
# Run from the repo root: .\infra\setup.ps1
#
# This script:
#   1. Checks for and installs prerequisites via Winget (Docker, .NET 10 SDK, Azure CLI)
#   2. Initializes Docker and starts Azurite
#   3. Checks Azure CLI login status for Key Vault access
#   4. Kills any orphaned dotnet processes holding ports 5002/5003

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "=== PoRepoLineTracker First-Run Setup ===" -ForegroundColor Cyan

# ── 1. Prerequisites ──────────────────────────────────────────────────────
function Test-Command($Name) {
    $null = Get-Command $Name -ErrorAction SilentlyContinue
    return $?
}

# Check Winget
if (-not (Test-Command "winget")) {
    Write-Host "ERROR: Winget is not available. Install App Installer from the Microsoft Store." -ForegroundColor Red
    exit 1
}

# Check .NET 10 SDK
$dotnetVersion = dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or -not $dotnetVersion.StartsWith("10.")) {
    Write-Host "Installing .NET 10 SDK..." -ForegroundColor Yellow
    winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements
} else {
    Write-Host "OK: .NET SDK $dotnetVersion" -ForegroundColor Green
}

# Check Docker
if (-not (Test-Command "docker")) {
    Write-Host "Installing Docker Desktop..." -ForegroundColor Yellow
    winget install Docker.DockerDesktop --accept-package-agreements --accept-source-agreements
    Write-Host "Docker installed. Please start Docker Desktop and re-run this script." -ForegroundColor Yellow
    exit 0
} else {
    Write-Host "OK: Docker installed" -ForegroundColor Green
}

# Check Azure CLI
if (-not (Test-Command "az")) {
    Write-Host "Installing Azure CLI..." -ForegroundColor Yellow
    winget install Microsoft.AzureCLI --accept-package-agreements --accept-source-agreements
} else {
    Write-Host "OK: Azure CLI installed" -ForegroundColor Green
}

# ── 2. Docker + Azurite ───────────────────────────────────────────────────
Write-Host "`nStarting Docker services..." -ForegroundColor Cyan

# Ensure Docker is running. $LASTEXITCODE, not try/catch: a native command that exits non-zero
# does not throw, so the catch this used to rely on never ran and the script carried on to a
# confusing `docker compose` failure.
docker info 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker is not running. Please start Docker Desktop first." -ForegroundColor Red
    exit 1
}

# Start Azurite via docker compose
Push-Location $RepoRoot
try {
    docker compose up -d
    Write-Host "OK: Azurite started (ports 10000-10002) and Jaeger (UI on http://localhost:16686)" -ForegroundColor Green
} catch {
    Write-Host "WARNING: Failed to start Azurite: $_" -ForegroundColor Yellow
}
Pop-Location

# ── 3. Azure CLI Login Check ──────────────────────────────────────────────
Write-Host "`nChecking Azure CLI login status..." -ForegroundColor Cyan
$azAccount = az account show 2>$null | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $azAccount) {
    Write-Host "Not logged into Azure. Running 'az login'..." -ForegroundColor Yellow
    az login
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: Azure login failed. Key Vault access will not work." -ForegroundColor Yellow
    }
} else {
    Write-Host "OK: Logged in as $($azAccount.user.name) [subscription: $($azAccount.name)]" -ForegroundColor Green
}

# ── 4. Kill Orphaned dotnet Processes ─────────────────────────────────────
Write-Host "`nChecking for orphaned dotnet processes on ports 5002/5003..." -ForegroundColor Cyan
$orphaned = Get-NetTCPConnection -LocalPort 5002,5003 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
if ($orphaned) {
    # $pid would shadow the read-only automatic $PID variable and throw in PS7
    foreach ($procId in $orphaned) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq "dotnet") {
            Write-Host "  Killing orphaned dotnet process PID $procId" -ForegroundColor Yellow
            Stop-Process -Id $procId -Force
        }
    }
    Write-Host "OK: Orphaned processes cleared" -ForegroundColor Green
} else {
    Write-Host "OK: No orphaned dotnet processes found" -ForegroundColor Green
}

# ── Done ───────────────────────────────────────────────────────────────────
Write-Host "`n=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "Run the app with: dotnet run --project src/PoRepoLineTracker.Api --launch-profile https" -ForegroundColor White
Write-Host "API will be available at http://localhost:5002 / https://localhost:5003" -ForegroundColor White
