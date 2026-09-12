param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Platform,
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$sdkPath = Split-Path -Parent $PSScriptRoot
$bundlePath = Join-Path $sdkPath 'native-bundles'
$exeName = if ($Platform.StartsWith('win-')) { 'Mail.Cli.exe' } else { 'Mail.Cli' }
if (-not (Test-Path -LiteralPath (Join-Path $publishPath $exeName) -PathType Leaf)) {
    throw "Missing $exeName in $publishPath. Use a self-contained dotnet publish output directory."
}
New-Item -ItemType Directory -Force -Path $bundlePath | Out-Null
$zipPath = Join-Path $bundlePath "$Platform.zip"
if (Test-Path -LiteralPath $zipPath) {
    throw "Bundle already exists: $zipPath. Remove that file explicitly before replacing it."
}
# Use a dedicated, clean publish directory: every file is shipped with the SDK.
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $zipPath)
Write-Output "Created $zipPath. Build with: mvn clean package -Pbundled-cli"
