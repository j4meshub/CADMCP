import net from "node:net";
import crypto from "node:crypto";
import { FrameDecoder, encodeFrame } from "../protocol/framing.js";
import { BUILD_VERSION, loadLiveSession, PRODUCT_VERSION, PROTOCOL_VERSION, SessionError } from "./session.js";

export interface InvokeOptions { timeoutMs?: number; bypassSlot?: boolean }

export class CadRpcError extends Error {
  constructor(public readonly code: string, message: string, public readonly data?: unknown) {
    super(message);
    this.name = "CadRpcError";
  }
}

let slotBusy = false;

export async function invokeCad(method: string, parameters: Record<string, unknown>, options: InvokeOptions = {}): Promise<unknown> {
  const callId = typeof parameters.callId === "string" ? parameters.callId : crypto.randomUUID();
  const params = { ...parameters, callId };
  if (!options.bypassSlot && slotBusy) throw new CadRpcError("cad_busy", "已有 CAD 调用正在执行，请稍后重试");
  if (!options.bypassSlot) slotBusy = true;
  try {
    return await invokeOnce(method, params, options.timeoutMs ?? 300_000);
  } catch (error) {
    if (error instanceof CadRpcError || error instanceof SessionError) throw error;
    throw new CadRpcError("transport_error", (error as Error).message);
  } finally {
    if (!options.bypassSlot) slotBusy = false;
  }
}

function invokeOnce(method: string, params: Record<string, unknown>, timeoutMs: number): Promise<unknown> {
  const session = loadLiveSession();
  const id = crypto.randomUUID();
  const callId = String(params.callId);
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: "127.0.0.1", port: session.port });
    const decoder = new FrameDecoder();
    let authenticated = false;
    let settled = false;
    const finish = (error?: Error, value?: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      error ? reject(error) : resolve(value);
    };
    const timer = setTimeout(() => finish(new CadRpcError("timeout_unknown", "CAD 调用已超过等待时限，执行结果未知", { callId })), timeoutMs);
    socket.once("connect", () => socket.write(encodeFrame({
      type: "authenticate",
      protocolVersion: PROTOCOL_VERSION,
      productVersion: PRODUCT_VERSION,
      buildVersion: BUILD_VERSION,
      token: session.token
    })));
    socket.on("data", (chunk) => {
      try {
        for (const message of decoder.push(chunk)) {
          const value = message as Record<string, unknown>;
          if (!authenticated) {
            if (value.type !== "authenticated" || value.success !== true) {
              finish(new CadRpcError(String(value.errorCode ?? "authentication_failed"), String(value.message ?? "认证失败"), value));
              return;
            }
            if (value.protocolVersion !== PROTOCOL_VERSION) {
              finish(new CadRpcError("protocol_mismatch", `协议版本不匹配: 插件 ${String(value.protocolVersion)}，Server ${PROTOCOL_VERSION}`, value));
              return;
            }
            if (value.productVersion !== PRODUCT_VERSION) {
              finish(new CadRpcError("version_mismatch", `产品版本不匹配: 插件 ${String(value.productVersion)}，Server ${PRODUCT_VERSION}`, value));
              return;
            }
            authenticated = true;
            socket.write(encodeFrame({ jsonrpc: "2.0", id, method, params }));
            continue;
          }
          if (value.id !== id) continue;
          const rpcError = value.error as Record<string, unknown> | undefined;
          if (rpcError) finish(new CadRpcError(String(rpcError.code ?? "cad_error"), String(rpcError.message ?? "CAD 调用失败"), rpcError.data));
          else finish(undefined, value.result);
        }
      } catch (error) { finish(error as Error); }
    });
    socket.once("error", (error) => finish(new CadRpcError("service_unavailable", `无法连接 CADMCP: ${error.message}`)));
    socket.once("close", () => { if (!settled) finish(new CadRpcError("connection_closed", "CADMCP 连接提前关闭")); });
  });
}

export function errorResult(error: unknown): Record<string, unknown> {
  if (error instanceof CadRpcError) {
    const data = error.data && typeof error.data === "object" ? error.data as Record<string, unknown> : {};
    return { callId: data.callId ?? null, status: error.code === "timeout_unknown" ? "timeout_unknown" : "failed", stage: "transport", success: false, result: null, diagnostics: [], warnings: [], errorCode: error.code, errorType: error.name, message: error.message, stackTrace: null, durationMs: null, transactionMode: null, committed: false, rolledBack: false, undoGuaranteed: false };
  }
  const code = error instanceof SessionError ? error.code : "internal_error";
  const message = error instanceof Error ? error.message : String(error);
  return { callId: null, status: "failed", stage: "transport", success: false, result: null, diagnostics: [], warnings: [], errorCode: code, errorType: error instanceof Error ? error.name : null, message, stackTrace: null, durationMs: null, transactionMode: null, committed: false, rolledBack: false, undoGuaranteed: false };
}
