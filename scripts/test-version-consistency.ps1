$ErrorActionPreference = "Stop"
& node "$PSScriptRoot\version-tools.mjs" check
if ($LASTEXITCODE -ne 0) { throw "版本一致性检查失败，退出码: $LASTEXITCODE" }
