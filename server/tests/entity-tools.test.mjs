import assert from "node:assert/strict";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { registerTools } from "../build/tools/register.js";
import { getEntityDetailsSchema, setSelectionSchema } from "../build/tools/entity-schemas.js";
import { encodeFrame, FrameDecoder } from "../build/protocol/framing.js";
import { BUILD_VERSION, PRODUCT_VERSION, PROTOCOL_VERSION } from "../build/runtime/session.js";

const identity = { documentToken: "08824584-33bb-426a-8c6a-9273ef129b6a", activeSpaceHandle: "1F" };

test("详情参数必填身份、设置默认值并限制句柄", () => {
  const parsed = getEntityDetailsSchema.parse({ ...identity, handles: ["a", "00A"] });
  assert.equal(parsed.includeGeometry, true); assert.equal(parsed.includeAttributes, true); assert.equal(parsed.includeStyle, true);
  for (const handles of [[], ["0"], ["0x10"], ["xyz"], ["1".repeat(17)], [1], Array(2001).fill("A")])
    assert.equal(getEntityDetailsSchema.safeParse({ ...identity, handles }).success, false);
  assert.equal(getEntityDetailsSchema.safeParse({ handles: ["A"] }).success, false);
  assert.equal(getEntityDetailsSchema.safeParse({ ...identity, handles: ["A"], unknown: true }).success, false);
  assert.equal(getEntityDetailsSchema.safeParse({ ...identity, handles: ["A"], includeGeometry: false }).success, true);
});

test("选择模式、最终数量以及严格参数校验", () => {
  assert.equal(setSelectionSchema.parse({ ...identity, handles: ["A"] }).mode, "replace");
  assert.deepEqual(setSelectionSchema.parse({ ...identity, mode: "clear" }).handles, []);
  for (const args of [
    { mode: "clear", handles: ["A"] }, { mode: "add" }, { mode: "remove", handles: [] }, { mode: "other", handles: ["A"] },
    { handles: ["A"], expectedCount: -1 }, { handles: ["A"], expectedCount: 1.5 }, { handles: ["A"], expectedCount: 2001 }, { handles: ["A"], extraneous: true }
  ]) assert.equal(setSelectionSchema.safeParse({ ...identity, ...args }).success, false);
});

test("真实 MCP 注册、Schema 拒绝、默认参数转发、工具禁用与业务错误返回", async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cadmcp-tools-"));
  const previousEnv = process.env.LOCALAPPDATA;
  const received = []; let errorCode = null;
  const host = net.createServer(socket => {
    const decoder = new FrameDecoder(); let authenticated = false;
    socket.on("data", chunk => {
      for (const value of decoder.push(chunk)) {
        if (!authenticated) {
          authenticated = true;
          socket.write(encodeFrame({ type: "authenticated", success: true, protocolVersion: PROTOCOL_VERSION, productVersion: PRODUCT_VERSION, buildVersion: BUILD_VERSION }));
        } else {
          received.push(value);
          socket.write(encodeFrame({ jsonrpc: "2.0", id: value.id, result: { success: errorCode === null, errorCode, result: value.params } }));
        }
      }
    });
  });
  await new Promise(resolve => host.listen(0, "127.0.0.1", resolve));
  const dir = path.join(root, "CADMCP", "runtime"); fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, "session.json"), JSON.stringify({ processId: process.pid, port: host.address().port, token: "t".repeat(43), protocolVersion: PROTOCOL_VERSION, productVersion: PRODUCT_VERSION, buildVersion: BUILD_VERSION, startedAt: new Date().toISOString() }));
  process.env.LOCALAPPDATA = root;
  const server = new McpServer({ name: "test-server", version: PRODUCT_VERSION }); registerTools(server);
  const client = new Client({ name: "test-client", version: PRODUCT_VERSION });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  try {
    await server.connect(serverTransport); await client.connect(clientTransport);
    const list = await client.listTools(); assert.equal(list.tools.length, 12);
    for (const name of ["get_entity_details", "set_selection"]) {
      const schema = list.tools.find(tool => tool.name === name).inputSchema;
      assert.ok(schema.required.includes("documentToken")); assert.ok(schema.required.includes("activeSpaceHandle"));
    }
    const invalid = await client.callTool({ name: "set_selection", arguments: { ...identity, mode: "clear", handles: ["A"] } });
    assert.equal(invalid.isError, true); assert.equal(received.length, 0);
    const details = await client.callTool({ name: "get_entity_details", arguments: { ...identity, handles: ["A"] } });
    assert.equal(JSON.parse(details.content[0].text).success, true);
    assert.equal(received[0].params.includeAttributes, true); assert.equal(received[0].method, "get_entity_details");
    assert.equal(received[0].params.documentToken, identity.documentToken);
    assert.match(received[0].params.callId, /^[a-f0-9-]{36}$/);
    for (const code of ["tool_disabled", "document_mismatch", "active_space_mismatch", "count_mismatch", "entity_not_found", "cad_busy"]) {
      errorCode = code;
      const response = await client.callTool({ name: "set_selection", arguments: { ...identity, mode: "clear", expectedCount: 0 } });
      assert.equal(JSON.parse(response.content[0].text).errorCode, code);
    }
  } finally {
    await client.close(); await server.close();
    await new Promise(resolve => host.close(resolve));
    if (previousEnv === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = previousEnv;
    fs.rmSync(root, { recursive: true, force: true });
  }
});
