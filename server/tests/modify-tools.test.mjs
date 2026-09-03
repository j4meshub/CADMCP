import assert from "node:assert/strict";
import test from "node:test";
import { cloneEntitiesSchema, transformEntitiesSchema } from "../build/tools/modify-schemas.js";
const common = { documentToken: "08824584-33bb-426a-8c6a-9273ef129b6a", activeSpaceHandle: "2", source: { kind: "handles", handles: ["A", "00a"] }, expectedCount: 1 };
const p = { x: 0, y: 0 };
test("复制：双来源、必填数量、严格有限坐标与默认行为", () => {
  const input = { ...common, displacement: p };
  assert.equal(cloneEntitiesSchema.parse(input).dryRun, false);
  assert.equal(cloneEntitiesSchema.parse(input).selectCreated, false);
  assert.equal(cloneEntitiesSchema.parse({...input, source:{kind:"selection"}}).source.kind, "selection");
  for(const patch of [{expectedCount:undefined},{expectedCount:0},{expectedCount:2001},{source:{kind:"query"}},{source:{kind:"selection",handles:["A"]}},{source:{kind:"handles",handles:[]}},{source:{kind:"handles",handles:Array(2001).fill("A")}},{displacement:{x:Infinity,y:0}},{displacement:{x:NaN,y:0}},{operation:{type:"move"}},{coordinateSystem:"ocs"}])
    assert.equal(cloneEntitiesSchema.safeParse({...input,...patch}).success,false);
});
test("变换：四种判别类型、镜像平面与等比缩放限制", () => {
  for(const operation of [{type:"move",displacement:p},{type:"rotate",basePoint:p,angle:Math.PI},{type:"scale",basePoint:p,factor:2},{type:"mirror",axisStart:p,axisEnd:{x:1,y:0}}])
    assert.equal(transformEntitiesSchema.safeParse({...common,operation}).success,true);
  for(const operation of [{type:"scale",basePoint:p,factor:0},{type:"scale",basePoint:p,factor:-2},{type:"scale",basePoint:p,factor:Infinity},{type:"rotate",angle:1},{type:"mirror",axisStart:p,axisEnd:p},{type:"mirror",axisStart:p,axisEnd:{x:1,y:0,z:2}},{type:"move",displacement:p,angle:1},{type:"move",displacement:{x:0,y:0,w:1}}])
    assert.equal(transformEntitiesSchema.safeParse({...common,operation}).success,false);
});
