import type { ExecuteRequest, ExecuteResult } from "./types.ts";
import { MAX_ERROR_CHARS, MAX_LOG_CHARS, MAX_LOGS } from "./limits.ts";

export const DEFAULT_TIMEOUT_MS = 20_000;
export const MAX_TIMEOUT_MS = 60_000;
export const MAX_EFFECTS_BYTES = 256 * 1024;

export function clampTimeout(timeoutMs: number | undefined): number {
  return Math.min(timeoutMs ?? DEFAULT_TIMEOUT_MS, MAX_TIMEOUT_MS);
}

function truncate(s: string, max: number): string {
  return s.length > max ? s.slice(0, max) + "…" : s;
}

// The worker message is attacker-influenced: the script shares the worker
// global and can call self.postMessage directly, bypassing worker.ts's caps.
// Coerce every field to the expected shape and re-apply the size caps.
function clamp(raw: unknown): Omit<ExecuteResult, "stats"> {
  const m = (typeof raw === "object" && raw !== null ? raw : {}) as Record<
    string,
    unknown
  >;
  const ok = m.ok === true;
  const error = typeof m.error === "string"
    ? truncate(m.error, MAX_ERROR_CHARS)
    : null;
  const logs = Array.isArray(m.logs)
    ? m.logs.slice(0, MAX_LOGS).map((l) =>
      truncate(typeof l === "string" ? l : String(l), MAX_LOG_CHARS)
    )
    : [];
  const effects = ok
    ? (typeof m.effects === "object" && m.effects !== null &&
        !Array.isArray(m.effects)
      ? m.effects as Record<string, unknown>
      : {})
    : null;
  return { ok, effects, logs, error };
}

export async function execute(req: ExecuteRequest): Promise<ExecuteResult> {
  const started = Date.now();
  const timeoutMs = clampTimeout(req.timeoutMs);

  // Net permission = exactly the request's `net`: tietue composes it (script
  // allowedHosts + the extract-callback host when llm is granted, see
  // ScriptHandler.BuildNet) — this side never widens it.
  const net = req.net ?? [];

  // Accepted residual risk: there is no per-worker V8 heap cap, so a script
  // can balloon memory until the pod limit; the k8s memory limit + restart is
  // the containment.
  const worker = new Worker(new URL("./worker.ts", import.meta.url).href, {
    type: "module",
    deno: {
      permissions: {
        net,
        read: false,
        write: false,
        env: false,
        run: false,
        ffi: false,
      },
    },
  });

  const partial = await new Promise<Omit<ExecuteResult, "stats">>((resolve) => {
    const timer = setTimeout(
      () =>
        // Accepted: logs are lost on timeout — they live in the terminated
        // isolate and a streaming redesign isn't warranted.
        resolve({
          ok: false,
          effects: null,
          logs: [],
          error: `script exceeded ${timeoutMs}ms budget`,
        }),
      timeoutMs,
    );
    worker.onmessage = (e) => {
      clearTimeout(timer);
      resolve(clamp(e.data));
    };
    worker.onerror = (e) => {
      // Uncaught error outside the worker's own try/catch (e.g. top-level in
      // module eval before onmessage). preventDefault stops host propagation.
      e.preventDefault();
      clearTimeout(timer);
      resolve({ ok: false, effects: null, logs: [], error: e.message });
    };
    worker.postMessage({
      code: req.code,
      input: req.input ?? {},
      extract: req.extract,
    });
  });
  worker.terminate(); // hard preemption — also the timeout path's actual stop

  if (
    partial.ok &&
    new TextEncoder().encode(JSON.stringify(partial.effects)).length >
      MAX_EFFECTS_BYTES // byte-accurate: UTF-8 bytes, not UTF-16 code units
    // counterpart: SuoritinClient.cs MaxEffectsBytes
  ) {
    return {
      ok: false,
      effects: null,
      logs: partial.logs,
      error: `effects payload exceeds ${MAX_EFFECTS_BYTES} byte cap`,
      stats: { durationMs: Date.now() - started },
    };
  }
  return { ...partial, stats: { durationMs: Date.now() - started } };
}
