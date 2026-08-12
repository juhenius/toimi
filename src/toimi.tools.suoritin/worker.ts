/// <reference lib="deno.worker" />
// Worker entry for one script execution. The host scopes this worker's Deno
// permissions to the request's `net`; everything else (fs, env, run,
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
  const { code, input, extract } = e.data;
  try {
    if (extract) {
      const host = new URL(extract.url).host;
      // Defense in depth: this worker's net permission already scopes every
      // fetch, so an out-of-allowlist callback would be denied by Deno anyway —
      // but refuse it explicitly so a mis-composed request (or a compromised
      // caller) fails with a clear error instead of a permission trace.
      if (
        Deno.permissions.querySync({ name: "net", host }).state !== "granted"
      ) {
        throw new Error(
          `extract callback host ${host} is not in the net allowlist`,
        );
      }
      // The URL arrives fully composed; this sandbox knows no route shapes
      // (counterpart: ExtractEndpoints.cs Route/TokenHeader/CallbackUrl).
      input.extract = async (
        prompt: string,
        text: string,
        schema?: unknown,
      ) => {
        const res = await fetch(extract.url, {
          method: "POST",
          headers: {
            "content-type": "application/json",
            "x-run-token": extract.token,
          },
          body: JSON.stringify({ prompt, text, schema }),
        });
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
