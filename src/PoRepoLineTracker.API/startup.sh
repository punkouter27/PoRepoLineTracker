#!/bin/bash
# Azure App Service startup script for PoRepoLineTracker.
#
# The built-in DOTNETCORE|10.0 runtime image ships without git, but GitClient shells out to
# the git CLI for clone/pull (LibGit2Sharp is used only for local object-store reads, because
# its native network stack SIGABRTs in this container). Without git, every analysis fails with
# "git is not installed or not in PATH".
#
# It is the App Service startup command; infra/resources.bicep sets that (appCommandLine).

# NOTE: deliberately NOT `set -e`. A failed apt-get must not stop the site from booting —
# a running site with broken analysis beats a container that never starts.

WWWROOT=/home/site/wwwroot

if ! command -v git &> /dev/null; then
    echo "[startup] git not found — installing via apt-get..."
    if apt-get update -qq && apt-get install -y --no-install-recommends git; then
        rm -rf /var/lib/apt/lists/*
        echo "[startup] git installed: $(git --version)"
    else
        echo "[startup] WARNING: git install FAILED. The site will start, but repository" >&2
        echo "[startup] analysis will report 'git is not installed or not in PATH'." >&2
    fi
else
    echo "[startup] git already available: $(git --version)"
fi

# Avoids 'dubious ownership' when a clone is owned by a different uid than the app process.
git config --global --add safe.directory '*' 2>/dev/null || true

DLL="$WWWROOT/PoRepoLineTracker.Api.dll"

if [ ! -f "$DLL" ]; then
    echo "[startup] FATAL: no PoRepoLineTracker entry assembly found in $WWWROOT" >&2
    ls -1 "$WWWROOT"/*.dll 2>/dev/null | head -20 >&2
    exit 1
fi

echo "[startup] Starting $DLL ..."
exec dotnet "$DLL"
