import { assert, assertEquals } from "@std/assert";
import { handler } from "./main.ts";

Deno.test("GET /health returns ok", async () => {
  const res = await handler(new Request("http://x/health"));
  assertEquals(res.status, 200);
  assertEquals((await res.json()).status, "ok");
});

Deno.test("unknown route is 404", async () => {
  const res = await handler(new Request("http://x/nope"));
  assertEquals(res.status, 404);
  await res.body?.cancel();
});

Deno.test("POST /execute runs code end-to-end", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({
        code:
          `export default function run(input) { return { echo: input.data.v }; }`,
        input: { data: { v: 7 } },
      }),
    }),
  );
  assertEquals(res.status, 200);
  const body = await res.json();
  assertEquals(body.ok, true);
  assertEquals(body.effects, { echo: 7 });
});

Deno.test("POST /execute rejects missing code", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({ input: {} }),
    }),
  );
  assertEquals(res.status, 400);
  await res.body?.cancel();
});

Deno.test("POST /execute rejects invalid JSON", async () => {
  const res = await handler(
    new Request("http://x/execute", { method: "POST", body: "{nope" }),
  );
  assertEquals(res.status, 400);
  await res.body?.cancel();
});

Deno.test("POST /execute rejects oversized body", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({ code: "x".repeat(1_100_000), input: {} }),
    }),
  );
  assertEquals(res.status, 413);
  await res.body?.cancel();
});

Deno.test("POST /execute rejects oversized content-length before reading", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({ code: "export default () => ({})", input: {} }),
      headers: { "content-length": "9999999" },
    }),
  );
  assertEquals(res.status, 413);
  await res.body?.cancel();
});

Deno.test("POST /execute body cap is byte-accurate for multibyte text", async () => {
  // ~400k chars of "€" is > 1MB of UTF-8 bytes though under 1M in .length.
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({ code: "€".repeat(400_000), input: {} }),
    }),
  );
  assertEquals(res.status, 413);
  await res.body?.cancel();
});

Deno.test("POST /execute accepts explicit nulls for optional fields", async () => {
  // Regression: .NET's serializer used to send absent optionals as JSON null;
  // null must count as absent, not fail the 'must be a valid URL' checks.
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({
        code: "export default () => ({})",
        input: { data: {} },
        timeoutMs: null,
        net: null,
        extract: null,
      }),
    }),
  );
  assertEquals(res.status, 200);
  assertEquals((await res.json()).ok, true);
});

Deno.test("POST /execute rejects a non-number timeoutMs", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({
        code: "export default () => ({})",
        input: {},
        timeoutMs: "soon",
      }),
    }),
  );
  assertEquals(res.status, 400);
  await res.body?.cancel();
});

Deno.test("more than 4 concurrent executions get 429", async () => {
  const slow = () =>
    handler(
      new Request("http://x/execute", {
        method: "POST",
        body: JSON.stringify({
          code:
            `export default async function run() { await new Promise(() => {}); }`,
          input: {},
          timeoutMs: 1000,
        }),
      }),
    );
  const responses = await Promise.all([slow(), slow(), slow(), slow(), slow()]);
  const statuses = responses.map((r) => r.status);
  assert(statuses.includes(429), `statuses: ${statuses}`);
  for (const r of responses) await r.body?.cancel();
});

Deno.test("POST /execute rejects a non-array net", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({
        code: "export default () => ({})",
        input: {},
        net: "api.example.com",
      }),
    }),
  );
  assertEquals(res.status, 400);
  await res.body?.cancel();
});

Deno.test("POST /execute rejects a malformed extract", async () => {
  for (
    const extract of [
      "yes",
      { url: null, token: null },
      { url: "http://x/e" },
      { url: "not a url", token: "t" },
      { url: "http://x/e", token: "" },
    ]
  ) {
    const res = await handler(
      new Request("http://x/execute", {
        method: "POST",
        body: JSON.stringify({
          code: "export default () => ({})",
          input: {},
          extract,
        }),
      }),
    );
    assertEquals(res.status, 400, JSON.stringify(extract));
    await res.body?.cancel();
  }
});
