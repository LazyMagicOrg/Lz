# Build gate-checker Lambda zip package
# Requires: pip (Python 3.x), Docker (for psql extraction on first run)
# Output: Lambda/gate-checker.zip (~6MB)
#
# Run this script when handler.py, requirements.txt, or psql binaries change,
# then commit the updated gate-checker.zip.

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $scriptDir 'GateChecker'
$buildDir = Join-Path $sourceDir 'build'
$stageDir = Join-Path $buildDir 'stage'
$zipPath = Join-Path $scriptDir 'gate-checker.zip'

Write-Host "Building gate-checker Lambda package..."

# Clean stage
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

# Install pip dependencies
$requirements = Join-Path $sourceDir 'requirements.txt'
if (Test-Path $requirements) {
    Write-Host "  Installing pip dependencies..."
    pip install -r $requirements -t $stageDir --quiet
    if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
}

# Copy handler
Write-Host "  Copying handler.py..."
Copy-Item (Join-Path $sourceDir 'handler.py') $stageDir

# Ensure psql extracted (reuse existing setup script)
$binDir = Join-Path $sourceDir 'bin'
$libDir = Join-Path $sourceDir 'lib'
if (-not (Test-Path (Join-Path $binDir 'psql'))) {
    Write-Host "  Extracting psql via Docker..."
    $setupScript = Join-Path $sourceDir 'setup-psql.ps1'
    if (Test-Path $setupScript) {
        & $setupScript
    } else {
        Write-Warning "setup-psql.ps1 not found and psql binary missing. Lambda will deploy without DB restore support."
    }
}

# Copy bin/ and lib/ into stage
if (Test-Path $binDir) {
    Write-Host "  Bundling bin/ (psql, pg_restore)..."
    Copy-Item $binDir (Join-Path $stageDir 'bin') -Recurse
}
if (Test-Path $libDir) {
    Write-Host "  Bundling lib/ (shared libraries)..."
    Copy-Item $libDir (Join-Path $stageDir 'lib') -Recurse
}

# Create zip
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath

# Cleanup
Remove-Item $stageDir -Recurse -Force

$size = '{0:N1}' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "Built: $zipPath ($size MB)"
Write-Host "Commit this file to include it in the lz tool package."
