#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)

function Invoke-Check([string]$Name, [scriptblock]$Command) {
    Write-Host "::group::$Name"
    & $Command
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { throw "$Name failed (exit $code)." }
}

Invoke-Check 'Install Bicep CLI' { az bicep install }
foreach ($file in Get-ChildItem -LiteralPath infra -Filter '*.bicep' -File -Recurse) {
    Invoke-Check "Compile Bicep: $($file.Name)" {
        az bicep build --file $file.FullName --stdout > $null
    }
}
Write-Host 'ci-checks: all passed.'
