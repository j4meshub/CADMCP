import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { loadLiveSession, SessionError } from "../build/runtime/session.js";

function writeSession(root, overrides = {}) {
  const file = path.join(root, "CADMCP", "runtime", "session.json");
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ processId: process.pid, port: 8080, token: "a".repeat(43), protocolVersion: 1, startedAt: new Date().toISOString(), ...overrides }));
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
