import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { CadRpcError, errorResult, invokeCad } from "../runtime/cad-client.js";
import { getEntityDetailsSchema, setSelectionInputSchema, setSelectionSchema } from "./entity-schemas.js";

const point = z.object({ x: z.number(), y: z.number(), z: z.number().optional().default(0) });
const coordinateSystem = z.enum(["ucs", "wcs"]).optional().default("ucs");
const color = z.union([
  z.object({ mode: z.literal("byLayer") }),
  z.object({ mode: z.literal("aci"), index: z.number().int().min(1).max(255) }),
  z.object({ mode: z.literal("rgb"), red: z.number().int().min(0).max(255), green: z.number().int().min(0).max(255), blue: z.number().int().min(0).max(255) })
]);
const properties = { layer: z.string().min(1).optional(), color: color.optional(), linetype: z.string().min(1).optional() };

function result(value: unknown) { return { content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }] }; }
function handler(method: string, options: { timeoutMs?: number; bypassSlot?: boolean } = {}) {
  return async (params: Record<string, unknown>) => {
    try { return result(await invokeCad(method, params, options)); }
    catch (error) { return result(errorResult(error)); }
  };
}

export function registerTools(server: McpServer) {
  server.registerTool("get_entity_details", {
    description: "按句柄读取当前空间实体详情，坐标 WCS、长度毫米；不修改 DWG / Read entity details in WCS/mm; requires document identity",
    inputSchema: getEntityDetailsSchema,
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }
  }, handler("get_entity_details"));
  server.registerTool("set_selection", {
    description: "精确设置预选集；先验证全部目标，失败保留原选择；不修改实体 / Set implied selection atomically; no DWG edits",
    inputSchema: setSelectionInputSchema,
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false }
  }, async params => {
    const parsed = setSelectionSchema.safeParse(params);
    if (!parsed.success) return { ...result(errorResult(new CadRpcError("invalid_parameters", parsed.error.message))), isError: true };
    return handler("set_selection")(parsed.data);
  });
  server.tool("say_hello", "向当前 AutoCAD 文档问好 / Say hello in the active drawing", { message: z.string().optional() }, handler("say_hello"));
  server.tool("get_current_document_info", "获取当前 DWG 与活动空间信息 / Get active DWG information", {}, handler("get_current_document_info"));
  server.tool("get_selected_entities", "读取当前预选实体 / Get implied-selection entities", { limit: z.number().int().min(1).max(2000).optional().default(200), includeGeometry: z.boolean().optional().default(false) }, handler("get_selected_entities"));
  server.tool("query_entities", "按类型、图层、颜色、线型和包围盒查询实体 / Query active-space entities", {
    entityTypes: z.array(z.string()).optional(), layers: z.array(z.string()).optional(), linetypes: z.array(z.string()).optional(), color: color.optional(),
    boundingBox: z.object({ min: point, max: point }).optional(), coordinateSystem, limit: z.number().int().min(1).max(2000).optional().default(200), includeGeometry: z.boolean().optional().default(false)
  }, handler("query_entities"));
  server.tool("send_code_to_cad", "在 AutoCAD 进程内动态编译并执行 C# / Compile and execute C# inside AutoCAD", {
    code: z.string().min(1), parameters: z.record(z.unknown()).optional().default({}), transactionMode: z.enum(["auto", "none"]).optional().default("auto"), usings: z.array(z.string()).optional(), references: z.array(z.string()).optional()
  }, handler("send_code_to_cad", { timeoutMs: 300_000 }));
  server.tool("get_execution_status", "查询超时调用的最终状态 / Poll a timed-out CAD call", { callId: z.string().uuid() }, handler("get_execution_status", { timeoutMs: 10_000, bypassSlot: true }));
  server.tool("create_line", "原子批量创建直线 / Atomically create lines", {
    items: z.array(z.object({ start: point, end: point, ...properties })).min(1), coordinateSystem
  }, handler("create_line"));
  server.tool("create_polyline", "原子批量创建二维轻量多段线 / Atomically create 2D lightweight polylines", {
    items: z.array(z.object({ vertices: z.array(z.object({ x: z.number(), y: z.number(), bulge: z.number().optional().default(0) })).min(2), closed: z.boolean().optional().default(false), ...properties })).min(1), coordinateSystem
  }, handler("create_polyline"));
  server.tool("create_circle", "原子批量创建圆 / Atomically create circles", {
    items: z.array(z.object({ center: point, radius: z.number().positive(), ...properties })).min(1), coordinateSystem
  }, handler("create_circle"));
  server.tool("create_text", "原子批量创建 MText / Atomically create MText", {
    items: z.array(z.object({ content: z.string(), position: point, height: z.number().positive(), width: z.number().nonnegative().optional().default(0), rotation: z.number().optional().default(0), attachment: z.enum(["topLeft", "topCenter", "topRight", "middleLeft", "middleCenter", "middleRight", "bottomLeft", "bottomCenter", "bottomRight"]).optional().default("topLeft"), ...properties })).min(1), coordinateSystem
  }, handler("create_text"));
}
