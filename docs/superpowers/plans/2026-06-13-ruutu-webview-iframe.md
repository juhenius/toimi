# ruutu `webview` iframe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Toimi show an external web page (e.g. a parcel-tracking page) on a ruutu display via a seeded `webview` template that embeds a sandboxed, https-only `<iframe>`.

**Architecture:** One new general-purpose Scriban filter `safe_url` (validates https + rejects internal hosts + HTML-escapes) registered in the renderer; one seeded `webview` template that uses it; a one-line addition to the `use-displays` seeded skill. No new MCP tool, no linter change. Toimi pushes it with the existing `DisplayShow(identifier, "webview", {url, title?})`.

**Tech Stack:** .NET 10, Scriban (text mode), xUnit. Tests run in a `.NET 10` SDK container (no local SDK assumed).

**Spec:** `docs/superpowers/specs/2026-06-13-ruutu-webview-iframe-design.md`

---

## File Structure

- **Modify** `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs` — add `public static string SafeUrl(string?)` + private `IsPrivate(IPAddress)`; register `safe_url` into the per-render `ScriptObject`.
- **Modify** `src/toimi.tools.ruutu/Data/SeedTemplates.cs` — add the `webview` `SeedTemplate` to `All`.
- **Modify** `src/toimi.tools.taidot/Skills/SkillSeeder.cs` — add one sentence to the `use-displays` skill body.
- **Create** `src/toimi.tools.ruutu.Tests/Rendering/SafeUrlTests.cs` — unit tests for `SafeUrl`.
- **Create** `src/toimi.tools.ruutu.Tests/Rendering/WebviewTemplateTests.cs` — render + lint tests for the seeded `webview`.
- **Modify** `src/toimi.tools.ruutu.Tests/Rendering/SeedTemplatesRenderTests.cs` — add `webview` to the two `[InlineData]` lists and the `covered` drift set.

**Test run command** (always from repo root `/Users/jari/private/toimi`):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo --filter "<FILTER>"
```

Replace `<FILTER>` per step (or drop `--filter` to run all ruutu tests).

---

## Task 1: `safe_url` Scriban filter

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/SafeUrlTests.cs`
- Modify: `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/toimi.tools.ruutu.Tests/Rendering/SafeUrlTests.cs`:

```csharp
using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class SafeUrlTests
{
  [Fact]
  public void Passes_a_normal_https_url_unchanged()
  {
    Assert.Equal("https://posti.fi/track/123", ScribanRenderer.SafeUrl("https://posti.fi/track/123"));
  }

  [Fact]
  public void Escapes_ampersands_in_query_for_attribute_context()
  {
    Assert.Equal("https://t.test/p?a=1&amp;b=2", ScribanRenderer.SafeUrl("https://t.test/p?a=1&b=2"));
  }

  [Theory]
  [InlineData("http://example.com")]
  [InlineData("javascript:alert(1)")]
  [InlineData("data:text/html,<h1>x</h1>")]
  [InlineData("file:///etc/passwd")]
  [InlineData("ftp://example.com/x")]
  public void Rejects_non_https_schemes(string url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Theory]
  [InlineData("https://localhost/admin")]
  [InlineData("https://router/")]
  [InlineData("https://127.0.0.1/")]
  [InlineData("https://10.0.0.5/")]
  [InlineData("https://192.168.1.1/")]
  [InlineData("https://172.16.4.4/")]
  [InlineData("https://169.254.1.1/")]
  public void Rejects_loopback_private_and_single_label_hosts(string url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("not a url")]
  [InlineData("/relative/path")]
  public void Rejects_null_empty_and_non_absolute(string? url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Fact]
  public void Prevents_attribute_breakout()
  {
    // A quote/angle-bracket payload must never survive into the attribute.
    var result = ScribanRenderer.SafeUrl("https://x.test/a\"><script>alert(1)</script>");
    Assert.DoesNotContain("\"", result);
    Assert.DoesNotContain("<script>", result);
  }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo --filter "FullyQualifiedName~SafeUrlTests"
```
Expected: build error / FAIL — `ScribanRenderer` does not contain a definition for `SafeUrl`.

- [ ] **Step 3: Implement `SafeUrl` and register the filter**

In `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs`, add these usings at the top (after the existing `using` lines):

```csharp
using System.Net;
using System.Net.Sockets;
```

Inside `RenderInternalAsync`, register the filter right after the data is copied into the `ScriptObject`. Change:

```csharp
    var scriptObj = new ScriptObject();
    foreach (var (k, v) in enriched) scriptObj[k] = v;
    var context = new TemplateContext { StrictVariables = false };
```

to:

```csharp
    var scriptObj = new ScriptObject();
    foreach (var (k, v) in enriched) scriptObj[k] = v;
    scriptObj.Import("safe_url", (Func<string?, string>)SafeUrl);
    var context = new TemplateContext { StrictVariables = false };
```

Add these two methods to the `ScribanRenderer` class (e.g. just below `RenderAsync`):

```csharp
  /// <summary>
  /// Template filter: returns the URL only if it is an absolute https URL with a
  /// public, externally-routable host, HTML-escaped for safe use in an attribute.
  /// Anything else (other schemes, loopback/private/internal hosts, malformed,
  /// null) collapses to "about:blank" so it can never break out of the attribute
  /// or aim a display's browser at the local network.
  /// </summary>
  public static string SafeUrl(string? input)
  {
    if (string.IsNullOrWhiteSpace(input)) return "about:blank";
    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return "about:blank";
    if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return "about:blank";
    if (uri.IsLoopback) return "about:blank";

    var host = uri.DnsSafeHost;
    if (string.IsNullOrEmpty(host)) return "about:blank";

    if (IPAddress.TryParse(host, out var ip))
    {
      if (IsPrivate(ip)) return "about:blank";
    }
    else if (!host.Contains('.'))
    {
      // Single-label hostname (e.g. "router", "localhost") — not externally routable.
      return "about:blank";
    }

    return WebUtility.HtmlEncode(uri.AbsoluteUri);
  }

  private static bool IsPrivate(IPAddress ip)
  {
    if (IPAddress.IsLoopback(ip)) return true;

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
      var b = ip.GetAddressBytes();
      return b[0] == 10
          || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
          || (b[0] == 192 && b[1] == 168)
          || (b[0] == 169 && b[1] == 254);
    }

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (ip.IsIPv6LinkLocal) return true;
      var b = ip.GetAddressBytes();
      return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
    }

    return false;
  }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo --filter "FullyQualifiedName~SafeUrlTests"
```
Expected: PASS (all SafeUrlTests green).

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs \
        src/toimi.tools.ruutu.Tests/Rendering/SafeUrlTests.cs
git commit -m "feat(ruutu): add safe_url Scriban filter (https-only, internal-host guard)"
```

---

## Task 2: Seeded `webview` template

**Files:**
- Create: `src/toimi.tools.ruutu.Tests/Rendering/WebviewTemplateTests.cs`
- Modify: `src/toimi.tools.ruutu.Tests/Rendering/SeedTemplatesRenderTests.cs`
- Modify: `src/toimi.tools.ruutu/Data/SeedTemplates.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/toimi.tools.ruutu.Tests/Rendering/WebviewTemplateTests.cs`:

```csharp
using System.Text.Json;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class WebviewTemplateTests
{
  private static readonly IRenderTemplateSource Source = new MapSource(
    SeedTemplates.All.ToDictionary(t => t.Name, t => new TemplateBody(t.ModernHtml, t.LegacyHtml)));

  private sealed class MapSource(IReadOnlyDictionary<string, TemplateBody> map) : IRenderTemplateSource
  {
    public Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default) =>
      Task.FromResult(map.TryGetValue(name, out var b) ? b : null);
  }

  private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

  private static Task<string> Render(string dataJson) =>
    ScribanRenderer.RenderAsync("webview", Json(dataJson), "modern", Source);

  [Fact]
  public async Task Renders_sandboxed_iframe_with_the_url()
  {
    var html = await Render("""{ "url": "https://posti.fi/track/9" }""");
    Assert.Contains("<iframe", html);
    Assert.Contains("sandbox=\"allow-scripts allow-same-origin\"", html);
    Assert.Contains("src=\"https://posti.fi/track/9\"", html);
  }

  [Fact]
  public async Task Shows_a_header_only_when_title_is_present()
  {
    var withTitle = await Render("""{ "url": "https://t.test/x", "title": "Parcel tracking" }""");
    Assert.Contains("Parcel tracking", withTitle);
    Assert.Contains("height:40px", withTitle); // header bar present

    var noTitle = await Render("""{ "url": "https://t.test/x" }""");
    Assert.DoesNotContain("height:40px", noTitle); // no header bar
    Assert.Contains("height:100%", noTitle);       // iframe fills the display
  }

  [Fact]
  public async Task Escapes_the_title()
  {
    var html = await Render("""{ "url": "https://t.test/x", "title": "<b>hi</b>" }""");
    Assert.DoesNotContain("<b>hi</b>", html);
    Assert.Contains("&lt;b&gt;hi&lt;/b&gt;", html);
  }

  [Fact]
  public async Task Collapses_a_non_https_url_to_about_blank()
  {
    var html = await Render("""{ "url": "javascript:alert(1)" }""");
    Assert.Contains("src=\"about:blank\"", html);
    Assert.DoesNotContain("javascript:", html);
  }

  [Fact]
  public async Task Prevents_url_attribute_breakout()
  {
    var html = await Render("""{ "url": "https://x.test/a\"><script>alert(1)</script>" }""");
    Assert.DoesNotContain("<script>alert(1)</script>", html);
  }

  [Fact]
  public void Legacy_body_passes_the_tier_linter()
  {
    var legacy = SeedTemplates.All.Single(t => t.Name == "webview").LegacyHtml;
    var result = TierLinter.Lint("legacy", legacy);
    Assert.True(result.Valid,
      "webview legacy body must pass the linter; issues: " +
      string.Join(", ", result.Issues.Select(i => i.Rule)));
  }
}
```

Also update `src/toimi.tools.ruutu.Tests/Rendering/SeedTemplatesRenderTests.cs` so the drift checks include `webview`.

In the **modern** theory, add this line to the `[InlineData]` block (after the `stack` lines):
```csharp
  [InlineData("webview", """{"url":"https://x.test/a"}""")]
```

In the **legacy** theory, add the same line to its `[InlineData]` block:
```csharp
  [InlineData("webview", """{"url":"https://x.test/a"}""")]
```

In `Every_seeded_template_appears_in_test_coverage`, add `"webview"` to the `covered` set:
```csharp
    var covered = new HashSet<string>
    {
      "splash", "clock", "message", "notification", "todo_list", "weather",
      "calendar_day", "reminders", "split_horizontal", "split_vertical", "stack",
      "webview"
    };
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo \
  --filter "FullyQualifiedName~WebviewTemplateTests|FullyQualifiedName~SeedTemplatesRenderTests"
```
Expected: FAIL — `webview` is not in `SeedTemplates.All`, so renders throw `RenderException("Template 'webview' not found")` and the drift test's sets differ.

- [ ] **Step 3: Add the `webview` seed template**

In `src/toimi.tools.ruutu/Data/SeedTemplates.cs`, add this entry to the `SeedTemplates.All` array (place it as the last element, before the closing `];`):

```csharp
    new(
      Name: "webview",
      Description: "Embed an external web page (e.g. a parcel-tracking page) in a sandboxed iframe. Provide an https `url`; an optional `title` shows a header bar. Works on modern and legacy displays. Note: sites that forbid framing (X-Frame-Options / CSP frame-ancestors) will appear blank.",
      SchemaJson: """
        {
          "type": "object",
          "properties": {
            "url":   { "type": "string", "description": "https URL to embed" },
            "title": { "type": "string", "description": "optional header label" }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        {{ if title }}<div style="height:40px;background:#222;color:#fff;font:500 15px -apple-system,Helvetica,Arial,sans-serif;line-height:40px;padding:0 14px;overflow:hidden;white-space:nowrap">{{ title | html.escape }}</div>{{ end }}
        <iframe src="{{ url | safe_url }}" sandbox="allow-scripts allow-same-origin" referrerpolicy="no-referrer" style="display:block;width:100%;height:{{ if title }}calc(100% - 40px){{ else }}100%{{ end }};border:0;background:#fff"></iframe>
        """,
      LegacyHtml: """
        {{ if title }}<div style="height:40px;background:#222;color:#fff;font:500 15px -apple-system,Helvetica,Arial,sans-serif;line-height:40px;padding:0 14px;overflow:hidden;white-space:nowrap">{{ title | html.escape }}</div>{{ end }}
        <iframe src="{{ url | safe_url }}" sandbox="allow-scripts allow-same-origin" referrerpolicy="no-referrer" style="display:block;width:100%;height:{{ if title }}calc(100% - 40px){{ else }}100%{{ end }};border:0;background:#fff"></iframe>
        """
    ),
```

Note: the assertions in `WebviewTemplateTests` (`sandbox="allow-scripts allow-same-origin"`, `height:40px`, `height:100%`, `src="..."`) match this HTML exactly — do not reword it.

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo \
  --filter "FullyQualifiedName~WebviewTemplateTests|FullyQualifiedName~SeedTemplatesRenderTests"
```
Expected: PASS.

- [ ] **Step 5: Run the full ruutu test suite (no regressions)**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo
```
Expected: PASS (previous 57 + the new tests).

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.ruutu/Data/SeedTemplates.cs \
        src/toimi.tools.ruutu.Tests/Rendering/WebviewTemplateTests.cs \
        src/toimi.tools.ruutu.Tests/Rendering/SeedTemplatesRenderTests.cs
git commit -m "feat(ruutu): seed webview template for embedding external pages"
```

---

## Task 3: Teach Toimi via the `use-displays` skill

**Files:**
- Modify: `src/toimi.tools.taidot/Skills/SkillSeeder.cs`

- [ ] **Step 1: Add the webview guidance line**

In `src/toimi.tools.taidot/Skills/SkillSeeder.cs`, find the `use-displays` skill body (around line 276–283, the numbered list of display steps). Add a new sentence after the `DisplayClear` line (step 5) and before the "Authoring new templates" paragraph:

```
      To show a web page or tracking link on a display, DisplayShow the 'webview' template with { "url": "https://...", "title": "Parcel tracking" } (title optional). Only https URLs are accepted; pages that forbid framing (X-Frame-Options / CSP) will appear blank.
```

Match the surrounding indentation and raw-string style exactly (the body is a single multi-line raw string literal).

- [ ] **Step 2: Build taidot to verify the string literal is valid**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build src/toimi.tools.taidot/toimi.tools.taidot.csproj --nologo
```
Expected: Build succeeded, 0 errors. (No unit test — the skill body is seed text upserted idempotently at startup.)

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.taidot/Skills/SkillSeeder.cs
git commit -m "docs(taidot): teach use-displays skill about the webview template"
```

---

## Final verification

- [ ] **Run the full solution test suite**

Run:
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test toimi.sln --nologo 2>&1 | grep -E "Passed!|Failed!|error" | grep -v NU19
```
Expected: every test project reports `Passed!`, 0 failures.

---

## Notes / out of scope (do not implement)

- No new MCP tool, no `TierLinter` change, no transport change.
- No historical logging of shown URLs (current-state `CurrentData`/`CurrentPushedAt` on the display row is the audit trail).
- No iframe→shell interactivity, no auto-refresh, no domain allowlist, no per-show confirmation.
- Deployment (`scripts/deploy.sh dev tools.ruutu` and `tools.taidot`) is a manual follow-up after merge, not part of this plan.
