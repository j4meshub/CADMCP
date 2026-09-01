import { Buffer } from "node:buffer";

export const MAX_FRAME_BYTES = 8 * 1024 * 1024;

export class FrameProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "FrameProtocolError";
  }
}

export function encodeFrame(value: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(value), "utf8");
  if (payload.length > MAX_FRAME_BYTES) {
    throw new FrameProtocolError(`消息超过 ${MAX_FRAME_BYTES} 字节上限`);
  }
  const frame = Buffer.allocUnsafe(payload.length + 4);
  frame.writeUInt32BE(payload.length, 0);
  payload.copy(frame, 4);
  return frame;
}

export class FrameDecoder {
  private buffer: Buffer<ArrayBufferLike> = Buffer.alloc(0);

  push(chunk: Buffer): unknown[] {
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);
    const messages: unknown[] = [];
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32BE(0);
      if (length === 0 || length > MAX_FRAME_BYTES) {
        throw new FrameProtocolError(`非法帧长度: ${length}`);
      }
      if (this.buffer.length < length + 4) break;
      const json = this.buffer.subarray(4, length + 4).toString("utf8");
      this.buffer = this.buffer.subarray(length + 4);
      try {
        messages.push(JSON.parse(json));
      } catch (error) {
        throw new FrameProtocolError(`无效 JSON: ${(error as Error).message}`);
      }
    }
    return messages;
  }
}
