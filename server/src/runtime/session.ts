import fs from "node:fs";
import path from "node:path";

export const PROTOCOL_VERSION = 1;

export interface SessionInfo {
  processId: number;
  port: number;
  token: string;
  protocolVersion: number;
  startedAt: string;
}

export class SessionError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
    this.name = "SessionError";
  }
}

export function getSessionPath(): string {
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) throw new SessionError("runtime_path_unavailable", "LOCALAPPDATA 未定义");
  return path.join(localAppData, "CADMCP", "runtime", "session.json");
}

export function loadLiveSession(sessionPath = getSessionPath()): SessionInfo {
  let parsed: unknown;
  try {
    parsed = JSON.parse(fs.readFileSync(sessionPath, "utf8"));
  } catch (error) {
    throw new SessionError("service_unavailable", `未找到可用 CADMCP 会话: ${(error as Error).message}`);
  }
  if (!isSessionInfo(parsed)) throw new SessionError("invalid_session", "session.json 格式无效");
  if (parsed.protocolVersion !== PROTOCOL_VERSION) {
    throw new SessionError("protocol_mismatch", `协议版本不匹配: ${parsed.protocolVersion}`);
  }
  if (!isProcessAlive(parsed.processId)) {
    throw new SessionError("stale_session", `AutoCAD 进程 ${parsed.processId} 已不存在`);
  }
  return parsed;
}

function isSessionInfo(value: unknown): value is SessionInfo {
  if (!value || typeof value !== "object") return false;
  const v = value as Record<string, unknown>;
  return Number.isInteger(v.processId) && Number.isInteger(v.port) &&
    (v.port as number) >= 1024 && (v.port as number) <= 65535 &&
    typeof v.token === "string" && (v.token as string).length >= 43 &&
    Number.isInteger(v.protocolVersion) && typeof v.startedAt === "string";
}

function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
