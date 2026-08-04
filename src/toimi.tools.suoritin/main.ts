// NOTE: the process must run with --deny-import (see deno.json tasks) — the
// Dockerfile ENTRYPOINT must carry it too. Worker-level permissions do not
// govern remote module imports; the host process's import permission does.
import { execute } from "./executor.ts";

const MAX_BODY_BYTES = 1024 * 1024;
const MAX_CONCURRENT = 4;
let inFlight = 0;

function isStringArray(v: unknown): v is string[] {
  return Array.isArray(v) && v.every((x) => typeof x === "string");
}

// Returns an error message, or null when the optional fields are well-typed.
// An explicit JSON null counts as absent (== null covers both), matching the
// executor's `??` defaults — .NET serializers send optional fields as null.
function validateOptionalFields(p: Record<string, unknown>): string | null {
  if (p.timeoutMs != null && typeof p.timeoutMs !== "number") {
    return "'timeoutMs' must be a number";
  }
  if (p.allowedHosts != null && !isStringArray(p.allowedHosts)) {
    return "'allowedHosts' must be an array of strings";
  }
  if (p.grants != null && !isStringArray(p.grants)) {
    return "'grants' must be an array of strings";
  }
  if (p.callbackUrl != null) {
    if (typeof p.callbackUrl !== "string" || !URL.canParse(p.callbackUrl)) {
      return "'callbackUrl' must be a valid URL";
    }
  }
  return null;
}

export async function handler(req: Request): Promise<Response> {
  const url = new URL(req.url);
  if (req.method === "GET" && url.pathname === "/health") {
    return Response.json({ status: "ok" });
  }
  if (req.method === "POST" && url.pathname === "/execute") {
    // Reject oversized payloads by declared size before reading the body …
    const declaredBytes = Number(req.headers.get("content-length"));
    if (declaredBytes > MAX_BODY_BYTES) {
      return new Response("payload too large", { status: 413 });
    }
    const body = await req.text();
    // … with a byte-accurate post-read fallback for chunked bodies.
    if (new TextEncoder().encode(body).length > MAX_BODY_BYTES) {
      return new Response("payload too large", { status: 413 });
    }
    let parsed;
    try {
      parsed = JSON.parse(body);
    } catch {
      return new Response("invalid JSON body", { status: 400 });
    }
    if (typeof parsed.code !== "string" || parsed.code.length === 0) {
      return new Response("'code' (string) is required", { status: 400 });
    }
    const fieldError = validateOptionalFields(parsed);
    if (fieldError !== null) {
      return new Response(fieldError, { status: 400 });
    }
    if (inFlight >= MAX_CONCURRENT) {
      return new Response("too many concurrent executions", { status: 429 });
    }
    inFlight++;
    try {
      return Response.json(await execute(parsed));
    } finally {
      inFlight--;
    }
  }
  return new Response("not found", { status: 404 });
}

if (import.meta.main) {
  Deno.serve({ port: 8080 }, handler);
}
