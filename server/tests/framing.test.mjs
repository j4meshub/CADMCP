import assert from "node:assert/strict";
import test from "node:test";
import { FrameDecoder, FrameProtocolError, MAX_FRAME_BYTES, encodeFrame } from "../build/protocol/framing.js";

test("拆包后还原多行源码", () => {
  const frame = encodeFrame({ code: "var x = 1;\nreturn x;", text: "中文" });
  const decoder = new FrameDecoder();
  assert.deepEqual(decoder.push(frame.subarray(0, 3)), []);
  assert.deepEqual(decoder.push(frame.subarray(3, 9)), []);
  assert.deepEqual(decoder.push(frame.subarray(9)), [{ code: "var x = 1;\nreturn x;", text: "中文" }]);
});

test("粘包一次解析多个消息", () => {
  const decoder = new FrameDecoder();
  assert.deepEqual(decoder.push(Buffer.concat([encodeFrame({ a: 1 }), encodeFrame({ b: 2 })])), [{ a: 1 }, { b: 2 }]);
});

test("拒绝零长度和超大帧", () => {
  const zero = Buffer.alloc(4);
  assert.throws(() => new FrameDecoder().push(zero), FrameProtocolError);
  const huge = Buffer.alloc(4); huge.writeUInt32BE(MAX_FRAME_BYTES + 1);
  assert.throws(() => new FrameDecoder().push(huge), FrameProtocolError);
});
