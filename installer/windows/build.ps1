param([string]$IsccPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repoRoot 'src/Mail.Cli/Mail.Cli.csproj'
$publish = Join-Path $repoRoot 'artifacts/installer/publish'
if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else {
        $candidates = @(
            "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
            "$env:ProgramFiles/Inno Setup 6/ISCC.exe",
            "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw @'
Inno Setup compiler not found. Install it once with:
  winget install --id JRSoftware.InnoSetup -e -s winget
Then run this script again. For a custom installation, use:
  ./installer/windows/build.ps1 -IsccPath 'C:/path/to/ISCC.exe'
'@
}
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported version: $version" }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
& (Join-Path $publish 'Mail.Cli.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Published CLI smoke check failed.' }
& $IsccPath "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot 'mailang.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Host "Installer: $repoRoot/artifacts/installer/mailang-$version-windows-x64-setup.exe"
