// Wire contract with tietue (counterpart: SuoritinRequest/SuoritinResult in
// src/toimi.tools.tietue/Scripts/SuoritinClient.cs). The request carries only
// what this sandbox enforces: code, input, a timeout, the exact net allowlist,
// and — when tietue granted llm — the extract callback. Capability names
// (setField / mcp:<tool> / llm) never cross this seam: tietue composes `net`
// and `extract` from them and interprets the returned effects against them.
export interface ExtractGrant {
  /** Full callback endpoint. Composed by tietue (ExtractEndpoints.cs owns the route shape). */
  url: string;
  /** One-run token, sent back as the X-Run-Token header. */
  token: string;
}

export interface ExecuteRequest {
  code: string;
  input: Record<string, unknown>;
  timeoutMs?: number;
  net?: string[];
  extract?: ExtractGrant;
}

export interface ExecuteResult {
  ok: boolean;
  effects: Record<string, unknown> | null;
  logs: string[];
  error: string | null;
  stats: { durationMs: number };
}
