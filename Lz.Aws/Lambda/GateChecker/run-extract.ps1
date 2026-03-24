$ErrorActionPreference = "Stop"
$gcDir = 'C:\Users\TimothyMay\repos\_lz\Lz.Aws\Lambda\GateChecker'
Set-Location $gcDir

# Clean
if (Test-Path bin) { Remove-Item bin -Recurse -Force }
if (Test-Path lib) { Remove-Item lib -Recurse -Force }
New-Item -ItemType Directory -Path bin -Force | Out-Null
New-Item -ItemType Directory -Path lib -Force | Out-Null

$binPath = (Resolve-Path bin).Path
$libPath = (Resolve-Path lib).Path
$scriptPath = (Resolve-Path extract.sh).Path

Write-Host "bin: $binPath"
Write-Host "lib: $libPath"
Write-Host "script: $scriptPath"

docker run --rm --platform linux/amd64 `
    -v "${binPath}:/out/bin" `
    -v "${libPath}:/out/lib" `
    -v "${scriptPath}:/extract.sh:ro" `
    amazonlinux:2023 bash /extract.sh

if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit 1
}

$psqlBytes = [System.IO.File]::ReadAllBytes("$binPath\psql")
$arch = [BitConverter]::ToUInt16($psqlBytes, 18)
Write-Host "psql architecture: $arch (62=x86_64, 183=aarch64)" -ForegroundColor Cyan
