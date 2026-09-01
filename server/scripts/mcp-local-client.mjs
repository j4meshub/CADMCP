#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const serverRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const defaultServer = resolve(serverRoot, "build", "index.js");
const nodeCommand = process.env.CADMCP_NODE || process.execPath;

function usage() {
  console.error(`用法：
  node scripts/mcp-local-client.mjs list
  node scripts/mcp-local-client.mjs call <toolName> [JSON对象或@JSON文件]

示例：
  node scripts/mcp-local-client.mjs call say_hello '{"message":"Codex 测试"}'
  node scripts/mcp-local-client.mjs call send_code_to_cad @request.json`);
}

async function parseArguments(value) {
  if (!value) return {};
  const text = value.startsWith("@")
    ? await readFile(resolve(process.cwd(), value.slice(1)), "utf8")
    : value.startsWith("base64:")
      ? Buffer.from(value.slice("base64:".length), "base64").toString("utf8")
      : value;
  const parsed = JSON.parse(text);
  if (!parsed || Array.isArray(parsed) || typeof parsed !== "object") {
    throw new Error("工具参数必须是 JSON 对象");
  }
  return parsed;
}

async function main() {
  const action = process.argv[2];
  if (action !== "list" && action !== "call") {
    usage();
    process.exitCode = 2;
    return;
  }

  const transport = new StdioClientTransport({
    command: nodeCommand,
    args: [process.env.CADMCP_SERVER_ENTRY || defaultServer],
    cwd: serverRoot,
    stderr: "pipe"
  });
  transport.stderr?.on("data", chunk => process.stderr.write(chunk));

  const client = new Client({ name: "cadmcp-codex-local-client", version: "1.0.0" });
  try {
    await client.connect(transport);
    if (action === "list") {
      const result = await client.listTools();
      process.stdout.write(JSON.stringify(result, null, 2) + "\n");
      return;
    }

    const toolName = process.argv[3];
    if (!toolName) throw new Error("call 操作缺少 toolName");
    const toolArguments = await parseArguments(process.argv[4]);
    const result = await client.callTool({ name: toolName, arguments: toolArguments });
    process.stdout.write(JSON.stringify(result, null, 2) + "\n");
    if (result.isError) process.exitCode = 1;
  } finally {
    await client.close();
  }
}

main().catch(error => {
  console.error(error instanceof Error ? error.stack || error.message : String(error));
  process.exitCode = 1;
});
