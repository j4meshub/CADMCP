import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const versionFile = path.join(repoRoot, "version.json");

function fail(message) {
  console.error(`Version check failed: ${message}`);
  process.exitCode = 1;
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function writeJson(file, value, indent = 2) {
  fs.writeFileSync(file, `${JSON.stringify(value, null, indent)}\n`, "utf8");
}

function validateVersionInfo(value) {
  if (!value || typeof value !== "object") throw new Error("version.json must contain an object");
  if (!/^\d+\.\d+\.\d+$/.test(value.productVersion)) throw new Error("productVersion must use MAJOR.MINOR.PATCH");
  if (!Number.isInteger(value.protocolVersion) || value.protocolVersion < 1) throw new Error("protocolVersion must be a positive integer");
  if (!Number.isInteger(value.settingsSchemaVersion) || value.settingsSchemaVersion < 1) throw new Error("settingsSchemaVersion must be a positive integer");
}

function expectedProps(info) {
  return `<!-- Generated from version.json by scripts/set-version.ps1. Do not edit manually. -->\n<Project>\n  <PropertyGroup>\n    <Version>${info.productVersion}</Version>\n    <AssemblyVersion>${info.productVersion}.0</AssemblyVersion>\n    <FileVersion>${info.productVersion}.0</FileVersion>\n    <GenerateAssemblyInformationalVersionAttribute>false</GenerateAssemblyInformationalVersionAttribute>\n    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>\n  </PropertyGroup>\n</Project>\n`;
}

function expectedCs(info) {
  return `// Generated from version.json by scripts/set-version.ps1. Do not edit manually.\nusing System.Reflection;\n\nnamespace CADMCP.Plugin;\n\npublic static class CadMcpVersion\n{\n    public const string ProductVersion = "${info.productVersion}";\n    public const int ProtocolVersion = ${info.protocolVersion};\n    public const int SettingsSchemaVersion = ${info.settingsSchemaVersion};\n\n    public static string BuildVersion =>\n        typeof(CadMcpVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion\n        ?? ProductVersion;\n}\n`;
}

function updatePackageVersions(info) {
  for (const relative of ["server/package.json", "server/package-lock.json"]) {
    const file = path.join(repoRoot, relative);
    const json = readJson(file);
    json.version = info.productVersion;
    if (relative.endsWith("package-lock.json") && json.packages?.[""]) json.packages[""].version = info.productVersion;
    writeJson(file, json, 4);
  }
}

function updateBundle(info) {
  const file = path.join(repoRoot, "bundle", "PackageContents.xml");
  let xml = fs.readFileSync(file, "utf8");
  xml = xml.replace(/AppVersion="[^"]+"/, `AppVersion="${info.productVersion}"`);
  xml = xml.replace(/(<ComponentEntry\b[^>]*\bVersion=")[^"]+("[^>]*>)/, `$1${info.productVersion}$2`);
  fs.writeFileSync(file, xml, "utf8");
}

function sync(args) {
  const info = readJson(versionFile);
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index];
    const raw = args[index + 1];
    if (raw === undefined) throw new Error(`missing value for ${flag}`);
    if (flag === "--product") info.productVersion = raw;
    else if (flag === "--protocol") info.protocolVersion = Number(raw);
    else if (flag === "--settings-schema") info.settingsSchemaVersion = Number(raw);
    else throw new Error(`unknown option: ${flag}`);
  }
  if (args.length === 0) throw new Error("at least one version option is required");
  validateVersionInfo(info);
  writeJson(versionFile, info);
  fs.writeFileSync(path.join(repoRoot, "Directory.Build.props"), expectedProps(info), "utf8");
  fs.writeFileSync(path.join(repoRoot, "plugin", "VersionInfo.cs"), expectedCs(info), "utf8");
  updatePackageVersions(info);
  updateBundle(info);
  console.log(`Synchronized CADMCP ${info.productVersion} / protocol ${info.protocolVersion} / settings schema ${info.settingsSchemaVersion}`);
}

function check() {
  const info = readJson(versionFile);
  validateVersionInfo(info);
  const comparisons = [
    ["Directory.Build.props", expectedProps(info)],
    ["plugin/VersionInfo.cs", expectedCs(info)]
  ];
  for (const [relative, expected] of comparisons) {
    const actual = fs.readFileSync(path.join(repoRoot, relative), "utf8").replace(/\r\n/g, "\n");
    if (actual !== expected) fail(`${relative} is not synchronized; run scripts/set-version.ps1`);
  }
  const packageJson = readJson(path.join(repoRoot, "server", "package.json"));
  const packageLock = readJson(path.join(repoRoot, "server", "package-lock.json"));
  if (packageJson.version !== info.productVersion) fail("server/package.json product version differs");
  if (packageLock.version !== info.productVersion || packageLock.packages?.[""]?.version !== info.productVersion) fail("server/package-lock.json root version differs");
  const xml = fs.readFileSync(path.join(repoRoot, "bundle", "PackageContents.xml"), "utf8");
  if (!xml.includes(`AppVersion="${info.productVersion}"`)) fail("bundle AppVersion differs");
  if (!new RegExp(`<ComponentEntry\\b[^>]*\\bVersion="${info.productVersion.replaceAll(".", "\\.")}"`).test(xml)) fail("bundle component version differs");
  if (process.exitCode !== 1) console.log(`Version consistency OK: ${info.productVersion} / protocol ${info.protocolVersion} / settings schema ${info.settingsSchemaVersion}`);
}

try {
  const [command, ...args] = process.argv.slice(2);
  if (command === "sync") sync(args);
  else if (command === "check") check();
  else throw new Error("usage: version-tools.mjs <sync|check> [options]");
} catch (error) {
  fail(error instanceof Error ? error.message : String(error));
}
