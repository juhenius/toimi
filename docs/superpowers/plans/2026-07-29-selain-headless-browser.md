# selain Headless Browser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `toimi.tools.selain` — a stateless headless-Chromium MCP tool server (12 curated tools) with per-tab screenshot/screencast HTTP endpoints for ruutu display feeds.

**Architecture:** One new .NET pod following the 1:1:1 convention. Playwright for .NET drives one headless Chromium; a `TabManager` owns tabs (GUID ids double as viewer-URL capability tokens) with all actions serialized behind one lock; aria snapshots with refs give the LLM targetable elements; CDP screencast streams live frames to a self-contained viewer page that ruutu embeds via its existing `webview` template. SSRF containment = egress NetworkPolicy (k3s) + Playwright request routing (all environments).

**Tech Stack:** .NET 10, ASP.NET minimal APIs, `Microsoft.Playwright`, `ModelContextProtocol` 1.4.1 (+`.AspNetCore`), xUnit, `Microsoft.AspNetCore.Mvc.Testing`, Kustomize.

**Spec:** `docs/superpowers/specs/2026-07-29-selain-headless-browser-design.md`

---

## Environment notes (read first)

- `dotnet` is NOT on PATH — run every dotnet command as `mise exec dotnet -- dotnet …`. Verified working: `mise exec dotnet -- dotnet --version` → `10.0.301`.
- There is no local `kubectl`/cluster on this machine — k8s/Docker tasks end at "manifests written + kustomize-buildable"; live deploy verification is a user step.
- Work on branch `selain-browser` (already exists, spec committed there).
- **Pending `toimi.sln` working-tree change:** the uncommitted diff *removes* `toimi.notifications.Tests` and `toimi.tools.koti.Tests` from the solution — it looks like IDE damage, and the user approved restoring it. Task 1 starts with `git checkout -- toimi.sln`.
- Style: 2-space indent, file-scoped namespaces. Before every commit run `mise exec dotnet -- dotnet format <changed csproj> --verbosity minimal` then `--verify-no-changes`.
- Browser-dependent tests are gated by `ChromiumFactAttribute` (Task 5) and skip when Chromium isn't installed. Install once with: `mise exec dotnet -- dotnet run --project src/toimi.tools.selain -- install-browsers`.
- Existing namespace conventions: tool servers use lowercase project-name namespaces (`toimi.tools.verkko.Fetcher`); follow with `toimi.tools.selain.*`.

## File map

```
src/toimi.tools.selain/
  Program.cs                      host wiring + install-browsers arg + partial class for tests
  appsettings.json
  toimi.tools.selain.csproj
  Dockerfile
  Browser/
    SelainOptions.cs              Enabled, PublicBaseUrl, AllowedPrivateHosts, IdleShutdownMinutes
    UrlPolicy.cs                  scheme + private-host validation (uses Toimi.Core.Net.PrivateAddress)
    SnapshotFormatter.cs          15K/50K caps, truncation marker, SHA-256 hash
    IPageSession.cs               seam for TabManager unit tests
    PlaywrightSession.cs          real IPage-backed session
    TabManager.cs                 tabs, active tab, adoption, lock, viewer URLs, dialog notes
    BrowserHost.cs                lazy launch, route guard, crash relaunch, stream counter
    IdleShutdownService.cs        BackgroundService: close browser after idle
  Tools/
    ToolGuard.cs                  Selain:Enabled gate helper
    PageResults.cs                snapshot+hash result composer shared by tools
    BrowseTools.cs                browse, snapshot, read_page
    ActTools.cs                   click, hover, type, select_option, press_key, go_back, wait_for
    TabTools.cs                   tabs
    ScreenshotTool.cs             screenshot (CallToolResult with image block)
  Streaming/
    ScreencastService.cs          CDP Page.startScreencast → WebSocket relay
    ViewerPage.cs                 self-contained canvas+WS viewer HTML
  Endpoints/
    TabEndpoints.cs               GET screenshot, GET view, WS stream
src/toimi.tools.selain.Tests/
  toimi.tools.selain.Tests.csproj
  UrlPolicyTests.cs
  SnapshotFormatterTests.cs
  TabManagerTests.cs
  FakePageSession.cs
  ChromiumFactAttribute.cs
  Integration/
    SelainFixture.cs              fixture Kestrel site + real browser stack
    BrowserToolTests.cs
    ActToolTests.cs
    TabToolTests.cs
    EndpointTests.cs              WebApplicationFactory-based
k8s/base/tools-selain/
  deployment.yaml  service.yaml  ingress.yaml  networkpolicy.yaml  kustomization.yaml
Modified:
  toimi.sln                                     (+2 projects)
  k8s/base/kustomization.yaml                   (+tools-selain)
  k8s/overlays/server/kustomization.yaml        (+selain TLS patch)
  k8s/overlays/server/tls/selain-ingress-patch.yaml   (new)
  src/toimi.web/appsettings.json                (+selain McpServer)
  src/toimi.tools.tietue/appsettings.json       (+selain McpServer)
  src/toimi.tools.verkko/Tools/FetchUrlTool.cs  (description line)
  CLAUDE.md                                     (selain pod entry)
```

---

### Task 1: Project scaffolding

**Files:**
- Modify: `toimi.sln` (restore, then add projects)
- Create: `src/toimi.tools.selain/toimi.tools.selain.csproj`
- Create: `src/toimi.tools.selain/Program.cs`
- Create: `src/toimi.tools.selain/appsettings.json`
- Create: `src/toimi.tools.selain/Browser/SelainOptions.cs`
- Create: `src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj`

- [ ] **Step 1: Restore the damaged solution file**

```bash
cd /Users/jari/private/toimi && git checkout -- toimi.sln
```

- [ ] **Step 2: Create the main project**

`src/toimi.tools.selain/toimi.tools.selain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Playwright" Version="1.54.0" />
    <PackageReference Include="ModelContextProtocol" Version="1.4.1" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.4.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>

</Project>
```

Then check the actual latest Microsoft.Playwright: `mise exec dotnet -- dotnet list src/toimi.tools.selain/toimi.tools.selain.csproj package --outdated` after the first restore; upgrade to the newest 1.x and **note the version — the Dockerfile runtime image tag (Task 13) must match it.**

- [ ] **Step 3: Create SelainOptions**

`src/toimi.tools.selain/Browser/SelainOptions.cs`:

```csharp
namespace toimi.tools.selain.Browser;

public class SelainOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>External base URL displays use to reach /tabs/{id}/view (e.g. https://toimi.example).</summary>
  public string PublicBaseUrl { get; set; } = "";

  /// <summary>Private hosts navigation may still reach — used by integration tests (loopback fixtures).</summary>
  public List<string> AllowedPrivateHosts { get; set; } = [];

  public int IdleShutdownMinutes { get; set; } = 15;
}
```

- [ ] **Step 4: Create Program.cs (skeleton — services filled in by later tasks)**

`src/toimi.tools.selain/Program.cs`:

```csharp
using toimi.tools.selain.Browser;
using Toimi.Core.Hosting;

if (args is ["install-browsers"])
{
  // Dev helper: install the Chromium build matching the Microsoft.Playwright package.
  Environment.Exit(Microsoft.Playwright.Program.Main(["install", "chromium"]));
}

var builder = WebApplication.CreateBuilder(args);

var selainOptions = builder.Configuration.GetSection("Selain").Get<SelainOptions>() ?? new SelainOptions();
builder.Services.AddSingleton(selainOptions);

builder.Services.AddToimiMcpServer("selain", typeof(Program).Assembly);

var app = builder.Build();

app.MapToimiMcp();

app.Run();

public partial class Program;
```

(~~The `public partial class Program;` line makes `WebApplicationFactory<Program>` possible in Task 10's endpoint tests.~~ **Resolved during Task 1:** the SDK errors on that line (ASP0027 — no longer required); omit it. `WebApplicationFactory<Program>` works against the implicit public Program class, as tietue.Tests already proves. Task 10: also bump `Microsoft.AspNetCore.Mvc.Testing` to 10.0.10 to match tietue.Tests when touching the test csproj.)

- [ ] **Step 5: Create appsettings.json**

`src/toimi.tools.selain/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Selain": {
    "Enabled": true,
    "PublicBaseUrl": ""
  }
}
```

- [ ] **Step 6: Create the test project**

`src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../toimi.tools.selain/toimi.tools.selain.csproj" />
  </ItemGroup>

</Project>
```

(If `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 doesn't resolve, take the newest 10.x: `mise exec dotnet -- dotnet add src/toimi.tools.selain.Tests package Microsoft.AspNetCore.Mvc.Testing`.)

- [ ] **Step 7: Add both projects to the solution and build**

```bash
mise exec dotnet -- dotnet sln toimi.sln add src/toimi.tools.selain/toimi.tools.selain.csproj src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj
mise exec dotnet -- dotnet build src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj
```

Expected: build succeeds.

- [ ] **Step 8: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add toimi.sln src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): scaffold headless-browser tool server project"
```

---

### Task 2: UrlPolicy

**Files:**
- Create: `src/toimi.tools.selain/Browser/UrlPolicy.cs`
- Test: `src/toimi.tools.selain.Tests/UrlPolicyTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.selain.Tests/UrlPolicyTests.cs`:

```csharp
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class UrlPolicyTests
{
  private static UrlPolicy Policy(params string[] allowedPrivate)
  {
    return new UrlPolicy(new SelainOptions { AllowedPrivateHosts = [.. allowedPrivate] });
  }

  [Theory]
  [InlineData("https://example.com/page")]
  [InlineData("http://example.com")]
  public void Validate_accepts_public_http_and_https(string url)
  {
    var (ok, error, uri) = Policy().Validate(url);
    Assert.True(ok);
    Assert.Null(error);
    Assert.NotNull(uri);
  }

  [Theory]
  [InlineData("ftp://example.com")]
  [InlineData("javascript:alert(1)")]
  [InlineData("not a url")]
  [InlineData("/relative/path")]
  public void Validate_rejects_non_http_or_malformed(string url)
  {
    var (ok, error, _) = Policy().Validate(url);
    Assert.False(ok);
    Assert.Contains("http", error);
  }

  [Theory]
  [InlineData("https://localhost/admin")]
  [InlineData("http://10.1.2.3/")]
  [InlineData("http://192.168.1.1/")]
  [InlineData("http://toimi-tools-tietue.apps.svc.cluster.local/sse")]
  [InlineData("http://router/")]
  public void Validate_rejects_private_and_internal_hosts(string url)
  {
    var (ok, error, _) = Policy().Validate(url);
    Assert.False(ok);
    Assert.Contains("private or internal", error);
  }

  [Fact]
  public void Validate_allows_explicitly_allowlisted_private_host()
  {
    var (ok, _, _) = Policy("127.0.0.1").Validate("http://127.0.0.1:5000/fixture");
    Assert.True(ok);
  }

  [Fact]
  public void IsAllowedHost_blocks_private_but_respects_allowlist()
  {
    var policy = Policy("127.0.0.1");
    Assert.True(policy.IsAllowedHost("example.com"));
    Assert.True(policy.IsAllowedHost("127.0.0.1"));
    Assert.False(policy.IsAllowedHost("10.255.255.1"));
    Assert.False(policy.IsAllowedHost("localhost"));
  }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter UrlPolicyTests
```

Expected: FAIL (compile error — `UrlPolicy` does not exist).

- [ ] **Step 3: Implement**

`src/toimi.tools.selain/Browser/UrlPolicy.cs`:

```csharp
namespace toimi.tools.selain.Browser;

/// <summary>
/// Navigation policy: http(s) only, no private/internal hosts (SSRF guard shared
/// with verkko via Toimi.Core.Net.PrivateAddress). AllowedPrivateHosts exists for
/// integration tests that serve fixture pages on loopback.
/// </summary>
public class UrlPolicy(SelainOptions options)
{
  public (bool Ok, string? Error, Uri? Uri) Validate(string url)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
      || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
      return (false, "Invalid URL. Must be an absolute URL starting with http:// or https://", null);
    }

    return IsAllowedHost(uri.DnsSafeHost)
      ? (true, null, uri)
      : (false, $"Blocked URL: '{uri.DnsSafeHost}' is a private or internal host.", null);
  }

  public bool IsAllowedHost(string host)
  {
    return options.AllowedPrivateHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
      || !Toimi.Core.Net.PrivateAddress.IsBlockedHost(host);
  }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter UrlPolicyTests
```

Expected: PASS (all).

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): UrlPolicy navigation guard over shared PrivateAddress"
```

---

### Task 3: SnapshotFormatter

**Files:**
- Create: `src/toimi.tools.selain/Browser/SnapshotFormatter.cs`
- Test: `src/toimi.tools.selain.Tests/SnapshotFormatterTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.selain.Tests/SnapshotFormatterTests.cs`:

```csharp
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class SnapshotFormatterTests
{
  [Fact]
  public void Truncate_returns_short_input_unchanged()
  {
    Assert.Equal("hello", SnapshotFormatter.Truncate("hello", SnapshotFormatter.ActionCap));
  }

  [Fact]
  public void Truncate_caps_long_input_with_marker()
  {
    var input = new string('x', SnapshotFormatter.ActionCap + 500);
    var result = SnapshotFormatter.Truncate(input, SnapshotFormatter.ActionCap);
    Assert.StartsWith(new string('x', 100), result);
    Assert.EndsWith(SnapshotFormatter.TruncationMarker, result);
    Assert.True(result.Length < input.Length);
  }

  [Fact]
  public void Caps_are_15k_for_actions_and_50k_for_read_page()
  {
    Assert.Equal(15_000, SnapshotFormatter.ActionCap);
    Assert.Equal(50_000, SnapshotFormatter.ReadCap);
  }

  [Fact]
  public void Hash_is_stable_for_equal_input_and_differs_otherwise()
  {
    Assert.Equal(SnapshotFormatter.Hash("abc"), SnapshotFormatter.Hash("abc"));
    Assert.NotEqual(SnapshotFormatter.Hash("abc"), SnapshotFormatter.Hash("abd"));
  }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter SnapshotFormatterTests
```

Expected: FAIL (compile error).

- [ ] **Step 3: Implement**

`src/toimi.tools.selain/Browser/SnapshotFormatter.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace toimi.tools.selain.Browser;

/// <summary>
/// Token-budget rules for page snapshots: action tools return at most ActionCap
/// chars (each action's snapshot lands in LLM context, every step), read_page may
/// return up to ReadCap. Hash powers the "(page unchanged)" suppression.
/// </summary>
public static class SnapshotFormatter
{
  public const int ActionCap = 15_000;
  public const int ReadCap = 50_000;
  public const string TruncationMarker = "\n\n[Truncated — use read_page for full text or wait_for + snapshot to inspect further]";

  public static string Truncate(string content, int cap)
  {
    return content.Length <= cap ? content : content[..cap] + TruncationMarker;
  }

  public static string Hash(string content)
  {
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
  }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter SnapshotFormatterTests
```

Expected: PASS.

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): snapshot caps, truncation marker, content hash"
```

---

### Task 4: TabManager + IPageSession seam

**Files:**
- Create: `src/toimi.tools.selain/Browser/IPageSession.cs`
- Create: `src/toimi.tools.selain/Browser/TabManager.cs`
- Create: `src/toimi.tools.selain.Tests/FakePageSession.cs`
- Test: `src/toimi.tools.selain.Tests/TabManagerTests.cs`

- [ ] **Step 1: Write the seam and fake**

`src/toimi.tools.selain/Browser/IPageSession.cs`:

```csharp
namespace toimi.tools.selain.Browser;

/// <summary>
/// The slice of a browser page TabManager needs for bookkeeping. PlaywrightSession
/// wraps a real IPage; tests use FakePageSession. NativeHandle lets the popup
/// adoption path dedupe (the context Page event and NewPageAsync both see the
/// same underlying page object).
/// </summary>
public interface IPageSession
{
  object NativeHandle { get; }
  string Url { get; }
  Task<string> TitleAsync();
  Task CloseAsync();
}
```

`src/toimi.tools.selain.Tests/FakePageSession.cs`:

```csharp
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tests;

public sealed class FakePageSession(string url = "about:blank", string title = "fake") : IPageSession
{
  public bool Closed { get; private set; }
  public object NativeHandle => this;
  public string Url => url;

  public Task<string> TitleAsync()
  {
    return Task.FromResult(title);
  }

  public Task CloseAsync()
  {
    Closed = true;
    return Task.CompletedTask;
  }
}
```

- [ ] **Step 2: Write the failing tests**

`src/toimi.tools.selain.Tests/TabManagerTests.cs`:

```csharp
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class TabManagerTests
{
  private static TabManager Manager(string publicBaseUrl = "https://toimi.example")
  {
    return new TabManager(new SelainOptions { PublicBaseUrl = publicBaseUrl });
  }

  [Fact]
  public void First_adopted_tab_becomes_active()
  {
    var tabs = Manager();
    var id = tabs.Adopt(new FakePageSession());
    Assert.Equal(id, tabs.Active?.Id);
    Assert.Equal(1, tabs.Count);
  }

  [Fact]
  public void Adopting_a_second_tab_does_not_steal_active()
  {
    var tabs = Manager();
    var first = tabs.Adopt(new FakePageSession());
    tabs.Adopt(new FakePageSession());
    Assert.Equal(first, tabs.Active?.Id);
    Assert.Equal(2, tabs.Count);
  }

  [Fact]
  public void Adopting_the_same_native_handle_twice_returns_the_same_id()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var id = tabs.Adopt(session);
    Assert.Equal(id, tabs.Adopt(session));
    Assert.Equal(1, tabs.Count);
  }

  [Fact]
  public void Switch_changes_active_and_rejects_unknown_ids()
  {
    var tabs = Manager();
    tabs.Adopt(new FakePageSession());
    var second = tabs.Adopt(new FakePageSession());
    Assert.True(tabs.Switch(second));
    Assert.Equal(second, tabs.Active?.Id);
    Assert.False(tabs.Switch(Guid.NewGuid()));
  }

  [Fact]
  public async Task Close_removes_the_tab_closes_the_session_and_falls_back_active()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var first = tabs.Adopt(session);
    var second = tabs.Adopt(new FakePageSession());
    Assert.True(await tabs.CloseAsync(first));
    Assert.True(session.Closed);
    Assert.Equal(second, tabs.Active?.Id);
    Assert.False(await tabs.CloseAsync(first));
  }

  [Fact]
  public void ResetAll_clears_everything()
  {
    var tabs = Manager();
    tabs.Adopt(new FakePageSession());
    tabs.ResetAll();
    Assert.Equal(0, tabs.Count);
    Assert.Null(tabs.Active);
  }

  [Fact]
  public void ViewerUrl_composes_from_public_base_url_trimming_slash()
  {
    var tabs = Manager("https://toimi.example/");
    var id = tabs.Adopt(new FakePageSession());
    Assert.Equal($"https://toimi.example/tabs/{id}/view", tabs.ViewerUrl(id));
  }

  [Fact]
  public void Dialog_notes_are_taken_once()
  {
    var tabs = Manager();
    var id = tabs.Adopt(new FakePageSession());
    tabs.NoteDialog(id, "[alert dialog auto-dismissed]");
    Assert.Equal("[alert dialog auto-dismissed]", tabs.TakeDialogNote(id));
    Assert.Null(tabs.TakeDialogNote(id));
  }

  [Fact]
  public void FindByHandle_locates_a_tab_by_its_native_page()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var id = tabs.Adopt(session);
    Assert.Equal(id, tabs.FindByHandle(session.NativeHandle));
    Assert.Null(tabs.FindByHandle(new object()));
  }
}
```

- [ ] **Step 3: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter TabManagerTests
```

Expected: FAIL (compile error — `TabManager` missing).

- [ ] **Step 4: Implement**

`src/toimi.tools.selain/Browser/TabManager.cs`:

```csharp
namespace toimi.tools.selain.Browser;

/// <summary>
/// Owns the open tabs. Tab GUIDs double as the capability token for the HTTP
/// viewer endpoints. ActionLock serializes every mutating browser operation —
/// snapshot refs belong to the active tab, so concurrent cross-tab actions
/// would race ref validity.
/// </summary>
public sealed class TabManager(SelainOptions options)
{
  private readonly List<TabEntry> _tabs = [];
  private readonly Lock _gate = new();
  private Guid? _activeId;

  public SemaphoreSlim ActionLock { get; } = new(1, 1);

  public sealed class TabEntry
  {
    public required Guid Id { get; init; }
    public required IPageSession Session { get; init; }
    public string? LastShownHash { get; set; }
    public string? DialogNote { get; set; }
  }

  public int Count
  {
    get
    {
      lock (_gate)
      {
        return _tabs.Count;
      }
    }
  }

  public TabEntry? Active
  {
    get
    {
      lock (_gate)
      {
        return _tabs.FirstOrDefault(t => t.Id == _activeId);
      }
    }
  }

  public Guid Adopt(IPageSession session)
  {
    lock (_gate)
    {
      var existing = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.NativeHandle, session.NativeHandle));
      if (existing is not null)
      {
        return existing.Id;
      }

      var entry = new TabEntry { Id = Guid.NewGuid(), Session = session };
      _tabs.Add(entry);
      _activeId ??= entry.Id;
      return entry.Id;
    }
  }

  public Guid? FindByHandle(object nativeHandle)
  {
    lock (_gate)
    {
      return _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.NativeHandle, nativeHandle))?.Id;
    }
  }

  public TabEntry? Get(Guid id)
  {
    lock (_gate)
    {
      return _tabs.FirstOrDefault(t => t.Id == id);
    }
  }

  public IReadOnlyList<TabEntry> List()
  {
    lock (_gate)
    {
      return [.. _tabs];
    }
  }

  public bool Switch(Guid id)
  {
    lock (_gate)
    {
      if (_tabs.All(t => t.Id != id))
      {
        return false;
      }

      _activeId = id;
      return true;
    }
  }

  public async Task<bool> CloseAsync(Guid id)
  {
    TabEntry? entry;
    lock (_gate)
    {
      entry = _tabs.FirstOrDefault(t => t.Id == id);
      if (entry is null)
      {
        return false;
      }

      _tabs.Remove(entry);
      if (_activeId == id)
      {
        _activeId = _tabs.FirstOrDefault()?.Id;
      }
    }

    await entry.Session.CloseAsync();
    return true;
  }

  public void ResetAll()
  {
    lock (_gate)
    {
      _tabs.Clear();
      _activeId = null;
    }
  }

  public string ViewerUrl(Guid id)
  {
    return $"{options.PublicBaseUrl.TrimEnd('/')}/tabs/{id}/view";
  }

  public void NoteDialog(Guid id, string note)
  {
    lock (_gate)
    {
      var entry = _tabs.FirstOrDefault(t => t.Id == id);
      if (entry is not null)
      {
        entry.DialogNote = note;
      }
    }
  }

  public string? TakeDialogNote(Guid id)
  {
    lock (_gate)
    {
      var entry = _tabs.FirstOrDefault(t => t.Id == id);
      var note = entry?.DialogNote;
      if (entry is not null)
      {
        entry.DialogNote = null;
      }

      return note;
    }
  }
}
```

- [ ] **Step 5: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter TabManagerTests
```

Expected: PASS (10 tests).

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): TabManager with page-session seam, adoption, viewer URLs"
```

---

### Task 5: BrowserHost, PlaywrightSession, Chromium gating, fixture, aria-ref smoke test

This task establishes the real-browser stack and immediately verifies the two Playwright APIs the whole design leans on: `AriaSnapshotAsync` with refs, and the `aria-ref=` selector engine. **If either API differs in the installed Playwright version, adapt here** (check the `IPage.AriaSnapshotAsync` overloads in the package) — every later task uses the pattern this task proves.

> **Resolved during Task 5 (Playwright 1.61.0):** there is no `Ref` option. The
> working call is `page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai })`
> — it emits `[ref=eN]` markers and `page.Locator($"aria-ref={r}")` resolves
> them. **Every later task's `Ref = true` snippet must be read as
> `Mode = AriaSnapshotMode.Ai`.** Also: CA1711 forced the collection class name
> `SelainCollectionDefinition` (collection string stays `"selain"`), and CA1873
> requires `logger.IsEnabled(...)` guards around log calls.

**Files:**
- Create: `src/toimi.tools.selain/Browser/PlaywrightSession.cs`
- Create: `src/toimi.tools.selain/Browser/BrowserHost.cs`
- Create: `src/toimi.tools.selain.Tests/ChromiumFactAttribute.cs`
- Create: `src/toimi.tools.selain.Tests/Integration/SelainFixture.cs`
- Test: `src/toimi.tools.selain.Tests/Integration/BrowserToolTests.cs` (smoke test only; grows in Task 6)
- Modify: `src/toimi.tools.selain/Program.cs` (register singletons)

- [ ] **Step 1: Install Chromium locally (one-time)**

```bash
mise exec dotnet -- dotnet build src/toimi.tools.selain/toimi.tools.selain.csproj
mise exec dotnet -- dotnet run --project src/toimi.tools.selain -- install-browsers
```

Expected: downloads Chromium into `~/.cache/ms-playwright/` (or is a no-op if present).

- [ ] **Step 2: Write PlaywrightSession**

`src/toimi.tools.selain/Browser/PlaywrightSession.cs`:

```csharp
using Microsoft.Playwright;

namespace toimi.tools.selain.Browser;

public sealed class PlaywrightSession(IPage page) : IPageSession
{
  public IPage Page { get; } = page;
  public object NativeHandle => Page;
  public string Url => Page.Url;

  public Task<string> TitleAsync()
  {
    return Page.TitleAsync();
  }

  public Task CloseAsync()
  {
    return Page.CloseAsync();
  }
}
```

- [ ] **Step 3: Write BrowserHost**

`src/toimi.tools.selain/Browser/BrowserHost.cs`:

```csharp
using Microsoft.Playwright;

namespace toimi.tools.selain.Browser;

/// <summary>
/// Lazily launches one headless Chromium + one context. Route guard aborts any
/// request (navigation, redirect, subresource) to a private host — defense in
/// depth under the pod's egress NetworkPolicy, and the only guard on dev
/// clusters whose CNI doesn't enforce NetworkPolicy. Crash → relaunch with a
/// one-shot restart notice; idle → torn down by IdleShutdownService.
/// </summary>
public sealed class BrowserHost(SelainOptions options, UrlPolicy policy, TabManager tabs, ILogger<BrowserHost> logger) : IAsyncDisposable
{
  private readonly SemaphoreSlim _launchLock = new(1, 1);
  private IPlaywright? _playwright;
  private IBrowser? _browser;
  private IBrowserContext? _context;
  private bool _restartNotice;
  private int _activeStreams;

  public DateTimeOffset LastUse { get; private set; } = DateTimeOffset.UtcNow;
  public int ActiveStreams => _activeStreams;
  public bool IsRunning => _browser is { IsConnected: true };

  public void StreamStarted()
  {
    Interlocked.Increment(ref _activeStreams);
  }

  public void StreamEnded()
  {
    Interlocked.Decrement(ref _activeStreams);
  }

  /// <summary>True exactly once after a crash-relaunch; tools prepend a notice.</summary>
  public bool ConsumeRestartNotice()
  {
    var notice = _restartNotice;
    _restartNotice = false;
    return notice;
  }

  public async Task<IBrowserContext> GetContextAsync()
  {
    LastUse = DateTimeOffset.UtcNow;
    if (_browser is { IsConnected: true } && _context is not null)
    {
      return _context;
    }

    await _launchLock.WaitAsync();
    try
    {
      if (_browser is { IsConnected: true } && _context is not null)
      {
        return _context;
      }

      if (_browser is not null)
      {
        _restartNotice = true;
        tabs.ResetAll();
        try
        {
          await _browser.DisposeAsync();
        }
        catch (PlaywrightException)
        {
          // Already dead — that's why we're here.
        }
        _context = null;
      }

      _playwright ??= await Playwright.CreateAsync();
      _browser = await _playwright.Chromium.LaunchAsync(new()
      {
        Headless = true,
        Args = ["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]
      });
      _context = await _browser.NewContextAsync(new()
      {
        ViewportSize = new() { Width = 1280, Height = 720 }
      });

      await _context.RouteAsync("**/*", async route =>
      {
        var host = Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
        if (host is not null && policy.IsAllowedHost(host))
        {
          await route.ContinueAsync();
        }
        else
        {
          logger.LogWarning("Blocked browser request to {Url} (private/internal host).", route.Request.Url);
          await route.AbortAsync("blockedbyclient");
        }
      });

      _context.Page += (_, page) =>
      {
        var id = tabs.Adopt(new PlaywrightSession(page));
        page.Dialog += async (_, dialog) =>
        {
          tabs.NoteDialog(id, $"[{dialog.Type} dialog auto-dismissed: \"{dialog.Message}\"]");
          await dialog.DismissAsync();
        };
      };

      return _context;
    }
    finally
    {
      _launchLock.Release();
    }
  }

  /// <summary>Idle teardown: nothing open, nothing streaming, quiet past the threshold.</summary>
  public async Task ShutdownIfIdleAsync()
  {
    if (!IsRunning || tabs.Count > 0 || _activeStreams > 0
      || DateTimeOffset.UtcNow - LastUse < TimeSpan.FromMinutes(options.IdleShutdownMinutes))
    {
      return;
    }

    logger.LogInformation("Closing idle browser after {Minutes} min.", options.IdleShutdownMinutes);
    await DisposeBrowserAsync();
  }

  private async Task DisposeBrowserAsync()
  {
    await _launchLock.WaitAsync();
    try
    {
      if (_browser is not null)
      {
        try
        {
          await _browser.DisposeAsync();
        }
        catch (PlaywrightException)
        {
          // Best-effort teardown.
        }
      }
      _browser = null;
      _context = null;
      tabs.ResetAll();
    }
    finally
    {
      _launchLock.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await DisposeBrowserAsync();
    _playwright?.Dispose();
    _launchLock.Dispose();
  }
}
```

- [ ] **Step 4: Register services in Program.cs**

In `src/toimi.tools.selain/Program.cs`, after the `AddSingleton(selainOptions)` line, add:

```csharp
builder.Services.AddSingleton<toimi.tools.selain.Browser.UrlPolicy>();
builder.Services.AddSingleton<toimi.tools.selain.Browser.TabManager>();
builder.Services.AddSingleton<toimi.tools.selain.Browser.BrowserHost>();
```

(With `using toimi.tools.selain.Browser;` already at the top, write them unqualified: `AddSingleton<UrlPolicy>()` etc.)

- [ ] **Step 5: Write the gate attribute and fixture**

`src/toimi.tools.selain.Tests/ChromiumFactAttribute.cs`:

```csharp
using Xunit;

namespace toimi.tools.selain.Tests;

/// <summary>
/// A Fact that skips itself when Playwright's Chromium isn't installed, mirroring
/// tietue's DockerFactAttribute pattern. Install once with:
/// mise exec dotnet -- dotnet run --project src/toimi.tools.selain -- install-browsers
/// </summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
  private static readonly Lazy<bool> ChromiumAvailable = new(Probe);

  public ChromiumFactAttribute()
  {
    if (!ChromiumAvailable.Value)
    {
      Skip = "Playwright Chromium is not installed; run 'dotnet run --project src/toimi.tools.selain -- install-browsers'.";
    }
  }

  private static bool Probe()
  {
    var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
    return Directory.Exists(root) && Directory.EnumerateDirectories(root, "chromium*").Any();
  }
}
```

`src/toimi.tools.selain.Tests/Integration/SelainFixture.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

/// <summary>
/// Shared per-collection stack: a loopback Kestrel site serving fixture pages,
/// plus the real browser components wired the same way Program.cs wires them.
/// AllowedPrivateHosts lets navigation reach the loopback fixtures while
/// 10.x subresources stay blocked (that asymmetry is itself under test).
/// </summary>
public sealed class SelainFixture : IAsyncLifetime
{
  private WebApplication? _site;

  public string BaseUrl { get; private set; } = "";
  public SelainOptions Options { get; } = new()
  {
    PublicBaseUrl = "https://toimi.example",
    AllowedPrivateHosts = ["127.0.0.1", "localhost"]
  };
  public UrlPolicy Policy { get; private set; } = null!;
  public TabManager Tabs { get; private set; } = null!;
  public BrowserHost Host { get; private set; } = null!;

  public async Task InitializeAsync()
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    _site = builder.Build();

    _site.MapGet("/static", () => Page("<h1>Static page</h1><p>plain content here</p>"));
    _site.MapGet("/js", () => Page(
      """
      <h1>Shell</h1>
      <script>setTimeout(() => {
        document.body.insertAdjacentHTML('beforeend', '<p id="late">Hydrated content arrived</p>');
      }, 200);</script>
      """));
    _site.MapGet("/form", () => Page(
      """
      <label for="name">Your name</label><input id="name" type="text">
      <select id="pick" aria-label="Pick one"><option value="a">Alpha</option><option value="b">Beta</option></select>
      <button onclick="document.getElementById('out').textContent =
        document.getElementById('name').value + '/' + document.getElementById('pick').value">Send</button>
      <p id="out"></p>
      """));
    _site.MapGet("/popup", () => Page("""<a href="/static" target="_blank">open popup</a>"""));
    _site.MapGet("/dialog", () => Page("""<button onclick="alert('hello from dialog')">Alert me</button>"""));
    _site.MapGet("/hover", () => Page(
      """
      <style>#menu span { display: none } #menu:hover span { display: inline }</style>
      <div id="menu">Menu<span> revealed-by-hover</span></div>
      """));
    _site.MapGet("/subres", () => Page(
      """
      <h1>Subresource probe</h1>
      <img src="http://10.255.255.1/x.png"
           onerror="document.body.insertAdjacentHTML('beforeend', '<p>subresource-blocked</p>')">
      """));
    _site.MapGet("/mutate", () => Page(
      """
      <div id="t">start</div>
      <script>setInterval(() => { document.getElementById('t').textContent = Date.now(); }, 300);</script>
      """));

    await _site.StartAsync();
    BaseUrl = _site.Urls.First();

    Policy = new UrlPolicy(Options);
    Tabs = new TabManager(Options);
    Host = new BrowserHost(Options, Policy, Tabs, NullLogger<BrowserHost>.Instance);
  }

  public async Task DisposeAsync()
  {
    await Host.DisposeAsync();
    if (_site is not null)
    {
      await _site.StopAsync();
    }
  }

  private static IResult Page(string body)
  {
    return Results.Content($"<!doctype html><html><body>{body}</body></html>", "text/html");
  }
}

[CollectionDefinition("selain")]
public class SelainCollection : ICollectionFixture<SelainFixture>;
```

- [ ] **Step 6: Write the failing aria-ref smoke test**

`src/toimi.tools.selain.Tests/Integration/BrowserToolTests.cs`:

```csharp
using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class BrowserToolTests(SelainFixture fx)
{
  [ChromiumFact]
  public async Task AriaSnapshot_produces_refs_that_resolve_to_elements()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    try
    {
      await page.GotoAsync($"{fx.BaseUrl}/form");
      var snapshot = await page.AriaSnapshotAsync(new() { Ref = true });

      Assert.Contains("[ref=", snapshot);

      // Pull the first ref out of the snapshot and resolve it through the selector engine.
      var start = snapshot.IndexOf("[ref=", StringComparison.Ordinal) + "[ref=".Length;
      var elementRef = snapshot[start..snapshot.IndexOf(']', start)];
      Assert.Equal(1, await page.Locator($"aria-ref={elementRef}").CountAsync());
    }
    finally
    {
      await page.CloseAsync();
    }
  }

  [ChromiumFact]
  public async Task Context_page_event_adopts_new_pages_into_tab_manager()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    try
    {
      Assert.NotNull(fx.Tabs.FindByHandle(page));
    }
    finally
    {
      var id = fx.Tabs.FindByHandle(page);
      await page.CloseAsync();
      if (id is { } tabId)
      {
        await fx.Tabs.CloseAsync(tabId);
      }
    }
  }
}
```

- [ ] **Step 7: Run to verify failure, then compile-fix to pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter BrowserToolTests
```

First run before Steps 2–5 compile: FAIL. After implementation: **PASS**. If `AriaSnapshotAsync(new() { Ref = true })` doesn't compile, inspect the installed option type (`PageAriaSnapshotOptions`) and use its ref-enabling member; if the snapshot contains no `[ref=`, check the Playwright release notes for the current ref syntax — fix here before proceeding.

- [ ] **Step 8: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): BrowserHost with route guard, popup adoption, chromium-gated tests"
```

---

### Task 6: Observe tools — browse, snapshot, read_page

**Files:**
- Create: `src/toimi.tools.selain/Tools/ToolGuard.cs`
- Create: `src/toimi.tools.selain/Tools/PageResults.cs`
- Create: `src/toimi.tools.selain/Tools/BrowseTools.cs`
- Test: `src/toimi.tools.selain.Tests/Integration/BrowserToolTests.cs` (extend)

- [ ] **Step 1: Write the failing tests (append to BrowserToolTests)**

Append inside the `BrowserToolTests` class:

```csharp
  private BrowseTools NewBrowseTools()
  {
    return new BrowseTools(fx.Options, fx.Policy, fx.Tabs, fx.Host);
  }

  [ChromiumFact]
  public async Task Browse_returns_title_url_and_ref_snapshot()
  {
    var result = await NewBrowseTools().Browse($"{fx.BaseUrl}/static");
    Assert.Contains("Static page", result);
    Assert.Contains("URL:", result);
    Assert.Contains("[ref=", result);
  }

  [ChromiumFact]
  public async Task Browse_rejects_private_hosts_with_a_friendly_error()
  {
    var result = await NewBrowseTools().Browse("http://10.1.2.3/");
    Assert.Contains("private or internal", result);
  }

  [ChromiumFact]
  public async Task Snapshot_repeat_without_changes_reports_page_unchanged()
  {
    var tools = NewBrowseTools();
    await tools.Browse($"{fx.BaseUrl}/static");
    var second = await tools.Snapshot();
    Assert.Contains("(page unchanged)", second);
  }

  [ChromiumFact]
  public async Task Disabled_kill_switch_short_circuits_tools()
  {
    var offOptions = new SelainOptions { Enabled = false };
    var tools = new BrowseTools(offOptions, fx.Policy, fx.Tabs, fx.Host);
    Assert.Contains("disabled", await tools.Browse($"{fx.BaseUrl}/static"));
  }
```

(The JS-hydration and subresource-abort tests need `wait_for` and therefore live in Task 7's `ActToolTests`, where `ActTools` exists.)

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter BrowserToolTests
```

Expected: FAIL (compile — `BrowseTools` missing).

- [ ] **Step 3: Implement the shared helpers**

`src/toimi.tools.selain/Tools/ToolGuard.cs`:

```csharp
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

internal static class ToolGuard
{
  /// <summary>Non-null message when the global kill switch is off.</summary>
  public static string? Disabled(SelainOptions options)
  {
    return options.Enabled ? null : "Browser tools are disabled (Selain:Enabled=false).";
  }
}
```

`src/toimi.tools.selain/Tools/PageResults.cs`:

```csharp
using Microsoft.Playwright;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

/// <summary>
/// Composes the standard tool result: restart/dialog notices + title/URL + the
/// capped aria snapshot — or "(page unchanged)" when the snapshot hash matches
/// what the agent last saw for this tab.
/// </summary>
internal static class PageResults
{
  public static async Task<string> ComposeAsync(TabManager tabs, BrowserHost host, TabManager.TabEntry tab)
  {
    var page = ((PlaywrightSession)tab.Session).Page;
    var snapshot = await page.AriaSnapshotAsync(new() { Ref = true });
    var hash = SnapshotFormatter.Hash(snapshot);

    string body;
    if (tab.LastShownHash == hash)
    {
      body = "(page unchanged)";
    }
    else
    {
      tab.LastShownHash = hash;
      body = SnapshotFormatter.Truncate(snapshot, SnapshotFormatter.ActionCap);
    }

    var notice = host.ConsumeRestartNotice() ? "Note: browser restarted — all previous tabs were lost.\n" : "";
    var dialog = tabs.TakeDialogNote(tab.Id) is { } note ? note + "\n" : "";
    var title = await page.TitleAsync();
    return $"{notice}{dialog}Title: {title}\nURL: {page.Url}\n\n{body}";
  }
}
```

- [ ] **Step 4: Implement BrowseTools**

`src/toimi.tools.selain/Tools/BrowseTools.cs`:

```csharp
using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class BrowseTools(SelainOptions options, UrlPolicy policy, TabManager tabs, BrowserHost host)
{
  [McpServerTool, Description("Open a URL in the browser's active tab (opening a first tab if none) and return an accessibility snapshot with element refs like [ref=e5], usable with click/type/hover/select_option. Prefer verkko's fetch_url for simple static pages — use browse when a page needs JavaScript, interaction, or a display feed.")]
  public async Task<string> Browse([Description("Absolute http(s) URL")] string url)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    var (ok, error, uri) = policy.Validate(url);
    if (!ok)
    {
      return error!;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      var context = await host.GetContextAsync();
      var active = tabs.Active;
      if (active is null)
      {
        var page = await context.NewPageAsync();
        var id = tabs.FindByHandle(page) ?? tabs.Adopt(new PlaywrightSession(page));
        tabs.Switch(id);
        active = tabs.Get(id)!;
      }

      var target = ((PlaywrightSession)active.Session).Page;
      try
      {
        await target.GotoAsync(uri!.ToString(), new() { WaitUntil = WaitUntilState.Load, Timeout = 20_000 });
        try
        {
          // Settle: brief network-quiet so late-hydrating SPAs get a chance;
          // pages that poll forever fall through after 3s instead of hanging.
          await target.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
          // Expected on busy pages — snapshot whatever is there.
        }
      }
      catch (TimeoutException)
      {
        return $"Navigation to {url} timed out after 20s.";
      }
      catch (PlaywrightException ex)
      {
        return $"Navigation to {url} failed: {ex.Message}";
      }

      return await PageResults.ComposeAsync(tabs, host, active);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  [McpServerTool, Description("Re-read the active tab: fresh accessibility snapshot with refs. Use after waiting for dynamic content. Returns '(page unchanged)' if nothing differs from what you last saw.")]
  public async Task<string> Snapshot()
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      return await PageResults.ComposeAsync(tabs, host, active);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  [McpServerTool, Description("Plain extracted text of the active tab's page (up to 50K chars) — for reading long articles where the accessibility snapshot is noise.")]
  public async Task<string> ReadPage()
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      var page = ((PlaywrightSession)active.Session).Page;
      var text = await page.InnerTextAsync("body");
      return $"URL: {page.Url}\n\n{SnapshotFormatter.Truncate(text, SnapshotFormatter.ReadCap)}";
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }
}
```

- [ ] **Step 5: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter BrowserToolTests
```

Expected: PASS (smoke tests from Task 5 + the four new tests).

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): browse/snapshot/read_page with token caps and unchanged suppression"
```

---

### Task 7: Act tools — click, hover, type, select_option, press_key, go_back, wait_for

**Files:**
- Create: `src/toimi.tools.selain/Tools/ActTools.cs`
- Test: `src/toimi.tools.selain.Tests/Integration/ActToolTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.selain.Tests/Integration/ActToolTests.cs`:

```csharp
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class ActToolTests(SelainFixture fx)
{
  private BrowseTools Browse => new(fx.Options, fx.Policy, fx.Tabs, fx.Host);
  private ActTools Act => new(fx.Options, fx.Tabs, fx.Host);

  private static string FirstRefMatching(string snapshot, string nearText)
  {
    // Find the snapshot line mentioning nearText and extract its [ref=eN].
    foreach (var line in snapshot.Split('\n'))
    {
      if (line.Contains(nearText, StringComparison.OrdinalIgnoreCase) && line.Contains("[ref="))
      {
        var start = line.IndexOf("[ref=", StringComparison.Ordinal) + "[ref=".Length;
        return line[start..line.IndexOf(']', start)];
      }
    }
    throw new InvalidOperationException($"No ref found near '{nearText}' in snapshot:\n{snapshot}");
  }

  [ChromiumFact]
  public async Task Type_and_click_round_trip_mutates_the_page()
  {
    var snapshot = await Browse.Browse($"{fx.BaseUrl}/form");
    var nameRef = FirstRefMatching(snapshot, "Your name");
    var buttonRef = FirstRefMatching(snapshot, "Send");

    await Act.Type(nameRef, "jari", pressEnter: false);
    var afterClick = await Act.Click(buttonRef);

    Assert.Contains("jari/a", afterClick);
  }

  [ChromiumFact]
  public async Task Select_option_changes_the_selection()
  {
    var snapshot = await Browse.Browse($"{fx.BaseUrl}/form");
    var selectRef = FirstRefMatching(snapshot, "Pick one");
    var buttonRef = FirstRefMatching(snapshot, "Send");

    await Act.SelectOption(selectRef, "b");
    var after = await Act.Click(buttonRef);
    Assert.Contains("/b", after);
  }

  [ChromiumFact]
  public async Task Stale_ref_reports_take_a_new_snapshot()
  {
    await Browse.Browse($"{fx.BaseUrl}/form");
    var result = await Act.Click("e9999");
    Assert.Contains("not found", result);
    Assert.Contains("snapshot", result);
  }

  [ChromiumFact]
  public async Task Hover_reveals_hover_content()
  {
    var snapshot = await Browse.Browse($"{fx.BaseUrl}/hover");
    var menuRef = FirstRefMatching(snapshot, "Menu");
    await Act.Hover(menuRef);
    var text = await Browse.ReadPage();
    Assert.Contains("revealed-by-hover", text);
  }

  [ChromiumFact]
  public async Task Dialogs_are_auto_dismissed_and_reported()
  {
    var snapshot = await Browse.Browse($"{fx.BaseUrl}/dialog");
    var buttonRef = FirstRefMatching(snapshot, "Alert me");
    var result = await Act.Click(buttonRef);
    Assert.Contains("dialog auto-dismissed", result);
    Assert.Contains("hello from dialog", result);
  }

  [ChromiumFact]
  public async Task Popup_click_adopts_the_new_tab()
  {
    var before = fx.Tabs.Count;
    var snapshot = await Browse.Browse($"{fx.BaseUrl}/popup");
    var linkRef = FirstRefMatching(snapshot, "open popup");
    await Act.Click(linkRef);
    await Act.WaitFor(null, 1);
    Assert.True(fx.Tabs.Count > before, "popup page was not adopted as a tab");
  }

  [ChromiumFact]
  public async Task Read_page_sees_js_rendered_content_that_fetch_cannot()
  {
    await Browse.Browse($"{fx.BaseUrl}/js");
    await Act.WaitFor("Hydrated content arrived", 10);
    var text = await Browse.ReadPage();
    Assert.Contains("Hydrated content arrived", text);
  }

  [ChromiumFact]
  public async Task Private_host_subresources_are_aborted_by_the_route_guard()
  {
    await Browse.Browse($"{fx.BaseUrl}/subres");
    await Act.WaitFor("subresource-blocked", 10);
    var text = await Browse.ReadPage();
    Assert.Contains("subresource-blocked", text);
  }

  [ChromiumFact]
  public async Task Go_back_returns_to_the_previous_page()
  {
    await Browse.Browse($"{fx.BaseUrl}/static");
    await Browse.Browse($"{fx.BaseUrl}/form");
    var result = await Act.GoBack();
    Assert.Contains("/static", result);
  }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter ActToolTests
```

Expected: FAIL (compile — `ActTools` missing).

- [ ] **Step 3: Implement ActTools**

`src/toimi.tools.selain/Tools/ActTools.cs`:

```csharp
using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class ActTools(SelainOptions options, TabManager tabs, BrowserHost host)
{
  private const int ActionTimeoutMs = 10_000;

  [McpServerTool, Description("Click the element with the given snapshot ref (e.g. e5) in the active tab. Returns the resulting page snapshot.")]
  public Task<string> Click([Description("Element ref from the snapshot, e.g. e5")] string elementRef)
  {
    return WithElementAsync(elementRef, locator => locator.ClickAsync(new() { Timeout = ActionTimeoutMs }), settleAfter: true);
  }

  [McpServerTool, Description("Hover the element with the given snapshot ref — for menus/content that reveal on hover.")]
  public Task<string> Hover([Description("Element ref from the snapshot")] string elementRef)
  {
    return WithElementAsync(elementRef, locator => locator.HoverAsync(new() { Timeout = ActionTimeoutMs }), settleAfter: false);
  }

  [McpServerTool, Description("Type text into the element with the given snapshot ref. Optionally press Enter afterwards.")]
  public Task<string> Type(
    [Description("Element ref from the snapshot")] string elementRef,
    [Description("Text to type")] string text,
    [Description("Press Enter after typing (submits many forms)")] bool pressEnter = false)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      await locator.FillAsync(text, new() { Timeout = ActionTimeoutMs });
      if (pressEnter)
      {
        await locator.PressAsync("Enter", new() { Timeout = ActionTimeoutMs });
      }
    }, settleAfter: pressEnter);
  }

  [McpServerTool, Description("Select an option (by value or label) in the <select> with the given snapshot ref.")]
  public Task<string> SelectOption(
    [Description("Element ref from the snapshot")] string elementRef,
    [Description("Option value or visible label")] string value)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      var byValue = await locator.SelectOptionAsync(new SelectOptionValue { Value = value }, new() { Timeout = ActionTimeoutMs });
      if (byValue.Count == 0)
      {
        await locator.SelectOptionAsync(new SelectOptionValue { Label = value }, new() { Timeout = ActionTimeoutMs });
      }
    }, settleAfter: false);
  }

  [McpServerTool, Description("Press a keyboard key in the active tab (e.g. Escape, PageDown to scroll, ArrowDown, Enter).")]
  public Task<string> PressKey([Description("Key name, e.g. Escape, PageDown, Enter")] string key)
  {
    return WithPageAsync(async page =>
    {
      await page.Keyboard.PressAsync(key);
      return null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Navigate the active tab back to the previous page.")]
  public Task<string> GoBack()
  {
    return WithPageAsync(async page =>
    {
      await page.GoBackAsync(new() { Timeout = ActionTimeoutMs, WaitUntil = WaitUntilState.Load });
      return null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Wait for text to appear in the active tab (or just wait N seconds if no text given). Max 30 seconds. Use for slow/lazy-loading content, then take a snapshot.")]
  public async Task<string> WaitFor(
    [Description("Text to wait for (optional)")] string? text = null,
    [Description("Seconds to wait (default 15, max 30)")] int? seconds = null)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    var budget = Math.Clamp(seconds ?? 15, 1, 30);
    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      var page = ((PlaywrightSession)active.Session).Page;
      if (text is not null)
      {
        try
        {
          await page.GetByText(text).First.WaitForAsync(new() { Timeout = budget * 1000 });
        }
        catch (TimeoutException)
        {
          return $"Text \"{text}\" did not appear within {budget}s.";
        }
      }
      else
      {
        await Task.Delay(TimeSpan.FromSeconds(budget));
      }

      return await PageResults.ComposeAsync(tabs, host, active);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  private Task<string> WithElementAsync(string elementRef, Func<ILocator, Task> action, bool settleAfter)
  {
    return WithPageAsync(async page =>
    {
      var locator = page.Locator($"aria-ref={elementRef}");
      if (await locator.CountAsync() == 0)
      {
        return $"ref '{elementRef}' not found — the page changed; take a new snapshot.";
      }

      await action(locator);
      return null;
    }, settleAfter);
  }

  /// <summary>Shared action wrapper: gate, lock, run, settle, compose result.</summary>
  private async Task<string> WithPageAsync(Func<IPage, Task<string?>> action, bool settleAfter)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      var page = ((PlaywrightSession)active.Session).Page;
      try
      {
        if (await action(page) is { } shortCircuit)
        {
          return shortCircuit;
        }

        if (settleAfter)
        {
          // A click/submit may navigate; give the new document a moment.
          try
          {
            await page.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = 5_000 });
          }
          catch (TimeoutException)
          {
            // Still loading — snapshot what's there.
          }
        }
      }
      catch (TimeoutException)
      {
        return $"Action timed out after {ActionTimeoutMs / 1000}s — the element may be covered or the page busy; take a new snapshot.";
      }
      catch (PlaywrightException ex)
      {
        return $"Action failed: {ex.Message}";
      }

      return await PageResults.ComposeAsync(tabs, host, active);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter "ActToolTests|BrowserToolTests"
```

Expected: PASS. (The dialog test depends on the popup-adoption dialog wiring from Task 5's `BrowserHost`; the dialog note lands on the tab that raised it.)

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): act tools with stale-ref messaging, dialog notes, popup adoption"
```

---

### Task 8: Tabs tool

**Files:**
- Create: `src/toimi.tools.selain/Tools/TabTools.cs`
- Test: `src/toimi.tools.selain.Tests/Integration/TabToolTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.selain.Tests/Integration/TabToolTests.cs`:

```csharp
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class TabToolTests(SelainFixture fx)
{
  private TabTools Tool => new(fx.Options, fx.Policy, fx.Tabs, fx.Host);

  [ChromiumFact]
  public async Task New_list_switch_close_lifecycle()
  {
    var created = await Tool.Tabs("new", url: $"{fx.BaseUrl}/static");
    Assert.Contains("Static page", created);

    var list = await Tool.Tabs("list");
    Assert.Contains("/tabs/", list);           // viewer URL present
    Assert.Contains("https://toimi.example", list);
    Assert.Contains("[active]", list);

    // Extract the new tab's id from the list output ("- <guid> ..." lines).
    var activeLine = list.Split('\n').First(l => l.Contains("[active]"));
    var id = activeLine.TrimStart('-', ' ').Split(' ')[0];

    var closed = await Tool.Tabs("close", tabId: id);
    Assert.Contains("closed", closed, StringComparison.OrdinalIgnoreCase);
  }

  [ChromiumFact]
  public async Task New_tab_with_viewport_uses_the_given_size()
  {
    var created = await Tool.Tabs("new", url: $"{fx.BaseUrl}/static", width: 800, height: 1280);
    Assert.Contains("800x1280", created);
    var list = await Tool.Tabs("list");
    var activeLine = list.Split('\n').First(l => l.Contains("[active]"));
    var id = activeLine.TrimStart('-', ' ').Split(' ')[0];
    await Tool.Tabs("close", tabId: id);
  }

  [ChromiumFact]
  public async Task Unknown_action_and_bad_ids_report_errors()
  {
    Assert.Contains("Unknown action", await Tool.Tabs("frobnicate"));
    Assert.Contains("not found", await Tool.Tabs("switch", tabId: Guid.NewGuid().ToString()));
    Assert.Contains("Invalid tab id", await Tool.Tabs("switch", tabId: "not-a-guid"));
  }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter TabToolTests
```

Expected: FAIL (compile).

- [ ] **Step 3: Implement TabTools**

`src/toimi.tools.selain/Tools/TabTools.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class TabTools(SelainOptions options, UrlPolicy policy, TabManager tabs, BrowserHost host)
{
  [McpServerTool, Description("Manage browser tabs. action=list|new|switch|close. 'new' accepts an optional url and viewport width/height (default 1280x720 — set to a display's size when the tab will be streamed to it). 'list' shows each tab's id, title, URL, and its viewer URL for ruutu's webview template. Actions apply to tabId (a GUID from 'list').")]
  public async Task<string> Tabs(
    [Description("list | new | switch | close")] string action,
    [Description("Tab id (GUID from 'list') — required for switch/close")] string? tabId = null,
    [Description("URL to open in the new tab (action=new)")] string? url = null,
    [Description("Viewport width for the new tab")] int? width = null,
    [Description("Viewport height for the new tab")] int? height = null)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      switch (action)
      {
        case "list":
          return ListTabs();
        case "new":
          return await NewTabAsync(url, width, height);
        case "switch":
        case "close":
        {
          if (!Guid.TryParse(tabId, out var id))
          {
            return $"Invalid tab id '{tabId}' — use a GUID from tabs(list).";
          }

          if (action == "switch")
          {
            return tabs.Switch(id) ? $"Switched to tab {id}." : $"Tab {id} not found.";
          }

          return await tabs.CloseAsync(id) ? $"Tab {id} closed." : $"Tab {id} not found.";
        }
        default:
          return $"Unknown action '{action}' — use list, new, switch, or close.";
      }
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  private string ListTabs()
  {
    var entries = tabs.List();
    if (entries.Count == 0)
    {
      return "No open tabs.";
    }

    var sb = new StringBuilder();
    foreach (var entry in entries)
    {
      var marker = entry.Id == tabs.Active?.Id ? " [active]" : "";
      sb.AppendLine($"- {entry.Id}{marker} {entry.Session.Url}");
      sb.AppendLine($"  viewer: {tabs.ViewerUrl(entry.Id)}");
    }

    return sb.ToString().TrimEnd();
  }

  private async Task<string> NewTabAsync(string? url, int? width, int? height)
  {
    var context = await host.GetContextAsync();
    var page = await context.NewPageAsync();
    var id = tabs.FindByHandle(page) ?? tabs.Adopt(new PlaywrightSession(page));
    tabs.Switch(id);

    var size = "";
    if (width is { } w && height is { } h)
    {
      await page.SetViewportSizeAsync(w, h);
      size = $" (viewport {w}x{h})";
    }

    if (url is null)
    {
      return $"Opened tab {id}{size}. Viewer: {tabs.ViewerUrl(id)}";
    }

    var (ok, error, uri) = policy.Validate(url);
    if (!ok)
    {
      return error!;
    }

    try
    {
      await page.GotoAsync(uri!.ToString(), new() { WaitUntil = WaitUntilState.Load, Timeout = 20_000 });
    }
    catch (TimeoutException)
    {
      return $"Opened tab {id}{size}, but navigation to {url} timed out after 20s.";
    }
    catch (PlaywrightException ex)
    {
      return $"Opened tab {id}{size}, but navigation failed: {ex.Message}";
    }

    var active = tabs.Get(id)!;
    var result = await PageResults.ComposeAsync(tabs, host, active);
    return $"Opened tab {id}{size}. Viewer: {tabs.ViewerUrl(id)}\n{result}";
  }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter TabToolTests
```

Expected: PASS.

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): tabs tool with viewport sizing and viewer URLs"
```

---

### Task 9: Vision spike — does an MCP image block reach the model?

**Spec §3 requires this before building the `screenshot` tool.** Outcome is a *decision*, recorded in the spec.

**Files:**
- Temporary: `src/toimi.tools.selain/Tools/VisionProbeTool.cs` (deleted at the end of this task)
- Possibly modify: `src/toimi.web/appsettings.Development.json`
- Modify: `docs/superpowers/specs/2026-07-29-selain-headless-browser-design.md` (record the outcome)

- [ ] **Step 1: Read how tool results flow today**

Read `src/toimi.core/ResilientMcpTool.cs` (InvokeAsync passes through the MCP SDK's `AIFunction` result untouched) and `src/toimi.core/ToimiClientFactory.cs` / `src/toimi.core/ToolCallNotifier.cs` to see what the chat pipeline does with a function result object before it reaches the OpenAI request. Note where a non-string result would be serialized.

- [ ] **Step 2: Add the temporary probe tool**

`src/toimi.tools.selain/Tools/VisionProbeTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace toimi.tools.selain.Tools;

/// <summary>TEMPORARY spike probe — delete before merging. A 5x5 solid red PNG.</summary>
[McpServerToolType]
public class VisionProbeTool
{
  private const string RedPngBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAFCAYAAACNbyblAAAAFUlEQVR4nGP8z8DwnwEPYMKnYBgpAADLMQMH1DJQuwAAAABJRU5ErkJggg==";

  [McpServerTool, Description("Spike probe: returns a small solid-color image. Ask the model what color it sees.")]
  public CallToolResult VisionProbe()
  {
    return new CallToolResult
    {
      Content = [new ImageContentBlock { Data = RedPngBase64, MimeType = "image/png" }]
    };
  }
}
```

- [ ] **Step 3: Run selain + toimi.web locally against it**

```bash
ASPNETCORE_URLS=http://localhost:5250 mise exec dotnet -- dotnet run --project src/toimi.tools.selain &
```

Create/edit `src/toimi.web/appsettings.Development.json` so `Toimi:McpServers` contains ONLY:

```json
{
  "Toimi": {
    "McpServers": [
      { "Name": "selain", "Transport": "Http", "Url": "http://localhost:5250/sse" }
    ]
  }
}
```

Export the OpenAI settings from `toimi.env` (`OPENAI_API_KEY`, `OPENAI_MODEL` — check `src/toimi.web/appsettings.json` for the exact config key names the factory binds, e.g. `OpenAI__ApiKey`) and run:

```bash
mise exec dotnet -- dotnet run --project src/toimi.web
```

In the chat UI ask: *"Call the vision_probe tool and tell me what color the image is."*

- [ ] **Step 4: Record the verdict**

- **Model answers "red" →** image passthrough works. Record in the spec under the spike paragraph: "Spike verified 2026-MM-DD: image blocks reach the model." Task 10 proceeds as written.
- **Model can't see it (describes JSON/base64 text, or errors) →** flattening confirmed. Record that, and in Task 10 still build the tool exactly as specified (endpoints and ruutu are unaffected), but set its `Description` to: `"Screenshot the active tab as PNG. NOTE: the chat pipeline currently flattens images — use this only via the /tabs endpoints; prefer snapshot/read_page for content."` Then add a follow-up entry to the spec's Out-of-scope list: "toimi.core image-content passthrough (spike found flattening at <the location found in Step 1>)".

- [ ] **Step 5: Clean up and commit**

```bash
rm src/toimi.tools.selain/Tools/VisionProbeTool.cs
git checkout -- src/toimi.web/appsettings.Development.json 2>/dev/null || rm -f src/toimi.web/appsettings.Development.json
git add docs/superpowers/specs/2026-07-29-selain-headless-browser-design.md
git commit -m "docs(selain): record vision-spike verdict on image tool results"
```

---

### Task 10: Screenshot tool + screenshot endpoint

**Files:**
- Create: `src/toimi.tools.selain/Tools/ScreenshotTool.cs`
- Create: `src/toimi.tools.selain/Endpoints/TabEndpoints.cs` (screenshot + view routes; stream route added in Task 11)
- Create: `src/toimi.tools.selain/Streaming/ViewerPage.cs`
- Modify: `src/toimi.tools.selain/Program.cs`
- Test: `src/toimi.tools.selain.Tests/Integration/EndpointTests.cs`

- [ ] **Step 1: Write the failing endpoint tests**

`src/toimi.tools.selain.Tests/Integration/EndpointTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

/// <summary>
/// Boots the real selain app in-memory (WebApplicationFactory) with loopback
/// fixtures allowed, then exercises the HTTP surface displays will use.
/// </summary>
public class EndpointTests : IClassFixture<EndpointTests.SelainAppFactory>, IClassFixture<FixtureSite>
{
  public sealed class SelainAppFactory : WebApplicationFactory<Program>
  {
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
      builder.UseSetting("Selain:PublicBaseUrl", "https://toimi.example");
      builder.UseSetting("Selain:AllowedPrivateHosts:0", "127.0.0.1");
      builder.UseSetting("Selain:AllowedPrivateHosts:1", "localhost");
    }
  }

  private readonly SelainAppFactory _app;
  private readonly FixtureSite _site;

  public EndpointTests(SelainAppFactory app, FixtureSite site)
  {
    _app = app;
    _site = site;
  }

  private async Task<Guid> OpenTabAsync()
  {
    var services = _app.Services;
    var tools = new BrowseTools(
      services.GetRequiredService<SelainOptions>(),
      services.GetRequiredService<UrlPolicy>(),
      services.GetRequiredService<TabManager>(),
      services.GetRequiredService<BrowserHost>());
    await tools.Browse($"{_site.BaseUrl}/mutate");
    return services.GetRequiredService<TabManager>().Active!.Id;
  }

  [ChromiumFact]
  public async Task Screenshot_endpoint_returns_png_for_a_known_tab_and_404_otherwise()
  {
    var id = await OpenTabAsync();
    var client = _app.CreateClient();

    var ok = await client.GetAsync($"/tabs/{id}/screenshot");
    Assert.True(ok.IsSuccessStatusCode);
    Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
    var bytes = await ok.Content.ReadAsByteArrayAsync();
    // PNG magic bytes.
    Assert.Equal(0x89, bytes[0]);
    Assert.Equal((byte)'P', bytes[1]);

    var missing = await client.GetAsync($"/tabs/{Guid.NewGuid()}/screenshot");
    Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
  }

  [ChromiumFact]
  public async Task Viewer_page_is_self_contained_html_referencing_the_tab()
  {
    var id = await OpenTabAsync();
    var client = _app.CreateClient();
    var response = await client.GetAsync($"/tabs/{id}/view");
    var html = await response.Content.ReadAsStringAsync();
    Assert.Contains(id.ToString(), html);
    Assert.Contains("/stream", html);
    Assert.Contains("/screenshot", html);
    // Self-contained: no external scripts, stylesheets, or fonts.
    Assert.DoesNotContain("<script src", html);
    Assert.DoesNotContain("<link", html);
  }
}

/// <summary>Standalone fixture site for EndpointTests (SelainFixture's browser stack is separate from the app factory's).</summary>
public sealed class FixtureSite : IAsyncLifetime
{
  private Microsoft.AspNetCore.Builder.WebApplication? _site;
  public string BaseUrl { get; private set; } = "";

  public async Task InitializeAsync()
  {
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    _site = builder.Build();
    _site.MapGet("/mutate", () => Microsoft.AspNetCore.Http.Results.Content(
      """<!doctype html><html><body><div id="t">start</div><script>setInterval(() => { document.getElementById('t').textContent = Date.now(); }, 300);</script></body></html>""",
      "text/html"));
    await _site.StartAsync();
    BaseUrl = _site.Urls.First();
  }

  public async Task DisposeAsync()
  {
    if (_site is not null)
    {
      await _site.StopAsync();
    }
  }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter EndpointTests
```

Expected: FAIL (compile / 404s — endpoints missing).

- [ ] **Step 3: Implement ViewerPage**

`src/toimi.tools.selain/Streaming/ViewerPage.cs`:

```csharp
namespace toimi.tools.selain.Streaming;

/// <summary>
/// Self-contained viewer (inline CSS/JS, zero external assets): paints screencast
/// frames onto a canvas; after 3 failed WebSocket attempts falls back to polling
/// the screenshot endpoint every 15s. Embedded by ruutu's webview iframe template.
/// </summary>
public static class ViewerPage
{
  public static string Html(Guid tabId)
  {
    return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>selain viewer</title>
<style>html,body{margin:0;height:100%;background:#000}canvas,img{display:block;width:100%;height:100%;object-fit:contain}</style>
</head>
<body>
<canvas id="c"></canvas><img id="f" style="display:none" alt="tab screenshot">
<script>
const id = "{{tabId}}";
const canvas = document.getElementById("c"), ctx = canvas.getContext("2d"), img = document.getElementById("f");
let failures = 0;
function connect() {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const ws = new WebSocket(proto + "://" + location.host + "/tabs/" + id + "/stream");
  ws.binaryType = "blob";
  ws.onmessage = async (e) => {
    failures = 0;
    const bmp = await createImageBitmap(e.data);
    canvas.width = bmp.width; canvas.height = bmp.height;
    ctx.drawImage(bmp, 0, 0);
    bmp.close();
  };
  ws.onclose = () => {
    failures++;
    if (failures >= 3) { startPolling(); } else { setTimeout(connect, 2000 * failures); }
  };
}
function startPolling() {
  canvas.style.display = "none"; img.style.display = "block";
  const refresh = () => { img.src = "/tabs/" + id + "/screenshot?t=" + Date.now(); };
  refresh(); setInterval(refresh, 15000);
}
connect();
</script>
</body>
</html>
""";
  }
}
```

- [ ] **Step 4: Implement TabEndpoints (screenshot + view) and the screenshot tool**

`src/toimi.tools.selain/Endpoints/TabEndpoints.cs`:

```csharp
using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Streaming;

namespace toimi.tools.selain.Endpoints;

/// <summary>
/// Display-facing HTTP surface. The unguessable tab GUID is the capability:
/// endpoints only answer for tabs the agent opened, and ids die with the tab.
/// </summary>
public static class TabEndpoints
{
  public static void MapTabEndpoints(this WebApplication app)
  {
    app.MapGet("/tabs/{id:guid}/screenshot", async (Guid id, TabManager tabs) =>
    {
      if (tabs.Get(id) is not { } tab)
      {
        return Results.NotFound();
      }

      var page = ((PlaywrightSession)tab.Session).Page;
      var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png });
      return Results.File(bytes, "image/png");
    });

    app.MapGet("/tabs/{id:guid}/view", (Guid id, TabManager tabs) =>
      tabs.Get(id) is null ? Results.NotFound() : Results.Content(ViewerPage.Html(id), "text/html"));
  }
}
```

`src/toimi.tools.selain/Tools/ScreenshotTool.cs` (adjust the Description per the Task 9 verdict):

```csharp
using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class ScreenshotTool(SelainOptions options, TabManager tabs)
{
  [McpServerTool, Description("Screenshot the active tab as a PNG image — for pages whose visual layout matters or when the snapshot is unclear.")]
  public async Task<CallToolResult> Screenshot(
    [Description("Capture the full scrollable page instead of just the viewport")] bool fullPage = false)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return Text(off);
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return Text("No open tab — use browse first.");
      }

      var page = ((PlaywrightSession)active.Session).Page;
      var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png, FullPage = fullPage });
      return new CallToolResult
      {
        Content = [new ImageContentBlock { Data = Convert.ToBase64String(bytes), MimeType = "image/png" }]
      };
    }
    catch (PlaywrightException ex)
    {
      return Text($"Screenshot failed: {ex.Message}");
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  private static CallToolResult Text(string message)
  {
    return new CallToolResult { Content = [new TextContentBlock { Text = message }] };
  }
}
```

In `src/toimi.tools.selain/Program.cs`, before `app.Run()` add:

```csharp
app.MapTabEndpoints();
```

with `using toimi.tools.selain.Endpoints;` at the top.

- [ ] **Step 5: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter EndpointTests
```

Expected: PASS.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): screenshot tool, per-tab screenshot endpoint, viewer page"
```

---

### Task 11: CDP screencast streaming

The CDP event API surface (`ICDPSession.Event(...).OnEvent`, payload as `JsonElement?`) is the one remaining API this plan assumes — verify against the installed package here and adapt member names if needed (the CDP protocol messages themselves — `Page.startScreencast`, `screencastFrame`, `screencastFrameAck` — are fixed by Chromium).

**Files:**
- Create: `src/toimi.tools.selain/Streaming/ScreencastService.cs`
- Modify: `src/toimi.tools.selain/Endpoints/TabEndpoints.cs`
- Modify: `src/toimi.tools.selain/Program.cs` (`UseWebSockets`, register service)
- Test: `src/toimi.tools.selain.Tests/Integration/EndpointTests.cs` (extend)

- [ ] **Step 1: Write the failing test (append to EndpointTests)**

```csharp
  [ChromiumFact]
  public async Task Stream_delivers_screencast_frames_for_a_mutating_page()
  {
    var id = await OpenTabAsync();
    var wsClient = _app.Server.CreateWebSocketClient();
    var ws = await wsClient.ConnectAsync(
      new Uri(_app.Server.BaseAddress, $"/tabs/{id}/stream"), CancellationToken.None);

    var buffer = new byte[512 * 1024];
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await ws.ReceiveAsync(buffer, cts.Token);

    Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, result.MessageType);
    Assert.True(result.Count > 100, "expected a JPEG frame of nontrivial size");
    // JPEG magic bytes.
    Assert.Equal(0xFF, buffer[0]);
    Assert.Equal(0xD8, buffer[1]);

    await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
  }
```

- [ ] **Step 2: Run to verify failure**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter Stream_delivers
```

Expected: FAIL (connect refused / 404 — no WS route).

- [ ] **Step 3: Implement ScreencastService**

`src/toimi.tools.selain/Streaming/ScreencastService.cs`:

```csharp
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Playwright;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Streaming;

/// <summary>
/// Relays Chromium's CDP Page.startScreencast JPEG frames to a WebSocket.
/// Frames only flow when the page repaints, so an idle tab costs nothing.
/// The bounded channel drops stale frames when the socket is slower than the
/// page (a live view wants the newest frame, not a backlog).
/// </summary>
public sealed class ScreencastService(BrowserHost host, ILogger<ScreencastService> logger)
{
  public async Task StreamAsync(IPage page, WebSocket socket, CancellationToken ct)
  {
    var cdp = await page.Context.NewCDPSessionAsync(page);
    var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    {
      FullMode = BoundedChannelFullMode.DropOldest
    });

    void OnFrame(object? sender, JsonElement? payload)
    {
      if (payload is not { } evt)
      {
        return;
      }

      var data = evt.GetProperty("data").GetString();
      var sessionId = evt.GetProperty("sessionId").GetInt32();
      if (data is not null)
      {
        frames.Writer.TryWrite(Convert.FromBase64String(data));
      }

      _ = cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = sessionId });
    }

    cdp.Event("Page.screencastFrame").OnEvent += OnFrame;
    host.StreamStarted();
    try
    {
      await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
      {
        ["format"] = "jpeg",
        ["quality"] = 60
      });

      await foreach (var frame in frames.Reader.ReadAllAsync(ct))
      {
        await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client went away or tab closed — normal end of stream.
    }
    catch (WebSocketException ex)
    {
      logger.LogDebug(ex, "Screencast socket closed.");
    }
    finally
    {
      host.StreamEnded();
      cdp.Event("Page.screencastFrame").OnEvent -= OnFrame;
      try
      {
        await cdp.SendAsync("Page.stopScreencast");
        await cdp.DetachAsync();
      }
      catch (PlaywrightException)
      {
        // Page/browser already gone.
      }
    }
  }
}
```

- [ ] **Step 4: Wire the WS route and service**

In `src/toimi.tools.selain/Endpoints/TabEndpoints.cs`, add inside `MapTabEndpoints` (and `using toimi.tools.selain.Streaming;` is already present):

```csharp
    app.Map("/tabs/{id:guid}/stream", async (HttpContext context, Guid id, TabManager tabs, ScreencastService screencast) =>
    {
      if (!context.WebSockets.IsWebSocketRequest)
      {
        return Results.BadRequest("WebSocket endpoint.");
      }

      if (tabs.Get(id) is not { } tab)
      {
        return Results.NotFound();
      }

      using var socket = await context.WebSockets.AcceptWebSocketAsync();
      await screencast.StreamAsync(((PlaywrightSession)tab.Session).Page, socket, context.RequestAborted);
      return Results.Empty;
    });
```

In `src/toimi.tools.selain/Program.cs`: register `builder.Services.AddSingleton<toimi.tools.selain.Streaming.ScreencastService>();` and add `app.UseWebSockets();` before `app.MapToimiMcp();`.

- [ ] **Step 5: Run to verify pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests --filter EndpointTests
```

Expected: PASS (all endpoint tests). If `cdp.Event(...).OnEvent` doesn't compile, check `ICDPSession` members in the installed package (the event-subscription shape has been stable but verify) and adapt.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
mise exec dotnet -- dotnet format src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --verbosity minimal
git add -A src/toimi.tools.selain src/toimi.tools.selain.Tests
git commit -m "feat(selain): CDP screencast relay over per-tab WebSocket"
```

---

### Task 12: Idle shutdown service

**Files:**
- Create: `src/toimi.tools.selain/Browser/IdleShutdownService.cs`
- Modify: `src/toimi.tools.selain/Program.cs`

(`ShutdownIfIdleAsync`'s conditions were built and unit-covered via `BrowserHost` in Task 5; this task adds the timer loop — thin enough that the integration suite passing is the test.)

- [ ] **Step 1: Implement**

`src/toimi.tools.selain/Browser/IdleShutdownService.cs`:

```csharp
namespace toimi.tools.selain.Browser;

/// <summary>
/// Closes the browser when nothing has used it for Selain:IdleShutdownMinutes —
/// a weeks-running Chromium slowly leaks memory, and relaunch is lazy anyway.
/// </summary>
public sealed class IdleShutdownService(BrowserHost host) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
      await host.ShutdownIfIdleAsync();
    }
  }
}
```

In `src/toimi.tools.selain/Program.cs` add `builder.Services.AddHostedService<IdleShutdownService>();` next to the other Browser registrations.

- [ ] **Step 2: Build + full selain test suite still green**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.selain.Tests
```

Expected: PASS.

- [ ] **Step 3: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.selain/toimi.tools.selain.csproj --verbosity minimal
git add -A src/toimi.tools.selain
git commit -m "feat(selain): idle browser shutdown"
```

---### Task 13: Dockerfile

**Files:**
- Create: `src/toimi.tools.selain/Dockerfile`

- [ ] **Step 1: Confirm the Microsoft.Playwright package version**

```bash
grep Playwright src/toimi.tools.selain/toimi.tools.selain.csproj
```

The runtime image tag below MUST be `v<that version>-noble` (browser revision is coupled to the driver version).

- [ ] **Step 2: Write the Dockerfile**

`src/toimi.tools.selain/Dockerfile` (substitute the real version for `1.54.0` twice):

```dockerfile
# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.tools.selain/Dockerfile -t <registry>/<image>:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.selain/toimi.tools.selain.csproj src/toimi.tools.selain/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.selain/toimi.tools.selain.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.selain/toimi.tools.selain.csproj -c Release -o /app

# Runtime: Playwright's image ships Chromium + every OS dep at /ms-playwright.
# The tag MUST match the Microsoft.Playwright package version.
FROM mcr.microsoft.com/playwright/dotnet:v1.54.0-noble
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.selain.dll"]
```

- [ ] **Step 3: Verify the runtime image carries .NET 10**

```bash
docker run --rm --entrypoint dotnet mcr.microsoft.com/playwright/dotnet:v1.54.0-noble --list-runtimes
```

Expected: a line starting `Microsoft.AspNetCore.App 10.`.
**If .NET 10 is absent** (image still on an older runtime), overlay it — insert after the `FROM …playwright/dotnet…` line:

```dockerfile
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0.10 /usr/share/dotnet /usr/share/dotnet
```

and re-run the check against a locally built image (`docker build -f src/toimi.tools.selain/Dockerfile -t selain-check . && docker run --rm --entrypoint dotnet selain-check --list-runtimes`).

- [ ] **Step 4: Build the image**

```bash
docker build -f src/toimi.tools.selain/Dockerfile -t selain-local .
docker run --rm -d -p 18080:8080 --name selain-smoke selain-local
sleep 3 && curl -sf http://localhost:18080/health && echo OK
docker rm -f selain-smoke
```

Expected: `OK`. (Skip this step if Docker isn't available on the machine — note it for the user.)

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.selain/Dockerfile
git commit -m "feat(selain): Dockerfile on the Playwright runtime image"
```

---

### Task 14: Kubernetes manifests

**Files:**
- Create: `k8s/base/tools-selain/deployment.yaml`
- Create: `k8s/base/tools-selain/service.yaml`
- Create: `k8s/base/tools-selain/ingress.yaml`
- Create: `k8s/base/tools-selain/networkpolicy.yaml`
- Create: `k8s/base/tools-selain/kustomization.yaml`
- Modify: `k8s/base/kustomization.yaml`
- Create: `k8s/overlays/server/tls/selain-ingress-patch.yaml`
- Modify: `k8s/overlays/server/kustomization.yaml`

- [ ] **Step 1: Write the manifests**

`k8s/base/tools-selain/deployment.yaml`:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: toimi-tools-selain
  namespace: apps
  labels:
    app: toimi-tools-selain
spec:
  replicas: 1
  selector:
    matchLabels:
      app: toimi-tools-selain
  template:
    metadata:
      labels:
        app: toimi-tools-selain
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 1654  # `app` user in the .NET base images (APP_UID)
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: toimi-tools-selain
          image: ${IMAGE_REGISTRY}/toimi-tools-selain:latest
          ports:
            - containerPort: 8080
          env:
            - name: Selain__PublicBaseUrl
              value: "https://${TOIMI_HOST}"
            - name: HOME
              value: /tmp  # readOnlyRootFilesystem: Chromium + data-protection writes land on the emptyDir
          resources:
            requests:
              cpu: 200m
              memory: 512Mi
            limits:
              cpu: "2"
              memory: 2Gi
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop:
                - ALL
          volumeMounts:
            - name: tmp
              mountPath: /tmp
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 3
            periodSeconds: 5
      volumes:
        - name: tmp
          emptyDir:
            sizeLimit: 1Gi
```

`k8s/base/tools-selain/service.yaml`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: toimi-tools-selain
  namespace: apps
spec:
  selector:
    app: toimi-tools-selain
  ports:
    - port: 80
      targetPort: 8080
```

`k8s/base/tools-selain/ingress.yaml` (displays reach the viewer/stream from outside the cluster; `/tabs` is selain's native path so no rewrite is needed):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-tools-selain
  namespace: apps
spec:
  ingressClassName: traefik
  rules:
    - host: ${TOIMI_HOST}
      http:
        paths:
          - path: /tabs
            pathType: Prefix
            backend:
              service:
                name: toimi-tools-selain
                port:
                  number: 80
```

`k8s/base/tools-selain/networkpolicy.yaml`:

```yaml
# SSRF containment (spec §5): the browser may egress to DNS and the public
# internet only — never to cluster services or the local network. Enforced by
# the CNI on k3s (kube-router); kind's default CNI does not enforce
# NetworkPolicy, which is why BrowserHost's route guard exists as a second layer.
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: toimi-tools-selain-egress
  namespace: apps
spec:
  podSelector:
    matchLabels:
      app: toimi-tools-selain
  policyTypes:
    - Egress
  egress:
    - to:
        - namespaceSelector: {}
          podSelector:
            matchLabels:
              k8s-app: kube-dns
      ports:
        - protocol: UDP
          port: 53
        - protocol: TCP
          port: 53
    - to:
        - ipBlock:
            cidr: 0.0.0.0/0
            except:
              - 10.0.0.0/8
              - 172.16.0.0/12
              - 192.168.0.0/16
              - 169.254.0.0/16
              - 100.64.0.0/10
```

`k8s/base/tools-selain/kustomization.yaml`:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

resources:
  - deployment.yaml
  - service.yaml
  - ingress.yaml
  - networkpolicy.yaml
```

- [ ] **Step 2: Register in the base kustomization**

In `k8s/base/kustomization.yaml`, add `- tools-selain` to `resources:` (after `- tools-tietue`).

- [ ] **Step 3: Server TLS patch (mirrors ruutu's)**

`k8s/overlays/server/tls/selain-ingress-patch.yaml`:

```yaml
# Shares ${TOIMI_HOST} with the web ingress, so it reuses toimi-web-tls.
# No cert-manager annotation here: the web ingress owns the Certificate
# (two Certificates for one host would fight over the secret).
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-tools-selain
  namespace: apps
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
spec:
  tls:
    - hosts:
        - ${TOIMI_HOST}
      secretName: toimi-web-tls
```

In `k8s/overlays/server/kustomization.yaml`, under `patches:` add:

```yaml
  - path: tls/selain-ingress-patch.yaml
```

- [ ] **Step 4: Verify manifests lint and build**

```bash
yamllint -c .yamllint.yaml k8s/base/tools-selain/ k8s/overlays/server/tls/selain-ingress-patch.yaml
```

Expected: clean. (`kubectl kustomize` is unavailable on this machine — the user validates cluster-side; note it in the task report. If `yamllint` is also unavailable locally, note that too and rely on Task 16's `scripts/lint.sh` run or user verification.)

- [ ] **Step 5: Commit**

```bash
git add k8s/base/tools-selain k8s/base/kustomization.yaml k8s/overlays/server
git commit -m "feat(selain): k8s manifests with egress NetworkPolicy and viewer ingress"
```

---

### Task 15: Registration — McpServers, verkko cross-reference, CLAUDE.md

**Files:**
- Modify: `src/toimi.web/appsettings.json`
- Modify: `src/toimi.tools.tietue/appsettings.json`
- Modify: `src/toimi.tools.verkko/Tools/FetchUrlTool.cs:10`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add selain to both McpServers lists**

In BOTH `src/toimi.web/appsettings.json` and `src/toimi.tools.tietue/appsettings.json`, append to the `Toimi:McpServers` array:

```json
    {
      "Name": "selain",
      "Transport": "Http",
      "Url": "http://toimi-tools-selain.apps.svc.cluster.local/sse"
    }
```

(Match each file's existing property order/indentation.)

- [ ] **Step 2: Cross-reference in verkko's fetch_url description**

In `src/toimi.tools.verkko/Tools/FetchUrlTool.cs` line 10, extend the `Description` string to end with:

```
 If the result looks like an empty shell or says JavaScript is required, use selain's browse tool instead.
```

Full new attribute line:

```csharp
  [McpServerTool, Description("Fetch a URL and extract its text content. Works with web pages (HTML extracted to readable text), JSON APIs, and plain text. Results are cached for 5 minutes. If the result looks like an empty shell or says JavaScript is required, use selain's browse tool instead.")]
```

- [ ] **Step 3: Update CLAUDE.md**

- In the **Pods** section, change the deployable-pods line to:
  `Deployable pods: **tietue, koti, verkko, ruutu, selain** (tool servers) + **toimi.web**.`
- After the **ruutu** entry, add:

```markdown
**selain — Headless browser (Playwright/Chromium).**
- Owns: real-browser page reading (aria snapshots with refs), page actions
  (click/type/hover/select), screenshots, and per-tab display feeds
  (`/tabs/{id}/view` + CDP-screencast stream) that ruutu's `webview` template
  embeds for live pages (e.g. delivery tracking). Stateless: no DB, no PVC;
  tabs die with the pod. SSRF containment = egress NetworkPolicy + request
  routing; `Selain:Enabled` kill switch.
- Extend when: adding browse/act verbs or display-feed behavior.
- Deliberately deferred (design doc): VNC/headful mode, logins + credential
  store. Cost ladder: verkko `fetch_url` first, selain `browse` when a page
  needs JS/interaction.
```

- In **Service DNS**, extend the tools list: `toimi-tools-<x>.apps` (tietue, koti, verkko, ruutu, selain).

- [ ] **Step 4: Verify the verkko change compiles and its tests pass**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.verkko.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.web/appsettings.json src/toimi.tools.tietue/appsettings.json src/toimi.tools.verkko/Tools/FetchUrlTool.cs CLAUDE.md
git commit -m "feat(selain): register MCP server, fetch_url cross-reference, docs"
```

---

### Task 16: Full verification

- [ ] **Step 1: Format the whole solution clean**

```bash
mise exec dotnet -- dotnet format toimi.sln --verbosity minimal
mise exec dotnet -- dotnet format toimi.sln --verify-no-changes --verbosity minimal
```

Expected: second command exits 0. (IDE0046 sometimes needs a manual conditional-expression rewrite — fix any reported file by hand.)

- [ ] **Step 2: Full test suite**

```bash
mise exec dotnet -- dotnet test toimi.sln
```

Expected: all green (docker-gated and chromium-gated tests skip where the daemon/browser is absent — skips are OK, failures are not).

- [ ] **Step 3: Lint**

```bash
scripts/lint.sh
```

Expected: passes for dotnet-format and yamllint; shellcheck is not installed on this machine (no script changes were made — note if it warns).

- [ ] **Step 4: Commit any format fixes**

```bash
git add -A
git commit -m "chore(selain): format and lint fixes" || echo "nothing to fix"
```

- [ ] **Step 5: User-side verification (report, don't attempt here — no cluster on this machine)**

Tell the user the branch is ready and that live verification needs their environment:

1. `scripts/deploy.sh dev tools.selain` (builds the fat image — first build downloads the Playwright runtime image).
2. In toimi chat: "browse example.com and tell me the heading" → expect a real answer.
3. "Open <some tracking URL> in a new browser tab and show it on the display" → tab opens, `tabs(list)` shows the viewer URL, ruutu `webview` shows the live view.
4. On the server env, confirm the NetworkPolicy: from the selain pod, `curl http://toimi-tools-tietue.apps.svc.cluster.local/health` must FAIL; `curl https://example.com` must succeed.

---

## Self-review notes (already applied)

- Spec coverage: 12 tools (Tasks 6–10), token caps + unchanged-hash (3, 6), settle strategy (6), popup adoption + dialogs (5, 7), stale refs (7), viewport (8), vision spike (9), screenshot + viewer + screencast endpoints (10–11), idle shutdown (12), route-guard + NetworkPolicy SSRF layers (5, 14), kill switch (6, tested), Playwright-image runtime (13), registration + fetch_url cross-ref + CLAUDE.md (15). Deviation from spec, intentional: `PublicBaseUrl` reuses `${TOIMI_HOST}` (already in every script's envsubst allowlist) instead of a new toimi.env variable — zero script changes; and `UrlPolicy` uses the already-shared `Toimi.Core.Net.PrivateAddress` instead of copying verkko's `UrlGuard` (the spec's copy note predates finding the shared class).
- Known API-risk points are called out where they're proven: aria-ref (Task 5), CDP events (Task 11), Playwright image runtime version (Task 13) — each with the adaptation instruction at the point of first use.
- Type consistency: `SelainOptions`/`UrlPolicy`/`TabManager`/`BrowserHost` constructor shapes match across all tasks; tools take `(options, [policy,] tabs, host)` consistently; `TabManager.TabEntry` members used by `PageResults` are defined in Task 4.
