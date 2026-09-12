# Build the platform-specific Python wheel for Windows x64.
#
# Run from the repository root (the directory containing Mail.slnx):
#   .\sdk\python\scripts\build_wheel.ps1
#
# Output: sdk/python/dist/mail_runtime-0.2.0-*-win_amd64.whl

param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$sdkRoot    = Join-Path $repoRoot "sdk/python"
$cliProject = Join-Path $repoRoot "src/Mail.Cli/Mail.Cli.csproj"
$binDir     = Join-Path $sdkRoot "mail_runtime/_bin"
$publishOut = Join-Path $repoRoot "obj/publish/$Rid"

Write-Host "==> Publishing CLI ($Rid) ..."
dotnet publish $cliProject `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishOut

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "==> Copying executable to _bin/ ..."
$exeName = "Mail.Cli.exe"
$src  = Join-Path $publishOut $exeName
$dest = Join-Path $binDir $exeName

if (-not (Test-Path $src)) {
    throw "Published executable not found at $src"
}
Copy-Item $src $dest -Force

Write-Host "==> Building Python wheel ..."
Push-Location $sdkRoot
try {
    python -m build --wheel
    if ($LASTEXITCODE -ne 0) { throw "python -m build failed." }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "Wheel written to: $sdkRoot/dist/"
Get-ChildItem (Join-Path $sdkRoot "dist/*.whl") | ForEach-Object { Write-Host "  $_" }
