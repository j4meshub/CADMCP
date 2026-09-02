import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import { BUILD_VERSION, PRODUCT_VERSION, PROTOCOL_VERSION, SETTINGS_SCHEMA_VERSION } from "../build/generated/build-info.js";

const rootVersion = JSON.parse(fs.readFileSync(new URL("../../version.json", import.meta.url), "utf8"));
const packageVersion = JSON.parse(fs.readFileSync(new URL("../package.json", import.meta.url), "utf8"));

test("构建版本与唯一版本源一致", () => {
  assert.equal(PRODUCT_VERSION, rootVersion.productVersion);
  assert.equal(PROTOCOL_VERSION, rootVersion.protocolVersion);
  assert.equal(SETTINGS_SCHEMA_VERSION, rootVersion.settingsSchemaVersion);
  assert.equal(packageVersion.version, PRODUCT_VERSION);
  assert.match(BUILD_VERSION, new RegExp(`^${PRODUCT_VERSION.replaceAll(".", "\\.")}\\+(?:[0-9a-f]{12}|unknown)(?:\\.dirty)?$`));
});
