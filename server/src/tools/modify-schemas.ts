import { z } from "zod";
import { handle, identity } from "./entity-schemas.js";

const finite = z.number().finite();
const point = z.object({ x: finite, y: finite, z: finite.default(0) }).strict();
const source = z.discriminatedUnion("kind", [
  z.object({ kind: z.literal("handles"), handles: z.array(handle).min(1).max(2000) }).strict(),
  z.object({ kind: z.literal("selection") }).strict()
]).describe("明确句柄或调用开始时的完整预选集；不接受查询 / Handles or initial selection; no inline query");
const common = {
  ...identity, source,
  expectedCount: z.number().int().min(1).max(2000).describe("必填：去重后顶层目标数 / Required distinct top-level target count"),
  coordinateSystem: z.enum(["ucs", "wcs"]).default("ucs"),
  dryRun: z.boolean().default(false).describe("true 只校验并估算范围，不写入、不生成句柄 / Validate and estimate only")
};
export const cloneEntitiesSchema = z.object({
  ...common,
  displacement: point.describe("毫米位移向量，不叠加 UCS 原点 / Displacement vector in mm"),
  selectCreated: z.boolean().default(false).describe("提交后选中新副本；选择失败不撤销复制 / Select copies after commit")
}).strict();
export const transformEntitiesInputSchema = z.object({
  ...common,
  operation: z.discriminatedUnion("type", [
    z.object({ type: z.literal("move"), displacement: point }).strict(),
    z.object({ type: z.literal("rotate"), basePoint: point, angle: finite.describe("弧度，绕输入坐标系 Z 轴 / Radians around input Z") }).strict(),
    z.object({ type: z.literal("scale"), basePoint: point, factor: finite.positive() }).strict(),
    z.object({ type: z.literal("mirror"), axisStart: point, axisEnd: point }).strict()
  ])
}).strict();
// Refine at invocation, keeping the root MCP schema a plain ZodObject for SDK export.
export const transformEntitiesSchema = transformEntitiesInputSchema.superRefine(({ operation }, ctx) => {
  if (operation.type !== "mirror") return;
  const { axisStart: a, axisEnd: b } = operation;
  if (a.z !== b.z || (a.x === b.x && a.y === b.y))
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["operation"], message: "Mirror axis must have equal Z and distinct XY points" });
});
