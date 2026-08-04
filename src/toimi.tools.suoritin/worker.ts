/// <reference lib="deno.worker" />
// Worker entry for one script execution. The host scopes this worker's Deno
// permissions to the script's allowedHosts; everything else (fs, env, run,
// ffi) is denied at spawn. The script is imported as an ES module from a
// data: URL and must default-export a function (input) => effects.

import { MAX_LOG_CHARS, MAX_LOGS } from "./limits.ts";

const logs: string[] = [];

function capture(level: string, args: unknown[]) {
  if (logs.length >= MAX_LOGS) return;
  const line = `[${level}] ` + args
    .map((a) => {
      if (typeof a === "string") return a;
      try {
        return JSON.stringify(a) ?? String(a);
      } catch {
        return String(a); // circular or otherwise unstringifiable argument
      }
    })
    .join(" ");
  logs.push(
    line.length > MAX_LOG_CHARS ? line.slice(0, MAX_LOG_CHARS) + "…" : line,
  );
}
console.log = (...a: unknown[]) => capture("log", a);
console.warn = (...a: unknown[]) => capture("warn", a);
console.error = (...a: unknown[]) => capture("error", a);
console.info = (...a: unknown[]) => capture("info", a);
console.debug = (...a: unknown[]) => capture("debug", a);

function toDataUrl(code: string): string {
  const bytes = new TextEncoder().encode(code);
  let bin = "";
  const chunk = 0x8000; // avoid arg-spread stack overflow on large sources
  for (let i = 0; i < bytes.length; i += chunk) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return "data:text/javascript;base64," + btoa(bin);
}

function post(
  msg: { ok: boolean; effects: unknown; logs: string[]; error: string | null },
) {
  try {
    self.postMessage(msg);
  } catch (err) {
    // Effects were not structured-cloneable (functions, circular refs, ...).
    self.postMessage({
      ok: false,
      effects: null,
      logs,
      error: `effects not serializable: ${err}`,
    });
  }
}

self.onmessage = async (e: MessageEvent) => {
  const { code, input, callbackUrl, runToken, grants } = e.data;
  try {
    if ((grants ?? []).includes("llm") && callbackUrl && runToken) {
      input.extract = async (
        prompt: string,
        text: string,
        schema?: unknown,
      ) => {
        const res = await fetch(
          `${callbackUrl}/internal/runs/${runToken}/extract`,
          {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ prompt, text, schema }),
          },
        );
        if (!res.ok) {
          throw new Error(`extract failed: ${res.status} ${await res.text()}`);
        }
        return await res.json();
      };
    }
    const mod = await import(toDataUrl(code));
    if (typeof mod.default !== "function") {
      throw new Error("script must default-export a function(input)");
    }
    const effects = await mod.default(input) ?? {};
    post({ ok: true, effects, logs, error: null });
  } catch (err) {
    post({
      ok: false,
      effects: null,
      logs,
      error: String((err as Error)?.message ?? err),
    });
  }
};
