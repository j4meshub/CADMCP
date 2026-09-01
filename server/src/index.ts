#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";

const server = new McpServer({ name: "cadmcp-server", version: "1.0.0" });
registerTools(server);

server.connect(new StdioServerTransport()).then(() => {
  console.error("CADMCP Server 已启动");
}).catch((error: unknown) => {
  console.error("CADMCP Server 启动失败:", error);
  process.exit(1);
});
