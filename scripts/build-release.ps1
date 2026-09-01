param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "CADMCP.sln"
$bundle = Join-Path $repoRoot "artifacts\CADMCP.bundle"
$zip = Join-Path $repoRoot "artifacts\CADMCP-$Version.bundle.zip"
dotnet build $solution -c Release
if (-not (Test-Path $bundle)) { throw "Bundle 构建产物不存在: $bundle" }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created $zip"
