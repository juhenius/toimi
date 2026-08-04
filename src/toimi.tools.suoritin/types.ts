export interface ExecuteRequest {
  code: string;
  input: Record<string, unknown>;
  timeoutMs?: number;
  allowedHosts?: string[];
  grants?: string[];
  runToken?: string;
  callbackUrl?: string;
}

export interface ExecuteResult {
  ok: boolean;
  effects: Record<string, unknown> | null;
  logs: string[];
  error: string | null;
  stats: { durationMs: number };
}
