import assert from "node:assert/strict";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { CadRpcError, invokeCad } from "../build/runtime/cad-client.js";
import { encodeFrame, FrameDecoder } from "../build/protocol/framing.js";
import { BUILD_VERSION, PRODUCT_VERSION, PROTOCOL_VERSION } from "../build/runtime/session.js";

async function mockCad(onRequest, authOverride = {}) {
  const token = "t".repeat(43);
  const authMessages = [];
  const server = net.createServer((socket) => {
    const decoder = new FrameDecoder(); let authenticated = false;
    socket.on("data", async (chunk) => {
      for (const value of decoder.push(chunk)) {
        if (!authenticated) {
          authenticated = true;
          authMessages.push(value);
          socket.write(encodeFrame({ type: "authenticated", success: true, message: null, protocolVersion: PROTOCOL_VERSION, productVersion: PRODUCT_VERSION, buildVersion: BUILD_VERSION, ...authOverride }));
          continue;
        }
        await onRequest(value, socket);
      }
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port; const root = fs.mkdtempSync(path.join(os.tmpdir(), "cadmcp-client-")); const file = path.join(root, "CADMCP", "runtime", "session.json");
  fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify({ processId: process.pid, port, token, protocolVersion: PROTOCOL_VERSION, productVersion: PRODUCT_VERSION, buildVersion: BUILD_VERSION, startedAt: new Date().toISOString() }));
  process.env.LOCALAPPDATA = root;
  return { authMessages, close: () => new Promise((resolve) => server.close(resolve)) };
}

test("完成认证并收发 JSON-RPC", async () => {
  const host = await mockCad(async (request, socket) => socket.write(encodeFrame({ jsonrpc: "2.0", id: request.id, result: { success: true, echoed: request.params.message } })));
  try {
    assert.deepEqual(await invokeCad("say_hello", { message: "你好" }), { success: true, echoed: "你好" });
    assert.equal(host.authMessages[0].protocolVersion, PROTOCOL_VERSION);
    assert.equal(host.authMessages[0].productVersion, PRODUCT_VERSION);
    assert.equal(host.authMessages[0].buildVersion, BUILD_VERSION);
  } finally { await host.close(); }
});

test("超时返回可轮询 callId 且不强杀宿主连接", async () => {
  const host = await mockCad(async () => {}); const callId = "09d86e8c-56ef-4651-8ca5-262ebd7b7eaa";
  try { await assert.rejects(invokeCad("send_code_to_cad", { callId }, { timeoutMs: 20 }), (error) => error instanceof CadRpcError && error.code === "timeout_unknown" && error.data.callId === callId); } finally { await host.close(); }
});

test("本地执行槽冲突立即返回 cad_busy", async () => {
  const host = await mockCad(async (request, socket) => { await new Promise((resolve) => setTimeout(resolve, 60)); socket.write(encodeFrame({ jsonrpc: "2.0", id: request.id, result: { success: true } })); });
  try { const first = invokeCad("query_entities", {}); await new Promise((resolve) => setTimeout(resolve, 10)); await assert.rejects(invokeCad("create_line", {}), (error) => error instanceof CadRpcError && error.code === "cad_busy"); await first; } finally { await host.close(); }
});

test("拒绝认证失败", async () => {
  const host = await mockCad(async () => {}, { success: false, errorCode: "authentication_failed", message: "bad token" });
  try { await assert.rejects(invokeCad("say_hello", {}), (error) => error instanceof CadRpcError && error.code === "authentication_failed"); } finally { await host.close(); }
});

test("拒绝认证响应中的产品版本和协议版本不匹配", async () => {
  const productHost = await mockCad(async () => {}, { productVersion: "1.9.0" });
  try { await assert.rejects(invokeCad("say_hello", {}), (error) => error instanceof CadRpcError && error.code === "version_mismatch"); } finally { await productHost.close(); }
  const protocolHost = await mockCad(async () => {}, { protocolVersion: 99 });
  try { await assert.rejects(invokeCad("say_hello", {}), (error) => error instanceof CadRpcError && error.code === "protocol_mismatch"); } finally { await protocolHost.close(); }
});
