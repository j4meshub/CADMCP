import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const serverRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = path.resolve(serverRoot, "..");
const version = JSON.parse(fs.readFileSync(path.join(repoRoot, "version.json"), "utf8"));

function git(args) {
  try {
    return execFileSync("git", ["-C", repoRoot, ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return "";
  }
}

const gitSha = git(["rev-parse", "--short=12", "HEAD"]) || "unknown";
if (gitSha === "unknown" && process.env.CADMCP_REQUIRE_GIT_SHA === "1") {
  throw new Error("正式构建需要可用的 Git SHA");
}
const dirty = git(["status", "--porcelain", "--untracked-files=normal"]) !== "";
const buildVersion = `${version.productVersion}+${gitSha}${dirty ? ".dirty" : ""}`;
const outputDir = path.join(serverRoot, "src", "generated");
const output = path.join(outputDir, "build-info.ts");
fs.mkdirSync(outputDir, { recursive: true });
fs.writeFileSync(output, `// Generated at build time. Do not edit.\nexport const PRODUCT_VERSION = ${JSON.stringify(version.productVersion)};\nexport const PROTOCOL_VERSION = ${version.protocolVersion};\nexport const SETTINGS_SCHEMA_VERSION = ${version.settingsSchemaVersion};\nexport const GIT_SHA = ${JSON.stringify(gitSha)};\nexport const BUILD_VERSION = ${JSON.stringify(buildVersion)};\n`, "utf8");
console.log(`Generated CADMCP build identity ${buildVersion}`);
