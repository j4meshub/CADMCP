// Architecture guardrails, NOT a substitute for real AutoCAD undo acceptance.
import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

const source = file => fs.readFileSync(new URL(`../../${file}`, import.meta.url), "utf8");

test("动态能力优先：不引入固定实体限制或严格撤销门槛", () => {
  const code = source("commandset/DynamicCodeCommand.cs");
  assert.match(code, /ExecutionKind => CadExecutionKind\.CommandContext/);
  assert.doesNotMatch(code, /StartUndoMark|EndUndoMark|new UndoBoundary|StrictUndoBoundary\.Require|RequireNativeUndoBoundary|GetSystemVariable\("UNDOCTL"\)|ModifyTargetGuard/);
  assert.match(code, /mode == "auto"/);
  assert.match(code, /Database\.TransactionManager\.StartTransaction\(\)/);
  assert.match(code, /new CadExecutionContext\(commandContext\.Document, null,/);
  assert.match(code, /serialized, watch\.ElapsedMilliseconds, mode, committed, false, warnings/);
  assert.match(code, /undo_not_guaranteed/);
  assert.match(code, /catch \(Exception error\) when \(committed\)/);
});

test("四个创建工具使用原生严格撤销且在提交前物化响应", () => {
  const code = source("commandset/DrawingCommands.cs");
  assert.match(code, /ExecutionKind => CadExecutionKind\.FixedWrite/);
  for (const name of ["CreateLineCommand", "CreateCircleCommand", "CreatePolylineCommand", "CreateTextCommand"])
    assert.match(code, new RegExp(`${name} : AtomicCreateCommand`));
  assert.match(code, /StrictUndoBoundary\.Require\(context\)/);
  assert.doesNotMatch(code, /StartUndoMark|EndUndoMark|new UndoBoundary/);
  assert.ok(code.indexOf("response = ExecutionResponses.Success") < code.indexOf("tr.Commit()"));
  assert.ok(code.indexOf("Encoding.UTF8.GetByteCount") < code.indexOf("tr.Commit()"));
  assert.match(code, /if \(committed && response != null\)/);
});
