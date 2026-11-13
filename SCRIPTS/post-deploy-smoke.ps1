#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [ValidateRange(1, 60)][int]$Attempts = 6
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-Response([string]$Path) {
    try {
        Invoke-WebRequest -Uri "$BaseUrl$Path" -SkipHttpErrorCheck -MaximumRedirection 0 `
            -TimeoutSec 20 -ErrorAction Stop
    }
    catch { $null }
}

$failed = $false
foreach ($entry in @(
    @{ Path = '/login'; Want = 200 },
    @{ Path = '/api/repositories'; Want = 401 },
    @{ Path = '/api/diagnostics'; Want = 401 },
    @{ Path = '/api/no-such-route-smoke-test'; Want = 404 }
)) {
    $code = 0
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $response = Get-Response $entry.Path
        $code = if ($response) { [int]$response.StatusCode } else { 0 }
        if ($code -eq $entry.Want) { break }
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    $marker = if ($code -eq $entry.Want) { 'OK ' } else { $failed = $true; 'BAD' }
    Write-Host ("{0} {1,-6} {2}{3}" -f $marker, $code, $BaseUrl, $entry.Path)
}

$root = Get-Response '/'
$location = if ($root) { [string]$root.Headers.Location } else { '' }
if ($location -and $location -notlike '/*' -and $location -notlike "$BaseUrl*") {
    Write-Host "BAD /      redirected off-origin to $location"
    $failed = $true
}
else {
    Write-Host "OK  /      $(if ($location) { "-> $location" } else { 'served' })"
}

if ($failed) { throw "Post-deploy smoke checks failed against $BaseUrl." }
Write-Host 'Post-deploy smoke tests passed.'
