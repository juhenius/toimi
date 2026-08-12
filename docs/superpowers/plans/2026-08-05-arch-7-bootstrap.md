# Arch 7: Deepen the Shared Tool-Server Bootstrap (toimi.core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Hosting/ToimiHostingExtensions.cs` today shares only the easy three lines (`AddToimiMcpServer` / `MapToimiMcp` / `MapToimiReadiness`) while the bootstrap that actually varies and drifts is copy-pasted per pod. Three findings close: (1) the required-config bind-and-throw (`GetSection("X").Get<T>() ?? throw new InvalidOperationException("X … is required")`) is copied across koti (`Program.cs:6-7`), tietue (`Program.cs:14-15`, `:32-33`, `:64-66`), ruutu (`Program.cs:9-10`), and web (`Program.cs:40-41`); (2) the DbContext + migrate + seed triad is copied 3× with drift — `AddDbContext(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention())` plus a boot scope calling `MigrateAsync()` in ruutu / tietue / web, where **only tietue guards with `IsRelational()`** (`Program.cs:98`) and ruutu (`Program.cs:38-48`) and web (`Program.cs:65-69`) do not; (3) the "never throw, return a string" MCP tool convention has ~15 hand-rolled try/catch copies — koti 5 blocks across 4 tools, verkko 2, ruutu 9 across 3 tool classes (3 of them with `#pragma warning disable CA1031`), selain having the only real abstraction (`Tools/ToolGuard.cs`) — which `koti.Tests/ToolErrorHandlingTests.cs`'s header comment already calls a cross-server convention. Plus one fold-in deferred from C6's final review: the `ScriptBudget` singleton in tietue is a lazy factory, so the `ScriptBudgetTests` comment "a misconfigured Scripts:TimeoutSeconds must fail at startup" is not actually true — nothing resolves it at boot.

**Architecture:** toimi.core grows three small seams, all in already-referenced dependency territory. (a) `Toimi.Core.Tools.ToolGuard` — the never-throw convention promoted from selain: `RunAsync(body, translate?, logger?, errorPrefix)` runs a `Func<Task<string>>`, maps expected domain failures through an optional `Func<Exception, string?>` translator (pinned messages), and backstops everything else as `"{errorPrefix}: {ex.Message}"` with optional logging — the single home of the CA1031 suppression. (b) `Toimi.Core.Hosting` config helpers — `RequireConfig<T>(section)`, `RequireConnectionString(name)`, `RequireValue(key)` on `WebApplicationBuilder`, whose uniform messages (`"{section} configuration is required"`, `"ConnectionStrings:{name} is required"`, `"{key} is required"`) are byte-identical to every existing call site. (c) `Toimi.Core.Hosting` database helpers — `AddToimiDatabase<TContext>(connectionStringName)` (required connection string → `UseNpgsql().UseSnakeCaseNamingConvention()`) and `app.MigrateAndSeedAsync<TContext>(seed?)` (one scope, `IsRelational()` guard from tietue — fixing ruutu's and web's drift — then `MigrateAsync` then the optional seed callback inside the guard). A thin `AddToimiToolServer(builder, name, assembly)` becomes the uniform builder-level entry for the five MCP pods. koti/verkko/ruutu tools and selain's `ToolGuard` migrate onto the core guard; every pod `Program.cs` shrinks to declarative calls; tietue additionally eagerly resolves `ScriptBudget` right after `Build()`.

**Tech Stack:** .NET 10 minimal APIs, xUnit v2, EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL` + `EFCore.NamingConventions` in core, `Microsoft.EntityFrameworkCore.InMemory` in tests), ModelContextProtocol 1.4.1.

## Global Constraints

- dotnet is NOT on PATH: every dotnet command is preceded by `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"`.
- Per-project test commands: `dotnet test src/<project>.Tests/<project>.Tests.csproj --nologo -v q` from `/Users/jari/private/toimi`.
- Suite floors — no drops: tietue 396 (Docker-gated Testcontainers tests RUN, not skip — Docker is available), core 93 (expected ~109 after the ~16 new tests), web 38, koti 26, verkko 26, ruutu 105 (verified by running the suite), selain 60 (Chromium facts run).
- Before each commit: `dotnet format <csproj>` for every touched project, then `dotnet format <csproj> --verify-no-changes` exits 0. Enforced as errors: IDE0005 (unused usings — watch for usings orphaned by removed try/catches), IDE0022 (block bodies), IDE0046, whitespace. 2-space indent, file-scoped namespaces.
- Commit style: `<type>(<scope>): <subject>` — here `refactor(core)`, `refactor(koti)`, `refactor(verkko)`, `refactor(ruutu)`, `refactor(selain)`, `refactor(tietue)`, `refactor(web)`, `docs`.
- UNCHANGED surfaces: the MCP tool surface (names, descriptions, parameters); deployment/k8s; tietue's `IEntityBehavior` registration order from C4 (`SemanticIndexBehavior` → `TriggerProvisioningBehavior` → `ExpiryBehavior`, `Program.cs:43-45`) and every other tietue service registration; the assembly-scan footgun doc comment on `AddToimiMcpServer` (`ToimiHostingExtensions.cs:15-20`) must survive verbatim.
- Pinned error strings that MUST keep passing (verified locations):
  - koti (`ToolErrorHandlingTests`): `"Home Assistant request failed: {msg}"` (contains "Home Assistant" + the exception message), `"Home Assistant request timed out."` (contains "timed out"), exact `"Entity not found."`, and the no-token-leak assert.
  - verkko (`FetchUrlToolTests`): exact `"Request timed out fetching http://example.test/a"`; the `HttpRequestException` inner-message composition (`"{msg} ({inner})"` only when the inner message isn't already contained — the "inner detail appears exactly once" test).
  - selain (`ToolGuardTests`): `ToolGuard.TabLostMessage`, `ToolGuard.PageBusyMessage` (contains "busy" and "wait_for"), and the `host.Touch()` idle-accounting behavior.
  - Bootstrap messages nothing pins in tests today, but the helpers reproduce them byte-identically anyway (they appear in ops runbooks/logs): `"HomeAssistant configuration is required"`, `"Toimi configuration is required"`, `"OpenAI:ApiKey is required"`, `"ConnectionStrings:{Tietue|Ruutu|Toimi} is required"`.
- `WebApplicationFactory` hosts that boot real `Program.cs` files and must stay green: `tietue.Tests/AdminEndpointsTests.cs` (`TietueTestFactory`, in-memory DB — relies on the `IsRelational()` skip) and `selain.Tests/Integration/EndpointTests.cs`.

## Design Decisions

**Package coupling for the DB helper: zero packages added — the honest answer is that core already pays this cost.** `toimi.core.csproj` already references `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 and `EFCore.NamingConventions` 10.0.1 (lines 26 and 15 — it needs both for `ToimiDbContext` + `ToimiDbContextFactory`), plus `FrameworkReference Microsoft.AspNetCore.App` (for `Hosting/`). So `AddToimiDatabase`/`MigrateAndSeedAsync` living in core adds no dependency to any pod that doesn't already carry it transitively. The only csproj edit anywhere is adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to **toimi.core.Tests** so tests can construct a `WebApplicationBuilder` (explicit rather than relying on transitive FrameworkReference flow).

**ToolGuard shape: string-in-string-out static guard with a translate-then-backstop ladder, promoted from selain's `Tools/ToolGuard.cs`.** selain's guard is the model but is domain-entangled (kill switch, `ActionLock`, `Touch()`, no-tab check) around a portable core: catch expected exception types → pinned message. The core generalization is `ToolGuard.RunAsync(Func<Task<string>> body, Func<Exception, string?>? translate = null, ILogger? logger = null, string errorPrefix = "Error")`: `translate` returning non-null wins (pinned domain messages — koti's HA pair, verkko's URL-bearing pair, ruutu's `Error: …` family, selain's busy/lost pair); anything untranslated (or with no translator) becomes `"{errorPrefix}: {ex.Message}"` and is logged when a logger is supplied (matching ruutu's `DisplayShow`, the only current logging site). The CA1031 `#pragma` lives exactly once, in core. selain's `Tools/ToolGuard.cs` keeps its name, constants, `Disabled()`, and `WithActiveTabAsync` (the domain layer other selain files reference) but its try/catch delegates to the core guard via a new `TranslatePageFailure` translator; koti/verkko/ruutu call core directly.

**The one deliberate behavior delta: the backstop catch-all.** Today an *unexpected* exception (anything outside the specifically-caught types) escapes koti, verkko, selain, and ruutu's `TemplateTools`/`DisplayManagementTools` as a raw MCP protocol error; only ruutu's `DisplayContentTools` backstops. After migration every guarded tool backstops to a readable string — which is exactly the convention the `ToolErrorHandlingTests` header comment and the CA1031 pragma justifications document. No test pins exception propagation. Ruutu's `DisplayShow` log line changes from `"display_show failed"` to the guard's `"MCP tool call failed"`; no test pins log text. Control-flow catches that are NOT the convention stay put: koti `ListEntities`' area-lookup fallback (degrades to `areas = []` or a specific area-filter error — semantics, not error plumbing), koti `CallService`'s JSON pre-validation (returns a hint string for *invalid input*, not a failure), selain `Browse`/`TabTools`/`ScreenshotTool` per-step messages (interleaved step-specific handling; `ScreenshotTool` doesn't even return `Task<string>`), ruutu lint/tier pre-validations.

**Config helpers: three, matching the three real call-site shapes — and web's ToimiConfiguration check is deliberately NOT unified.** Surveying all sites: section-bind (koti `HomeAssistant`→singleton, tietue `Toimi`→singleton) → `RequireConfig<T>`; connection strings (ruutu, tietue, web) → `RequireConnectionString` (subsumed by `AddToimiDatabase`); single indexer value (tietue `OpenAI:ApiKey`) → `RequireValue`. Sites with `?? new Options()` defaults (verkko `Ntfy`, selain `Selain`, tietue `Ntfy`/`Scripts`/`Suoritin`, web `Toimi:Admin`) are *optional* config and keep their fallbacks — a Require helper there would be wrong. web's `Toimi` section check (`Program.cs:8-20`) writes to `Console.Error` and `return 1` — an operator-facing exit-code contract with two distinct messages (missing section vs missing ApiKey), not the throw pattern; it stays as-is. web is not an MCP tool server: it adopts only `AddToimiDatabase` + `MigrateAndSeedAsync`, never `AddToimiToolServer`.

**Database triad: one scope, `IsRelational()` guard, seed inside the guard — tietue's shape is the union.** tietue is the correct existing site (guard, then migrate + seeders + Qdrant collection warm-up in one scope). ruutu drifts twice: no guard, and seeding in a *second* scope — the unified `MigrateAndSeedAsync<TContext>(Func<IServiceProvider, Task>? seed = null)` gives it the guard (the drift fix) and runs its seeder in the same scope (equivalent: both scopes were fresh; `TemplateSeeder` and its `RuutuDbContext` are scoped services resolved identically). web gains the guard too (latent hazard today — nothing boots web's `Program` under a non-relational provider, but nothing prevents it either). Seeding sits *inside* the guard exactly as tietue has it, which is what `TietueTestFactory` (in-memory) relies on. The relational path is exercised by real pod boots (dev/server), not unit tests; the non-relational skip IS unit-tested.

**`AddToimiToolServer` is honestly thin.** Auditing the five MCP `Program.cs` files, the only *config-free* common bootstrap is the MCP registration itself — everything else either differs (HTTP clients, hosted services) or is app-stage (`MapToimiMcp`). So `AddToimiToolServer(this WebApplicationBuilder, string serverName, Assembly toolsAssembly)` wraps `services.AddToimiMcpServer(...)` and returns the builder: its value is a uniform builder-level entry point (all five pods read identically) and a single future home for shared bootstrap, not folded volume today. `AddToimiMcpServer` remains the services-level implementation; its assembly-scan footgun doc comment (`WithToolsFromAssembly()` no-arg binds to `Assembly.GetCallingAssembly()` = toimi.core = zero tools) survives verbatim, and `AddToimiToolServer`'s own doc points at it.

**ScriptBudget fail-fast (C6 fold-in): eager singleton resolution, one line.** `ScriptBudgetTests.Non_positive_script_time_fails_fast` documents "must fail at startup, not produce a zero-length watchdog at fire time", but the registration (`tietue Program.cs:73-74`) is a lazy factory — first resolution happens at HTTP-client creation or first script fire. Adding `_ = app.Services.GetRequiredService<ScriptBudget>();` right after `builder.Build()` makes the comment true. `TietueTestFactory` boots `Program` with default `ScriptOptions` (no `Scripts:` settings) → `From` succeeds → all factory tests unaffected.

**Per-pod delta summary:**

| Pod | Program.cs | Tools |
|---|---|---|
| koti | `RequireConfig<HomeAssistantOptions>("HomeAssistant")`; `AddToimiToolServer` | 4 tools → core `ToolGuard` + shared `HomeAssistantErrors.Translate` |
| verkko | `AddToimiToolServer` | `FetchUrlTool`, `SendNotificationTool` → core `ToolGuard` |
| ruutu | `AddToimiDatabase<RuutuDbContext>("Ruutu")`; `MigrateAndSeedAsync` (gains `IsRelational` guard — drift fix); `AddToimiToolServer` | `DisplayContentTools` (pragmas deleted), `TemplateTools`, `DisplayManagementTools` → core `ToolGuard`; `DisplayEventsTools` untouched (no try/catch today) |
| selain | `AddToimiToolServer` | local `ToolGuard` keeps domain layer, delegates try/catch to core via `TranslatePageFailure` |
| tietue | `AddToimiDatabase<TietueDbContext>("Tietue")`; `RequireConfig<ToimiConfiguration>("Toimi")`; `RequireValue("OpenAI:ApiKey")`; `AddToimiToolServer`; eager `ScriptBudget`; `MigrateAndSeedAsync` with seeders+Qdrant callback | untouched (`ToolHelpers` is formatting-only; tietue tools return validation strings, no try/catch copies) |
| web | `AddToimiDatabase<ToimiDbContext>("Toimi")`; `MigrateAndSeedAsync` (gains guard); ToimiConfiguration exit-code check stays; NO `AddToimiToolServer` | n/a |

---

## Task 1: Core `ToolGuard` (TDD)

**Files**
- Create: `src/toimi.core/Tools/ToolGuard.cs`
- Create: `src/toimi.core.Tests/ToolGuardTests.cs`

**Interfaces**
- `Toimi.Core.Tools.ToolGuard.RunAsync(Func<Task<string>> body, Func<Exception, string?>? translate = null, ILogger? logger = null, string errorPrefix = "Error") : Task<string>`

**Steps**

- [ ] Write `src/toimi.core.Tests/ToolGuardTests.cs` (RED — the type doesn't exist yet):

```csharp
using Microsoft.Extensions.Logging;
using Toimi.Core.Tools;
using Xunit;

namespace Toimi.Core.Tests;

public class ToolGuardTests
{
  private sealed class CapturingLogger : ILogger
  {
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
      return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
      return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      Entries.Add((logLevel, exception));
    }
  }

  [Fact]
  public async Task Success_passes_the_body_result_through()
  {
    var result = await ToolGuard.RunAsync(() => Task.FromResult("ok"));

    Assert.Equal("ok", result);
  }

  [Fact]
  public async Task Translated_exception_returns_the_pinned_message()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new HttpRequestException("boom"),
      translate: ex => ex is HttpRequestException http ? $"Request failed: {http.Message}" : null);

    Assert.Equal("Request failed: boom", result);
  }

  [Fact]
  public async Task Translator_declining_falls_through_to_the_backstop()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("nope"),
      translate: ex => ex is HttpRequestException ? "unreachable" : null);

    Assert.Equal("Error: nope", result);
  }

  [Fact]
  public async Task Backstop_without_a_translator_uses_the_default_Error_prefix()
  {
    var result = await ToolGuard.RunAsync(() => throw new InvalidOperationException("nope"));

    Assert.Equal("Error: nope", result);
  }

  [Fact]
  public async Task Backstop_uses_a_custom_prefix()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("smtp down"),
      errorPrefix: "Failed to send notification");

    Assert.Equal("Failed to send notification: smtp down", result);
  }

  [Fact]
  public async Task Backstop_logs_the_untranslated_exception()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(() => throw new InvalidOperationException("nope"), logger: logger);

    var entry = Assert.Single(logger.Entries);
    Assert.Equal(LogLevel.Error, entry.Level);
    Assert.IsType<InvalidOperationException>(entry.Exception);
  }

  [Fact]
  public async Task Translated_exceptions_are_not_logged()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(
      () => throw new TimeoutException(),
      translate: ex => ex is TimeoutException ? "The page is busy." : null,
      logger: logger);

    Assert.Empty(logger.Entries);
  }

  [Fact]
  public async Task Cancellation_is_stringified_like_any_other_failure()
  {
    // Matches ruutu's existing catch-all behavior: tools take a CancellationToken
    // and a cancelled call comes back as a string, never as a thrown OCE.
    var result = await ToolGuard.RunAsync(() => throw new OperationCanceledException());

    Assert.StartsWith("Error: ", result);
  }
}
```

- [ ] Run the core suite, confirm the new file fails to compile (RED): `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q`
- [ ] Write `src/toimi.core/Tools/ToolGuard.cs` (GREEN):

```csharp
using Microsoft.Extensions.Logging;

namespace Toimi.Core.Tools;

/// <summary>
/// The cross-server MCP tool convention: a tool never throws. Failures come
/// back as readable strings the LLM can act on (retry, fix an argument, pick
/// another tool) instead of an opaque MCP protocol error. Wrap the tool body
/// in <see cref="RunAsync"/>; pass <c>translate</c> for the domain-specific
/// pinned messages and let the backstop stringify the rest.
/// </summary>
public static class ToolGuard
{
  /// <summary>
  /// Runs <paramref name="body"/> under the never-throw contract.
  /// <paramref name="translate"/> maps expected domain failures to their
  /// pinned messages (return null to decline); everything untranslated becomes
  /// "{errorPrefix}: {message}" and is logged when a logger is given.
  /// </summary>
  public static async Task<string> RunAsync(
    Func<Task<string>> body,
    Func<Exception, string?>? translate = null,
    ILogger? logger = null,
    string errorPrefix = "Error")
  {
    try
    {
      return await body();
    }
#pragma warning disable CA1031 // The backstop IS the convention: MCP tools return readable error strings, never propagate exceptions.
    catch (Exception ex)
#pragma warning restore CA1031
    {
      if (translate?.Invoke(ex) is { } translated)
      {
        return translated;
      }

      logger?.LogError(ex, "MCP tool call failed");
      return $"{errorPrefix}: {ex.Message}";
    }
  }
}
```

- [ ] Core suite green (93 + 8 = 101): `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q`
- [ ] Format: `dotnet format src/toimi.core/toimi.core.csproj && dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj`, then both with `--verify-no-changes` (exit 0).
- [ ] Commit: `refactor(core): ToolGuard — the MCP never-throw convention as a core seam`

## Task 2: Core config + database + tool-server bootstrap helpers (TDD)

**Files**
- Create: `src/toimi.core/Hosting/ToimiConfigurationExtensions.cs`
- Create: `src/toimi.core/Hosting/ToimiDatabaseExtensions.cs`
- Edit: `src/toimi.core/Hosting/ToimiHostingExtensions.cs` (add `AddToimiToolServer`; existing methods and the footgun comment untouched)
- Create: `src/toimi.core.Tests/HostingBootstrapTests.cs`
- Edit: `src/toimi.core.Tests/toimi.core.Tests.csproj` (add the AspNetCore FrameworkReference)

**Interfaces**
- `T RequireConfig<T>(this WebApplicationBuilder builder, string section)`
- `string RequireConnectionString(this WebApplicationBuilder builder, string name)`
- `string RequireValue(this WebApplicationBuilder builder, string key)`
- `WebApplicationBuilder AddToimiDatabase<TContext>(this WebApplicationBuilder builder, string connectionStringName) where TContext : DbContext`
- `Task MigrateAndSeedAsync<TContext>(this WebApplication app, Func<IServiceProvider, Task>? seed = null) where TContext : DbContext`
- `WebApplicationBuilder AddToimiToolServer(this WebApplicationBuilder builder, string serverName, Assembly toolsAssembly)`

**Steps**

- [ ] Add to `src/toimi.core.Tests/toimi.core.Tests.csproj`, as a new ItemGroup after the PropertyGroup:

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

- [ ] Write `src/toimi.core.Tests/HostingBootstrapTests.cs` (RED):

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Toimi.Core.Data;
using Toimi.Core.Hosting;
using Xunit;

namespace Toimi.Core.Tests;

public class HostingBootstrapTests
{
  private sealed class FakeOptions
  {
    public string? BaseUrl { get; set; }
  }

  private static WebApplicationBuilder Builder(params (string Key, string Value)[] settings)
  {
    var builder = WebApplication.CreateBuilder();
    builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
    return builder;
  }

  [Fact]
  public void RequireConfig_binds_a_present_section()
  {
    var options = Builder(("Ha:BaseUrl", "http://ha.test")).RequireConfig<FakeOptions>("Ha");

    Assert.Equal("http://ha.test", options.BaseUrl);
  }

  [Fact]
  public void RequireConfig_missing_section_throws_the_uniform_message()
  {
    // Byte-identical to koti's original hand-rolled message.
    var ex = Assert.Throws<InvalidOperationException>(
      () => Builder().RequireConfig<FakeOptions>("HomeAssistant"));

    Assert.Equal("HomeAssistant configuration is required", ex.Message);
  }

  [Fact]
  public void RequireConnectionString_returns_a_present_string()
  {
    var cs = Builder(("ConnectionStrings:Ruutu", "Host=x;Database=ruutu")).RequireConnectionString("Ruutu");

    Assert.Equal("Host=x;Database=ruutu", cs);
  }

  [Fact]
  public void RequireConnectionString_missing_throws_the_uniform_message()
  {
    var ex = Assert.Throws<InvalidOperationException>(() => Builder().RequireConnectionString("Ruutu"));

    Assert.Equal("ConnectionStrings:Ruutu is required", ex.Message);
  }

  [Fact]
  public void RequireValue_returns_a_present_value()
  {
    Assert.Equal("sk-test", Builder(("OpenAI:ApiKey", "sk-test")).RequireValue("OpenAI:ApiKey"));
  }

  [Fact]
  public void RequireValue_missing_throws_the_uniform_message()
  {
    var ex = Assert.Throws<InvalidOperationException>(() => Builder().RequireValue("OpenAI:ApiKey"));

    Assert.Equal("OpenAI:ApiKey is required", ex.Message);
  }

  [Fact]
  public async Task AddToimiDatabase_registers_npgsql_with_snake_case_naming()
  {
    var builder = Builder(("ConnectionStrings:Toimi", "Host=localhost;Database=x"));
    builder.AddToimiDatabase<ToimiDbContext>("Toimi");
    await using var app = builder.Build();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ToimiDbContext>();
    Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    var conversation = db.Model.FindEntityType(typeof(Conversation))!;
    Assert.Equal("created_at", conversation.FindProperty(nameof(Conversation.CreatedAt))!.GetColumnName());
  }

  [Fact]
  public void AddToimiDatabase_missing_connection_string_fails_at_boot()
  {
    var ex = Assert.Throws<InvalidOperationException>(
      () => Builder().AddToimiDatabase<ToimiDbContext>("Toimi"));

    Assert.Equal("ConnectionStrings:Toimi is required", ex.Message);
  }

  [Fact]
  public async Task MigrateAndSeedAsync_skips_migrate_and_seed_when_not_relational()
  {
    // The guard tietue had and ruutu/web lacked: test hosts swap in the EF
    // in-memory provider, where MigrateAsync throws and seeding is unwanted.
    var builder = WebApplication.CreateBuilder();
    builder.Services.AddDbContext<ToimiDbContext>(o => o.UseInMemoryDatabase($"bootstrap-{Guid.NewGuid()}"));
    await using var app = builder.Build();

    var seeded = false;
    await app.MigrateAndSeedAsync<ToimiDbContext>(_ =>
    {
      seeded = true;
      return Task.CompletedTask;
    });

    Assert.False(seeded);
  }

  [Fact]
  public async Task AddToimiToolServer_names_the_mcp_server()
  {
    var builder = WebApplication.CreateBuilder();
    builder.AddToimiToolServer("test-server", typeof(HostingBootstrapTests).Assembly);
    await using var app = builder.Build();

    var options = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
    Assert.Equal("test-server", options.ServerInfo!.Name);
  }
}
```

- [ ] Run the core suite, confirm RED (missing extension methods).
- [ ] Write `src/toimi.core/Hosting/ToimiConfigurationExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Toimi.Core.Hosting;

/// <summary>
/// Required-configuration binding with the uniform fail-at-boot error message,
/// replacing the per-pod "Get&lt;T&gt;() ?? throw" copies. Optional config with
/// a fallback (e.g. "?? new NtfyOptions()") deliberately has no helper here —
/// absence is not an error at those sites.
/// </summary>
public static class ToimiConfigurationExtensions
{
  /// <summary>Binds a required section; throws "{section} configuration is required" when absent.</summary>
  public static T RequireConfig<T>(this WebApplicationBuilder builder, string section)
  {
    return builder.Configuration.GetSection(section).Get<T>()
      ?? throw new InvalidOperationException($"{section} configuration is required");
  }

  /// <summary>Required connection string; throws "ConnectionStrings:{name} is required" when absent.</summary>
  public static string RequireConnectionString(this WebApplicationBuilder builder, string name)
  {
    return builder.Configuration.GetConnectionString(name)
      ?? throw new InvalidOperationException($"ConnectionStrings:{name} is required");
  }

  /// <summary>Required single value; throws "{key} is required" when absent.</summary>
  public static string RequireValue(this WebApplicationBuilder builder, string key)
  {
    return builder.Configuration[key]
      ?? throw new InvalidOperationException($"{key} is required");
  }
}
```

- [ ] Write `src/toimi.core/Hosting/ToimiDatabaseExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Toimi.Core.Hosting;

/// <summary>
/// The pod database triad in one place: Npgsql + snake_case registration from
/// a required connection string, and the migrate-then-seed boot scope guarded
/// by IsRelational() (test hosts swap in the EF in-memory provider, where
/// MigrateAsync throws and seeding is unwanted).
/// </summary>
public static class ToimiDatabaseExtensions
{
  public static WebApplicationBuilder AddToimiDatabase<TContext>(this WebApplicationBuilder builder, string connectionStringName)
    where TContext : DbContext
  {
    var connectionString = builder.RequireConnectionString(connectionStringName);
    builder.Services.AddDbContext<TContext>(options =>
      options.UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention());
    return builder;
  }

  /// <summary>
  /// Boot-time migration plus optional seeding, both inside the relational
  /// guard and sharing one scope. Call after Build(), before Run().
  /// </summary>
  public static async Task MigrateAndSeedAsync<TContext>(this WebApplication app, Func<IServiceProvider, Task>? seed = null)
    where TContext : DbContext
  {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TContext>();
    if (!db.Database.IsRelational())
    {
      return;
    }

    await db.Database.MigrateAsync();
    if (seed is not null)
    {
      await seed(scope.ServiceProvider);
    }
  }
}
```

- [ ] Add to `src/toimi.core/Hosting/ToimiHostingExtensions.cs`, after `AddToimiMcpServer` (do NOT touch the existing methods or the assembly-scan footgun comment at lines 15-20):

```csharp
  /// <summary>
  /// Builder-level entry point for an MCP tool-server pod. Thin today — the
  /// MCP registration is the only config-free bootstrap all five pods share —
  /// but it is the single home future shared bootstrap goes, and the pod
  /// Program.cs files read uniformly. The tool assembly MUST be passed
  /// explicitly (typeof(Program).Assembly) — see <see cref="AddToimiMcpServer"/>
  /// for the assembly-scan footgun.
  /// </summary>
  public static WebApplicationBuilder AddToimiToolServer(this WebApplicationBuilder builder, string serverName, Assembly toolsAssembly)
  {
    builder.Services.AddToimiMcpServer(serverName, toolsAssembly);
    return builder;
  }
```

- [ ] Core suite green (101 + 10 = 111 expected; floor 93): `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q`
- [ ] Format apply + verify for `toimi.core` and `toimi.core.Tests`.
- [ ] Commit: `refactor(core): required-config, database, and tool-server bootstrap helpers`

## Task 3: koti + verkko migrate onto the core seams

**Files**
- Edit: `src/toimi.tools.koti/Program.cs`
- Create: `src/toimi.tools.koti/Tools/HomeAssistantErrors.cs`
- Edit: `src/toimi.tools.koti/Tools/GetEntityStateTool.cs`, `GetHistoryTool.cs`, `CallServiceTool.cs`, `ListEntitiesTool.cs`
- Edit: `src/toimi.tools.verkko/Program.cs`
- Edit: `src/toimi.tools.verkko/Tools/FetchUrlTool.cs`, `SendNotificationTool.cs`

**Interfaces**
- `internal static class HomeAssistantErrors { public static string? Translate(Exception ex); }` — the four koti tools' shared translator.

**Steps**

- [ ] koti `Program.cs`: replace lines 6-7 with the helper and switch the MCP registration to the builder-level entry. Full resulting file:

```csharp
using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Hosting;

var builder = WebApplication.CreateBuilder(args);

var haOptions = builder.RequireConfig<HomeAssistantOptions>("HomeAssistant");

builder.Services.AddSingleton(haOptions);
builder.Services.AddHttpClient<HomeAssistantClient>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton(sp =>
{
  var factory = sp.GetRequiredService<IHttpClientFactory>();
  var http = factory.CreateClient(nameof(HomeAssistantClient));
  return new HomeAssistantClient(http, haOptions);
});

builder.AddToimiToolServer("koti", typeof(Program).Assembly);

var app = builder.Build();

app.MapToimiMcp();

app.Run();
```

- [ ] Create `src/toimi.tools.koti/Tools/HomeAssistantErrors.cs`:

```csharp
namespace toimi.tools.koti.Tools;

/// <summary>
/// The pinned Home Assistant failure translations shared by all four tools
/// (koti.Tests/ToolErrorHandlingTests pins both messages). Anything else falls
/// through to ToolGuard's backstop.
/// </summary>
internal static class HomeAssistantErrors
{
  public static string? Translate(Exception ex)
  {
    return ex switch
    {
      HttpRequestException http => $"Home Assistant request failed: {http.Message}",
      TaskCanceledException => "Home Assistant request timed out.",
      _ => null,
    };
  }
}
```

- [ ] `GetEntityStateTool.cs`: add `using Toimi.Core.Tools;`, replace the method body (attributes/descriptions unchanged; note the signature drops `async` — the guard returns the task):

```csharp
  public Task<string> GetEntityState(
    [Description("Entity ID (e.g. 'light.living_room', 'sensor.temperature', 'switch.tv')")] string entityId)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var state = await ha.GetStateAsync(entityId);
      return state is null ? "Entity not found." : state.Value.ToString();
    }, translate: HomeAssistantErrors.Translate);
  }
```

- [ ] `GetHistoryTool.cs`: keep the `hours` validation, guard the rest (method stays `async` because of the early return):

```csharp
    if (hours is < 1 or > 168)
    {
      return "Hours must be between 1 and 168.";
    }

    return await ToolGuard.RunAsync(async () =>
    {
      var result = await ha.GetHistoryAsync(entityId, hours);
      var json = result.GetRawText();
      const int maxChars = 50_000;
      return json.Length <= maxChars
        ? json
        : json[..maxChars] + "\n[truncated — request fewer hours]";
    }, translate: HomeAssistantErrors.Translate);
```

- [ ] `CallServiceTool.cs`: the JSON pre-validation block (lines 18-34, "Invalid JSON in data parameter…") stays verbatim — it is input validation, not failure plumbing. The trailing try/catch becomes:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      _ = await ha.CallServiceAsync(domain, service, entityId, parsedData);
      return "Service called successfully.";
    }, translate: HomeAssistantErrors.Translate);
```

- [ ] `ListEntitiesTool.cs`: wrap everything after the `limit` clamp in `ToolGuard.RunAsync(..., translate: HomeAssistantErrors.Translate)`. Inside the body: the `GetStatesAsync` try/catch (lines 18-30) collapses into the guard (plain `var states = await ha.GetStatesAsync();`); the "Unexpected response…" check stays; the `GetEntityAreasAsync` try/catch **stays verbatim** — it is fallback control flow (area filter → the "Area lookup failed…" message; otherwise `areas = []`), not the convention.
- [ ] koti suite green (26): `dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj --nologo -v q` — `ToolErrorHandlingTests` all pass unchanged (they construct tools directly; messages are byte-identical).
- [ ] Format apply + verify for `toimi.tools.koti`; commit: `refactor(koti): adopt core ToolGuard and required-config bootstrap`
- [ ] verkko `Program.cs`: replace `builder.Services.AddToimiMcpServer("verkko", typeof(Program).Assembly);` with `builder.AddToimiToolServer("verkko", typeof(Program).Assembly);` (the `Ntfy` section keeps its `?? new NtfyOptions()` fallback — optional config, no Require helper).
- [ ] `FetchUrlTool.cs`: add `using Toimi.Core.Tools;`; URL validation and blocked-host checks and the cache-hit path stay verbatim; the fetch block becomes:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      var result = await fetcher.FetchAsync(url, CancellationToken.None);
      cache.Set(url, result);
      return FormatResult(result, fromCache: false);
    }, translate: ex => ex switch
    {
      HttpRequestException http => $"HTTP error fetching {url}: {Reason(http)}",
      TaskCanceledException => $"Request timed out fetching {url}",
      _ => null,
    });
```

  with the inner-message composition preserved as a private helper (pinned by the "inner detail exactly once" test):

```csharp
  private static string Reason(HttpRequestException ex)
  {
    return ex.InnerException is { Message.Length: > 0 } inner && !ex.Message.Contains(inner.Message)
      ? $"{ex.Message} ({inner.Message})"
      : ex.Message;
  }
```

- [ ] `SendNotificationTool.cs`: priority validation stays; the try/catch becomes:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      await ntfy.SendAsync(message, title, priority, tags);
      return "Notification sent.";
    }, errorPrefix: "Failed to send notification");
```

  (The old catch-all produced `$"Failed to send notification: {ex.Message}"` — the prefix reproduces it byte-identically.)
- [ ] verkko suite green (26): `dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --nologo -v q` — the exact-equal timeout assertion in `FetchUrlToolTests` passes.
- [ ] Format apply + verify for `toimi.tools.verkko`; commit: `refactor(verkko): adopt core ToolGuard and tool-server bootstrap`

## Task 4: ruutu + selain migrate onto the core seams (ruutu's migrate-guard drift fixed)

**Files**
- Edit: `src/toimi.tools.ruutu/Program.cs`
- Edit: `src/toimi.tools.ruutu/Tools/DisplayContentTools.cs` (all three `#pragma CA1031` pairs deleted)
- Edit: `src/toimi.tools.ruutu/Tools/TemplateTools.cs`, `src/toimi.tools.ruutu/Tools/DisplayManagementTools.cs`
- Edit: `src/toimi.tools.selain/Tools/ToolGuard.cs`
- Edit: `src/toimi.tools.selain/Program.cs`

**Interfaces**
- selain `ToolGuard` gains `public static string? TranslatePageFailure(Exception ex)`; `WithActiveTabAsync` signature unchanged.

**Steps**

- [ ] ruutu `Program.cs` — full resulting file (DB triad → helpers; the two boot scopes collapse into one guarded call; registration order otherwise unchanged):

```csharp
using Toimi.Core.Hosting;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Transport;

var builder = WebApplication.CreateBuilder(args);

builder.AddToimiDatabase<RuutuDbContext>("Ruutu");

builder.Services.AddScoped<DisplayRepository>();
builder.Services.AddScoped<TemplateRepository>();
builder.Services.AddScoped<DisplayEventRepository>();
builder.Services.AddScoped<TemplateSeeder>();
builder.Services.AddScoped<toimi.tools.ruutu.Rendering.DbTemplateSource>();

builder.Services.AddSingleton<SseHub>();
builder.Services.AddScoped<ContentPushService>();

builder.Services.AddControllers();

builder.AddToimiToolServer("ruutu", typeof(Program).Assembly);

var app = builder.Build();

app.UseStaticFiles(new StaticFileOptions
{
  RequestPath = "/ruutu/static"
});

app.MapControllers();

await app.MigrateAndSeedAsync<RuutuDbContext>(sp => sp.GetRequiredService<TemplateSeeder>().SeedAsync());

app.MapToimiMcp();
app.MapToimiReadiness<RuutuDbContext>();

app.Run();
```

  (`using Microsoft.EntityFrameworkCore;` is dropped — nothing in the file needs it anymore; IDE0005 would flag it. ruutu thereby GAINS the `IsRelational()` guard it was missing — the drift fix — and its seeder moves into the same scope as the migration, which is behavior-equivalent since both are scoped services that were resolved from fresh scopes anyway.)
- [ ] `DisplayContentTools.cs` — full rewrite (descriptions/params verbatim from the current file; all `#pragma warning disable CA1031` pairs are gone):

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Toimi.Core.Tools;
using toimi.tools.ruutu.Rendering;
using toimi.tools.ruutu.Transport;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayContentTools(ContentPushService pusher, ILogger<DisplayContentTools> logger)
{
  [McpServerTool, Description("Render a template with the given data and push it as the display's current scene. Replaces whatever was being shown. Use list_templates first to see what's available; create_template if you need a new shape.")]
  public Task<string> DisplayShow(
    [Description("The display identifier.")] string identifier,
    [Description("Template name from display_list_templates.")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowSceneAsync(identifier, template, data, ct);
      return "ok";
    }, translate: ex => TranslateContentFailure(ex, template), logger: logger);
  }

  [McpServerTool, Description("Push a template as a temporary overlay on top of the current scene. Stays until the user taps it (no auto-clear). Newest overlay appears on top; tapping dismisses and reveals the next. Most commonly used with the 'notification' template.")]
  public Task<string> DisplayOverlay(
    [Description("The display identifier.")] string identifier,
    [Description("Template name (any template works as an overlay).")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowOverlayAsync(identifier, template, data, ct);
      return "ok";
    }, translate: ex => TranslateContentFailure(ex, template));
  }

  [McpServerTool, Description("Reset the display: clear all overlays and return to the configured idle scene (or the Toimi splash if no idle is configured).")]
  public Task<string> DisplayClear(
    [Description("The display identifier.")] string identifier,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      await pusher.ClearAsync(identifier, ct);
      return "ok";
    });
  }

  private static string? TranslateContentFailure(Exception ex, string template)
  {
    return ex switch
    {
      JsonException json => $"Error: dataJson is not valid JSON: {json.Message}",
      RenderException render => $"Error rendering '{template}': {render.Message}",
      InvalidOperationException op => $"Error: {op.Message}",
      _ => null,
    };
  }
}
```

- [ ] `TemplateTools.cs`: add `using Toimi.Core.Tools;`. Lint/tier/not-found pre-checks stay verbatim; each remaining try/catch becomes a guard call with the same messages:
  - `DisplayCreateTemplate` — the schema-parse try/catch and Upsert try/catch collapse into one guarded body:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      JsonDocument.Parse(schemaJson);
      await templates.UpsertAiAsync(name, description, schemaJson, modernHtml, legacyHtml, ct);
      return "ok";
    }, translate: ex => ex switch
    {
      JsonException json => $"Error: schemaJson is not valid JSON: {json.Message}",
      InvalidOperationException op => $"Error: {op.Message}",
      _ => null,
    });
```

  - `DisplayUpdateTemplate` — the Upsert try/catch becomes:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      await templates.UpsertAiAsync(
        name,
        description ?? existing.Description,
        schemaJson ?? existing.SchemaJson,
        modernHtml ?? existing.ModernHtml,
        legacyHtml ?? existing.LegacyHtml,
        ct);
      return "ok";
    }, translate: ex => ex is InvalidOperationException op ? $"Error: {op.Message}" : null);
```

  - `DisplayDeleteTemplate`:

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      var ok = await templates.DeleteAsync(name, ct);
      return ok ? "ok" : $"Template '{name}' not found.";
    }, translate: ex => ex is InvalidOperationException op ? $"Error: {op.Message}" : null);
```

  - `DisplayPreview` (tier validation stays first):

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      return await ScribanRenderer.RenderAsync(template, data, tier, source, ct);
    }, translate: ex => ex switch
    {
      JsonException json => $"Error: dataJson is not valid JSON: {json.Message}",
      RenderException render => $"Error: {render.Message}",
      _ => null,
    });
```

  - `DisplayListTemplates`, `DisplayGetTemplate`, `DisplayGetTierBrief` have no try/catch today — untouched (adding guards there is C8-cull territory, not this finding).
- [ ] `DisplayManagementTools.cs`: add `using Toimi.Core.Tools;`.
  - `DisplayRegister` (tier-override validation stays first):

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      var d = await displays.RegisterAsync(identifier, capabilityTierOverride, ct);
      return JsonSerializer.Serialize(new { d.Identifier, d.Tier, d.TierOverride, url = $"/ruutu/{d.Identifier}" });
    }, translate: ex => ex is ArgumentException arg ? $"Error: {arg.Message}" : null);
```

  - `DisplaySetIdle` — the whole body after the signature becomes one guarded block (the inline parse try/catch collapses; the message is identical):

```csharp
    return await ToolGuard.RunAsync(async () =>
    {
      string? storedData = null;
      if (template is not null)
      {
        var json = dataJson ?? "{}";
        JsonDocument.Parse(json);
        storedData = json;
      }

      var ok = await displays.SetIdleAsync(identifier, template, storedData, ct);
      return ok ? "ok" : $"Display '{identifier}' not found.";
    }, translate: ex => ex is JsonException json ? $"Error: dataJson is not valid JSON: {json.Message}" : null);
```

  - `DisplayUnregister`, `DisplayList`, `DisplaySetTier` have no try/catch — untouched. `DisplayEventsTools.cs` — untouched (no try/catch).
- [ ] ruutu suite green (105): `dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo -v q`
- [ ] Format apply + verify for `toimi.tools.ruutu`; commit: `refactor(ruutu): adopt core ToolGuard and database bootstrap — IsRelational drift fixed`
- [ ] selain `Tools/ToolGuard.cs` — full rewrite: the domain layer (constants, `Disabled`, lock/touch/no-tab choreography) stays; the try/catch delegates to core; the translation becomes a named, reusable member:

```csharp
using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using CoreToolGuard = Toimi.Core.Tools.ToolGuard;

namespace toimi.tools.selain.Tools;

internal static class ToolGuard
{
  public const string TabLostMessage = "The tab is no longer available (closed or browser restarted) — use browse to start again.";
  public const string PageBusyMessage = "The page is busy and did not respond in time — try again, or use wait_for.";

  /// <summary>Non-null message when the global kill switch is off.</summary>
  public static string? Disabled(SelainOptions options)
  {
    return options.Enabled ? null : "Browser tools are disabled (Selain:Enabled=false).";
  }

  /// <summary>
  /// The friendly-error translations for page-level failures: a bare timeout
  /// is a busy page, not a lost tab, so it gets its own message. Anything
  /// else falls through to the core guard's backstop.
  /// </summary>
  public static string? TranslatePageFailure(Exception ex)
  {
    return ex switch
    {
      TimeoutException => PageBusyMessage,
      PlaywrightException => TabLostMessage,
      _ => null,
    };
  }

  /// <summary>
  /// Shared guard for tools that operate on the active tab: kill switch,
  /// idle-clock touch, ActionLock, no-tab check, and the friendly-error
  /// contract via the core ToolGuard — page-level failures (tab crashed or
  /// closed mid-call, Playwright timeouts) come back as tool text, never as
  /// a raw exception out of the MCP tool. SemaphoreSlim is not reentrant, so
  /// never nest this inside a lock-holding path.
  /// </summary>
  public static async Task<string> WithActiveTabAsync(SelainOptions options, TabManager tabs, BrowserHost host, Func<TabManager.TabEntry, Task<string>> body)
  {
    if (Disabled(options) is { } off)
    {
      return off;
    }

    host.Touch();
    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      return await CoreToolGuard.RunAsync(() => body(active), translate: TranslatePageFailure);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }
}
```

  (`BrowseTools.Browse`, `TabTools`, `ScreenshotTool`, and `PageResults` keep their local step-specific handling — they already share the constants, `Browse`'s messages are per-step (`"Browser failed to start: …"`, `"Navigation to {url} timed out after 20s."`), and `ScreenshotTool` returns content blocks, not `Task<string>` — the guard's shape doesn't fit them.)
- [ ] selain `Program.cs`: replace `builder.Services.AddToimiMcpServer("selain", typeof(Program).Assembly);` with `builder.AddToimiToolServer("selain", typeof(Program).Assembly);`.
- [ ] selain suite green (60, `ToolGuardTests` + `Integration/EndpointTests` included): `dotnet test src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --nologo -v q`
- [ ] Format apply + verify for `toimi.tools.selain`; commit: `refactor(selain): ToolGuard delegates its friendly-error contract to the core guard`

## Task 5: tietue + web bootstrap migration, ScriptBudget fail-fast

**Files**
- Edit: `src/toimi.tools.tietue/Program.cs`
- Edit: `src/toimi.web/Program.cs`

**Steps**

- [ ] tietue `Program.cs` — five surgical hunks; every service registration between them (including the `IEntityBehavior` order at lines 43-45 and everything in the Scripts block) stays byte-identical:
  - Hunk 1 — lines 14-19 (connection string + AddDbContext) become:

```csharp
builder.AddToimiDatabase<TietueDbContext>("Tietue");
```

  - Hunk 2 — lines 32-33 become:

```csharp
var openAiApiKey = builder.RequireValue("OpenAI:ApiKey");
```

  - Hunk 3 — lines 64-66 (the Toimi section singleton) become:

```csharp
builder.Services.AddSingleton(builder.RequireConfig<Toimi.Core.Configuration.ToimiConfiguration>("Toimi"));
```

  - Hunk 4 — line 91 becomes:

```csharp
builder.AddToimiToolServer("tietue", typeof(Program).Assembly);
```

  - Hunk 5 — the boot block (lines 93-110) becomes:

```csharp
var app = builder.Build();

// A misconfigured Scripts:TimeoutSeconds must fail here at boot, not at the
// first trigger fire: the singleton is a lazy factory, so resolve it once now
// (ScriptBudgetTests documents the fail-fast contract).
_ = app.Services.GetRequiredService<toimi.tools.tietue.Scripts.ScriptBudget>();

await app.MigrateAndSeedAsync<TietueDbContext>(async sp =>
{
  await sp.GetRequiredService<toimi.tools.tietue.Seed.TypeSeeder>().SeedAsync();
  await sp.GetRequiredService<toimi.tools.tietue.Seed.SkillSeeder>().SeedAsync();

  var index = sp.GetRequiredService<ISemanticIndex>();
  foreach (var name in new[] { "memory", "skill" })
  {
    await index.EnsureCollectionAsync(name);
  }
});
```

  (Semantics identical to the old block: seeders and Qdrant warm-up run only when relational, in one scope. Check `using Microsoft.EntityFrameworkCore;` at line 1 — nothing else in the file uses it once the AddDbContext lambda is gone; delete it if IDE0005 flags it.)
- [ ] tietue suite green (396, Docker-gated tests RUN): `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q` — `AdminEndpointsTests`/`TietueTestFactory` boots the new Program: in-memory → `MigrateAndSeedAsync` skips (as the old guard did); default `ScriptOptions` → the eager `ScriptBudget` resolve succeeds.
- [ ] Format apply + verify for `toimi.tools.tietue`; commit: `refactor(tietue): declarative bootstrap + boot-time ScriptBudget fail-fast`
- [ ] web `Program.cs` — two hunks (web is NOT an MCP tool server: no `AddToimiToolServer`; the ToimiConfiguration Console.Error + `return 1` block at lines 8-20 stays verbatim — it is a deliberate operator-facing exit-code contract):
  - Add `using Toimi.Core.Hosting;` to the usings.
  - Hunk 1 — lines 40-45 (connection string + AddDbContext) become:

```csharp
builder.AddToimiDatabase<ToimiDbContext>("Toimi");
```

  - Hunk 2 — the migrate scope (lines 65-69) becomes:

```csharp
await app.MigrateAndSeedAsync<ToimiDbContext>();
```

  (web thereby gains the `IsRelational()` guard it was missing. Check line 1's `using Microsoft.EntityFrameworkCore;` for IDE0005 once the lambda is gone.)
- [ ] web suite green (38): `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q`
- [ ] Format apply + verify for `toimi.web`; commit: `refactor(web): adopt core database bootstrap — IsRelational guard gained`

## Task 6: Full gate + CLAUDE.md

**Files**
- Edit: `CLAUDE.md`

**Steps**

- [ ] Full suite sweep from repo root (Docker running so Testcontainers tests execute):

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
for p in toimi.core toimi.web toimi.tools.koti toimi.tools.verkko toimi.tools.ruutu toimi.tools.selain toimi.tools.tietue; do
  dotnet test "src/$p.Tests/$p.Tests.csproj" --nologo -v q || exit 1
done
```

  Floors: core ≥ 111 (93 + 18 new), web 38, koti 26, verkko 26, ruutu 105, selain 60, tietue 396. Zero failures, zero unexpected skips (tietue's Docker facts must show as passed, not skipped).
- [ ] `dotnet format <csproj> --verify-no-changes` exits 0 for every project touched in this plan (core, core.Tests, koti, verkko, ruutu, selain, tietue, web).
- [ ] `grep -rn "pragma warning disable CA1031" src/` — the only remaining hits are `src/toimi.core/Tools/ToolGuard.cs`, `src/toimi.core/ResilientMcpTool.cs`, and ruutu's `Transport/` (controllers, out of scope); none left in any `Tools/` directory outside core.
- [ ] CLAUDE.md — update the **toimi.core** section's "Owns:" bullet to:

```markdown
- Owns: LLM client factory (with `ToolCallNotifier`), MCP tool
  aggregation (`McpToolAggregator`), conversation persistence
  (`ToimiDbContext`), context-window management (`ContextManager`),
  system-prompt assembly + catalog injection (`ToimiClientFactory`),
  shared tool-server bootstrap (`Hosting/`: `AddToimiToolServer`,
  `RequireConfig`/`RequireConnectionString`/`RequireValue`,
  `AddToimiDatabase` + `MigrateAndSeedAsync` with the `IsRelational`
  boot guard), and the never-throw MCP tool guard
  (`Toimi.Core.Tools.ToolGuard`).
```

- [ ] CLAUDE.md — add one bullet to **Key Patterns** (after "Thin web transport"):

```markdown
- **Never-throw MCP tools** — tool bodies run under
  `Toimi.Core.Tools.ToolGuard.RunAsync`: expected failures map through a
  per-server translator to pinned messages, everything else backstops to
  `"Error: {message}"` — the LLM always gets readable text, never an MCP
  protocol error. Pod bootstrap is likewise declarative:
  `builder.AddToimiToolServer(...)` / `AddToimiDatabase<T>(...)` /
  `RequireConfig<T>(...)`, then `app.MigrateAndSeedAsync<T>(...)`.
```

- [ ] Commit: `docs: record shared tool-server bootstrap + never-throw ToolGuard in CLAUDE.md`

---

## Self-review checklist (verified against the code while writing this plan)

- Finding 1 (config bind ~6×): all real sites enumerated — koti:6, tietue:14/32/64, ruutu:9, web:40 — and the three helper shapes reproduce every message byte-identically; optional-with-fallback sites deliberately excluded. ✅
- Finding 2 (DB triad drift): ruutu gains `IsRelational()` (the named drift), web gains it too, tietue's guarded shape is the template; seeding stays inside the guard exactly as `TietueTestFactory` requires. ✅
- Finding 3 (never-throw ×~15): koti 5 blocks, verkko 2, ruutu 9 migrated; selain delegates; the 3 ruutu pragmas deleted; control-flow catches (koti area fallback, selain Browse steps) explicitly retained. ✅
- C6 fold-in: eager `ScriptBudget` resolve after `Build()` with the fail-fast comment. ✅
- Pinned strings: koti HA pair + "Entity not found.", verkko exact timeout string + inner-reason logic, selain constants — all preserved verbatim; per-server ToolErrorHandling/FetchUrlTool/ToolGuard tests untouched and passing through the tools. ✅
- Package coupling: honest — zero packages added anywhere; only a FrameworkReference in core.Tests. ✅
- Signature consistency: all builder helpers return `WebApplicationBuilder` or the bound value; app helpers are `Task`-returning verbs; footgun comment survives. ✅
- No placeholders: every step carries the actual code. ✅
