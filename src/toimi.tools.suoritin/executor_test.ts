import { assert, assertEquals, assertStringIncludes } from "@std/assert";
import {
  clampTimeout,
  DEFAULT_TIMEOUT_MS,
  execute,
  MAX_TIMEOUT_MS,
} from "./executor.ts";
import { MAX_LOG_CHARS, MAX_LOGS } from "./limits.ts";

Deno.test("runs a module and returns its effects", async () => {
  const r = await execute({
    code: `export default async function run(input) {
             return { setField: [{ path: "n", value: input.data.n + 1 }] };
           }`,
    input: { data: { n: 41 } },
  });
  assert(r.ok, r.error ?? "");
  assertEquals(r.effects, { setField: [{ path: "n", value: 42 }] });
  assert(r.stats.durationMs >= 0);
});

Deno.test("captures console output as logs", async () => {
  const r = await execute({
    code: `export default function run(input) {
             console.log("hello", { a: 1 });
             console.error("bad");
             return {};
           }`,
    input: {},
  });
  assert(r.ok);
  assertEquals(r.logs, ['[log] hello {"a":1}', "[error] bad"]);
});

Deno.test("null/undefined return means no effects", async () => {
  const r = await execute({
    code: `export default function run() {}`,
    input: {},
  });
  assert(r.ok);
  assertEquals(r.effects, {});
});

Deno.test("script throw returns ok:false with message and logs", async () => {
  const r = await execute({
    code:
      `export default function run() { console.log("before"); throw new Error("boom"); }`,
    input: {},
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "boom");
  assertEquals(r.logs, ["[log] before"]);
});

Deno.test("missing default export is an error", async () => {
  const r = await execute({ code: `export const x = 1;`, input: {} });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "default-export");
});

Deno.test("syntax error is an error, not a crash", async () => {
  const r = await execute({ code: `export default function( {`, input: {} });
  assertEquals(r.ok, false);
  assert(r.error !== null);
});

Deno.test("hung async script is terminated at the budget", async () => {
  const started = Date.now();
  const r = await execute({
    code:
      `export default async function run() { await new Promise(() => {}); }`,
    input: {},
    timeoutMs: 500,
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "budget");
  assert(Date.now() - started < 5000);
});

Deno.test("infinite CPU loop is terminated at the budget", async () => {
  // The case Jint could never hard-stop: worker.terminate() kills the isolate.
  const started = Date.now();
  const r = await execute({
    code: `export default function run() { while (true); }`,
    input: {},
    timeoutMs: 500,
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "budget");
  assert(Date.now() - started < 5000);
});

Deno.test("non-serializable effects are an error", async () => {
  const r = await execute({
    code: `export default function run() { return { f: () => 1 }; }`,
    input: {},
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "serializable");
});

Deno.test("fetch to a non-granted host is rejected", async () => {
  const r = await execute({
    code: `export default async function run() {
             const res = await fetch("http://example.com/");
             return { got: res.status };
           }`,
    input: {},
    net: [],
    timeoutMs: 5000,
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!.toLowerCase(), "net");
});

Deno.test("fetch to a granted host succeeds", async () => {
  const srv = Deno.serve(
    { port: 0, onListen: () => {} },
    () => Response.json({ temp: -3 }),
  );
  const host = `localhost:${srv.addr.port}`;
  try {
    const r = await execute({
      code: `export default async function run() {
               const res = await fetch("http://${host}/weather");
               const body = await res.json();
               return { mcpCall: [{ tool: "display_show", args: { temp: body.temp } }] };
             }`,
      input: {},
      net: [host],
      timeoutMs: 5000,
    });
    assert(r.ok, r.error ?? "");
    assertEquals(r.effects, {
      mcpCall: [{ tool: "display_show", args: { temp: -3 } }],
    });
  } finally {
    await srv.shutdown();
  }
});

Deno.test("extract() posts to the given URL with the run-token header", async () => {
  let seen: { path: string; token: string | null; body: unknown } | null = null;
  const srv = Deno.serve({ port: 0, onListen: () => {} }, async (req) => {
    seen = {
      path: new URL(req.url).pathname,
      token: req.headers.get("x-run-token"),
      body: await req.json(),
    };
    return Response.json({ price: 19.9 });
  });
  try {
    const host = `localhost:${srv.addr.port}`;
    const r = await execute({
      code: `export default async function run(input) {
               const out = await input.extract("get the price", "<html>19,90 €</html>", { type: "object" });
               return { setField: [{ path: "lastPrice", value: out.price }] };
             }`,
      input: {},
      net: [host],
      extract: { url: `http://${host}/internal/runs/extract`, token: "tok123" },
      timeoutMs: 5000,
    });
    assert(r.ok, r.error ?? "");
    assertEquals(r.effects, { setField: [{ path: "lastPrice", value: 19.9 }] });
    // The worker POSTed to the URL verbatim — the route shape is tietue's alone.
    assertEquals(seen!.path, "/internal/runs/extract");
    assertEquals(seen!.token, "tok123");
    assertEquals((seen!.body as { prompt: string }).prompt, "get the price");
  } finally {
    await srv.shutdown();
  }
});

Deno.test("extract() is absent without an extract grant", async () => {
  const r = await execute({
    code:
      `export default function run(input) { return { has: typeof input.extract }; }`,
    input: {},
  });
  assert(r.ok);
  assertEquals(r.effects, { has: "undefined" });
});

Deno.test("extract.url host outside the net allowlist is refused", async () => {
  const r = await execute({
    code: `export default async function run(input) {
             await input.extract("p", "t");
             return {};
           }`,
    input: {},
    net: [],
    extract: { url: "http://localhost:1/internal/runs/extract", token: "tok" },
    timeoutMs: 5000,
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "not in the net allowlist");
});

Deno.test("direct postMessage abuse is clamped host-side", async () => {
  // The script shares the worker global, so it can bypass worker.ts's caps by
  // calling self.postMessage itself; the host must re-clamp.
  const r = await execute({
    code: `export default function run() {
             const big = "y".repeat(10_000);
             self.postMessage({
               ok: true,
               effects: {},
               logs: Array.from({ length: 5000 }, () => big),
               error: null,
             });
             return new Promise(() => {}); // never post a second result
           }`,
    input: {},
    timeoutMs: 5000,
  });
  assert(r.ok, r.error ?? "");
  assert(r.logs.length <= MAX_LOGS, `logs length ${r.logs.length}`);
  for (const line of r.logs) {
    assert(line.length <= MAX_LOG_CHARS + 1, `log line length ${line.length}`);
  }
});

Deno.test("remote dynamic import from a script is rejected", async () => {
  // Import permission is governed by the HOST process (worker-level net/[]
  // does not close it) — the runner must be launched with --deny-import.
  const r = await execute({
    code: `export default async function run() {
             await import("https://esm.sh/canvas-confetti@1.9.3");
             return { imported: true };
           }`,
    input: {},
    timeoutMs: 5000,
  });
  assertEquals(r.ok, false);
});

Deno.test("effects byte cap is byte-accurate for multibyte payloads", async () => {
  // ~100k "€" chars stay under the cap in UTF-16 length but exceed 256KB of
  // UTF-8 bytes.
  const r = await execute({
    code:
      `export default function run() { return { big: "€".repeat(100_000) }; }`,
    input: {},
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "byte cap");
});

Deno.test("over-cap effects payload is rejected with logs preserved", async () => {
  const r = await execute({
    code: `export default function run() {
             console.log("about to return too much");
             return { big: "x".repeat(300_000) };
           }`,
    input: {},
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "byte cap");
  assertEquals(r.logs, ["[log] about to return too much"]);
});

Deno.test("a circular console argument does not kill the run", async () => {
  const r = await execute({
    code: `export default function run() {
             const o = {}; o.self = o;
             console.log(o);
             return { fine: 1 };
           }`,
    input: {},
  });
  assert(r.ok, r.error ?? "");
  assertEquals(r.effects, { fine: 1 });
  assertEquals(r.logs, ["[log] [object Object]"]);
});

Deno.test("clampTimeout applies default and max budget", () => {
  assertEquals(clampTimeout(undefined), DEFAULT_TIMEOUT_MS);
  assertEquals(clampTimeout(500), 500);
  assertEquals(clampTimeout(999_999_999), MAX_TIMEOUT_MS);
});
