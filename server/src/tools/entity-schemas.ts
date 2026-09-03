import { z } from "zod";

export const handle = z.string().regex(/^[0-9a-fA-F]{1,16}$/).refine(value => /[1-9a-fA-F]/.test(value), "Handle must be nonzero");
export const identity = {
  documentToken: z.string().uuid().describe("最近读取结果的文档标识 / Token from current document, selection or query result"),
  activeSpaceHandle: handle.describe("最近读取结果的活动空间句柄 / Active-space handle from the same result")
};

export const getEntityDetailsSchema = z.object({
  ...identity,
  handles: z.array(handle).min(1).max(2000).describe("当前空间顶层实体；块属性请传父块句柄 / Top-level entities; use parent block for attributes"),
  includeGeometry: z.boolean().default(true),
  includeAttributes: z.boolean().default(true),
  includeStyle: z.boolean().default(true)
}).strict();

export const setSelectionInputSchema = z.object({
  ...identity,
  mode: z.enum(["replace", "add", "remove", "clear"]).default("replace"),
  handles: z.array(handle).max(2000).default([]).describe("clear 留空；其余模式至少一个句柄 / Empty for clear; nonempty for other modes"),
  expectedCount: z.number().int().min(0).max(2000).optional().describe("校验去重后最终选择数量，不是输入数量 / Expected final selection count")
}).strict();

// Keep the exported MCP schema an object: this SDK does not export root ZodEffects schemas.
export const setSelectionSchema = setSelectionInputSchema.superRefine((value, ctx) => {
  if ((value.mode === "clear" && value.handles.length !== 0) || (value.mode !== "clear" && value.handles.length === 0))
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["handles"], message: "clear requires empty handles; other modes require at least one handle" });
});
