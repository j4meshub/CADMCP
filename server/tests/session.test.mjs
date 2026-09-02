import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { BUILD_VERSION, loadLiveSession, PRODUCT_VERSION, PROTOCOL_VERSION, SessionError } from "../build/runtime/session.js";

function writeSession(root, overrides = {}) {
  const file = path.join(root, "CADMCP", "runtime", "session.json");
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ processId: process.pid, port: 8080, token: "a".repeat(43), protocolVersion: PROTOCOL_VERSION, productVersion: PRODUCT_VERSION, buildVersion: BUILD_VERSION, startedAt: new Date().toISOString(), ...overrides }));
  return file;
}

test("接受当前进程的有效会话", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cadmcp-session-"));
  assert.equal(loadLiveSession(writeSession(root)).processId, process.pid);
});

test("拒绝陈旧进程和协议版本错误", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cadmcp-session-"));
  assert.throws(() => loadLiveSession(writeSession(root, { processId: 2147483647 })), (error) => error instanceof SessionError && error.code === "stale_session");
  assert.throws(() => loadLiveSession(writeSession(root, { protocolVersion: 99 })), (error) => error instanceof SessionError && error.code === "protocol_mismatch");
});

test("拒绝产品版本错误但接受不同构建标识", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cadmcp-session-"));
  assert.throws(() => loadLiveSession(writeSession(root, { productVersion: "1.9.0" })), (error) => error instanceof SessionError && error.code === "version_mismatch");
  assert.equal(loadLiveSession(writeSession(root, { buildVersion: "2.0.0+differentsha" })).buildVersion, "2.0.0+differentsha");
});
