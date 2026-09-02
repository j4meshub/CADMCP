param(
    [string]$ProductVersion,
    [int]$ProtocolVersion,
    [int]$SettingsSchemaVersion
)

$ErrorActionPreference = "Stop"
$arguments = @("$PSScriptRoot\version-tools.mjs", "sync")
if ($PSBoundParameters.ContainsKey("ProductVersion")) { $arguments += @("--product", $ProductVersion) }
if ($PSBoundParameters.ContainsKey("ProtocolVersion")) { $arguments += @("--protocol", $ProtocolVersion.ToString()) }
if ($PSBoundParameters.ContainsKey("SettingsSchemaVersion")) { $arguments += @("--settings-schema", $SettingsSchemaVersion.ToString()) }
if ($arguments.Count -eq 2) { throw "至少指定 ProductVersion、ProtocolVersion 或 SettingsSchemaVersion 之一。" }

& node @arguments
if ($LASTEXITCODE -ne 0) { throw "版本同步失败，退出码: $LASTEXITCODE" }
