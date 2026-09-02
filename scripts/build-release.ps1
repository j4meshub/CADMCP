param(
    [string]$Label,
    [switch]$Official
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $repoRoot "version.json") -Raw | ConvertFrom-Json
$version = $versionInfo.productVersion
if ($Label -and $Label -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') { throw "Label 只能包含字母、数字、点和连字符。" }
if ($Official -and $Label) { throw "正式构建不能使用 Label。" }
& "$PSScriptRoot\test-version-consistency.ps1"
if ($LASTEXITCODE -ne 0) { throw "版本一致性检查失败。" }
if ($Official) {
    $gitSha = (& git -C $repoRoot rev-parse --short=12 HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $gitSha) { throw "正式构建需要可用的 Git SHA。" }
    $changes = (& git -C $repoRoot status --porcelain --untracked-files=normal)
    if ($changes) { throw "正式构建要求干净的 Git 工作区。" }
}
$solution = Join-Path $repoRoot "CADMCP.sln"
$bundle = Join-Path $repoRoot "artifacts\CADMCP.bundle"
$artifactVersion = if ($Label) { "$version-$Label" } else { $version }
$zip = Join-Path $repoRoot "artifacts\CADMCP-$artifactVersion.bundle.zip"
dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw "CADMCP 构建失败，未生成发布包。退出码: $LASTEXITCODE" }
if (-not (Test-Path $bundle)) { throw "Bundle 构建产物不存在: $bundle" }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created $zip"
