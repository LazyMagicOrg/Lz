<#
.SYNOPSIS
    Extracts psql binary and shared libraries from Amazon Linux 2023 via Docker.
    Run this once before deploying the gate-checker Lambda with database restore support.

.DESCRIPTION
    The gate-checker Lambda can restore a database.sql dump file placed on EFS
    alongside Default.zip. This requires a psql binary compiled for Amazon Linux 2023
    (the Lambda runtime OS). This script uses Docker to extract psql and its
    library dependencies into bin/ and lib/ directories, which are then bundled
    into the Lambda zip by the build process.

.NOTES
    Requires Docker Desktop running with Linux containers.
    Only needs to be run once (or when upgrading PostgreSQL version).
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir = Join-Path $scriptDir "bin"
$libDir = Join-Path $scriptDir "lib"

Write-Host "Extracting psql from Amazon Linux 2023 via Docker..." -ForegroundColor Cyan
Write-Host ""

# Clean previous artifacts
if (Test-Path $binDir) { Remove-Item -Recurse -Force $binDir }
if (Test-Path $libDir) { Remove-Item -Recurse -Force $libDir }
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
New-Item -ItemType Directory -Path $libDir -Force | Out-Null

# Bash script as a single-line && chain. Avoids the CRLF-pollution that
# bites multi-line PowerShell here-strings when they're passed to a Linux
# bash via `docker run ... bash -c`. Each `&&` carries the failure mode
# of `set -e` without needing a separate statement.
$bashScript = "dnf install -y postgresql15 > /dev/null 2>&1 && " +
              "cp /usr/bin/psql /out/bin/ && " +
              "chmod +x /out/bin/psql && " +
              "for lib in `$(ldd /usr/bin/psql | grep '=> /' | awk '{print `$3}'); do cp `"`$lib`" /out/lib/ 2>/dev/null || true; done && " +
              "echo psql:`$(psql --version) && " +
              "echo libs:`$(ls /out/lib/ | wc -l)"

# Run Docker container to install postgresql15 and extract binaries
docker run --rm --platform linux/amd64 `
    -v "${binDir}:/out/bin" `
    -v "${libDir}:/out/lib" `
    amazonlinux:2023 `
    bash -c $bashScript

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ERROR: Docker extraction failed." -ForegroundColor Red
    Write-Host "Make sure Docker Desktop is running with Linux containers." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Setup complete!" -ForegroundColor Green
Write-Host "  bin/psql  - PostgreSQL client binary (Amazon Linux 2023)" -ForegroundColor White
Write-Host "  lib/      - Shared libraries for psql" -ForegroundColor White
Write-Host ""
Write-Host "These will be bundled into the Lambda package on next 'lz deployfoundation'." -ForegroundColor Yellow
