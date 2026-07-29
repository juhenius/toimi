# Tier 1 Test Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pin down and fix the seven likely-real bugs found in the 2026-07-29 test-coverage review, each as a failing test followed by a minimal fix.

**Architecture:** Pure TDD, one commit per bug. Two new test projects are created (`toimi.notifications.Tests`, `toimi.tools.koti.Tests`) and a vitest harness is stood up for the React ClientApp. All HTTP faking uses the repo's existing nested-`StubHandler` pattern (no mocking library). No behavior beyond the seven fixes is changed.

**Tech Stack:** .NET 10 / xUnit 2.9.3 (existing pattern), EF InMemory 10.0.10 for the hub test, vitest + @testing-library/react + jsdom for the frontend.

**Environment notes (critical):**
- `dotnet` is NOT on PATH in non-interactive shells. Run it as `mise exec dotnet -- dotnet <args>`. Same for node/npm: `mise exec node -- npm <args>`.
- All commands below assume cwd `/Users/jari/private/toimi` unless stated.
- Before each commit, run `mise exec dotnet -- dotnet format <changed-csproj> && mise exec dotnet -- dotnet format <changed-csproj> --verify-no-changes` for every modified C# project (test projects too). The repo enforces IDE0005/IDE0022/IDE0046 + whitespace as errors.
- Commit style: `<type>(<scope>): <subject>`. End commit messages with the Co-Authored-By line per repo convention.

---

### Task 1: tietue — `Schedules.Parse` crashes on valid-JSON-non-objects

**Bug:** `/Users/jari/private/toimi/src/toimi.tools.tietue/Scheduling/Schedules.cs:68` calls `root.TryGetProperty` before checking `ValueKind`. For `"[]"`, `"5"`, `"null"`, `"\"x\""`, `"true"` this throws `InvalidOperationException`, which the `catch (ex is JsonException or FormatException)` filter at line 78 does NOT catch. Reachable from `SetTriggerTool`, `UpdateTriggerTool` (via `TriggerRepository`), and `ScriptEffectApplier` — all with LLM- or script-supplied input.

**Files:**
- Modify: `src/toimi.tools.tietue/Scheduling/Schedules.cs`
- Test: `src/toimi.tools.tietue.Tests/SchedulesTests.cs`

- [ ] **Step 1: Write the failing test**

Append to the existing `SchedulesTests` class in `src/toimi.tools.tietue.Tests/SchedulesTests.cs` (inside the class, after `WithDefaultTimeZone_leaves_unparseable_unchanged`):

```csharp
  [Theory]
  [InlineData("[]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"daily\"")]
  [InlineData("true")]
  public void Non_object_schedule_json_yields_null_not_a_crash(string scheduleJson)
  {
    // Valid JSON that is not an object must behave like any other unparseable
    // schedule: null fire time, spec passed through unchanged — not an
    // InvalidOperationException escaping the MCP tool.
    Assert.Null(Schedules.InitialNextFireAt(scheduleJson, Now));
    Assert.Null(Schedules.NextAfter(scheduleJson, Now));
    Assert.Equal(scheduleJson, Schedules.WithDefaultTimeZone(scheduleJson, "Europe/Helsinki"));
  }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~Non_object_schedule_json"`
Expected: FAIL with `System.InvalidOperationException` (from `TryGetProperty` on a non-object element).

- [ ] **Step 3: Implement the fix**

In `src/toimi.tools.tietue/Scheduling/Schedules.cs`, inside `Parse`, add a ValueKind guard right after `var root = doc.RootElement;` (line 67):

```csharp
      using var doc = JsonDocument.Parse(scheduleJson);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return null;
      }
```

(No other lines change.)

- [ ] **Step 4: Run the full tietue test suite**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue/Scheduling/Schedules.cs src/toimi.tools.tietue.Tests/SchedulesTests.cs
git commit -m "fix(tietue): reject non-object schedule JSON instead of crashing"
```

---

### Task 2: core — repeated compaction accumulates summaries forever

**Bug:** `/Users/jari/private/toimi/src/toimi.core/ContextManager.cs:26-37` counts leading System messages as protected; line 80 inserts the summary as a System message at that boundary. On the next compaction the old summary counts as protected, so summaries accumulate (one permanent System message per compaction) and the reclaimable window shrinks every cycle.

**Files:**
- Modify: `src/toimi.core/ContextManager.cs`
- Test: `src/toimi.core.Tests/ContextManagerTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the `ContextManagerTests` class in `src/toimi.core.Tests/ContextManagerTests.cs`:

```csharp
  [Fact]
  public async Task Second_compaction_folds_the_prior_summary_instead_of_accumulating()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { new(ChatRole.System, "base prompt") };
    for (var i = 0; i < 40; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }

    Assert.True(await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default));

    for (var i = 0; i < 20; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }

    Assert.True(await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default));

    // The old summary must be summarized INTO the new one, not protected beside it —
    // otherwise every compaction leaves one more permanent System message and the
    // reclaimable window shrinks to nothing.
    Assert.Equal(1, messages.Count(m =>
      m.Role == ChatRole.System && (m.Text?.StartsWith("Summary of earlier conversation:") ?? false)));
    Assert.Equal("base prompt", messages[0].Text); // the real system prompt survives
  }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter "FullyQualifiedName~Second_compaction_folds"`
Expected: FAIL — `Assert.Equal(1, ...)` sees 2 summary messages.

- [ ] **Step 3: Implement the fix**

In `src/toimi.core/ContextManager.cs`:

Add a constant next to the existing ones (after line 10):

```csharp
  private const string SummaryPrefix = "Summary of earlier conversation:";
```

After the `systemCount` loop (after line 37, before `var nonSystemCount = ...`), back the boundary off over prior summaries so they re-enter the summarizable range:

```csharp
    // Prior compaction summaries are System messages sitting at the end of the
    // protected block. Treat them as summarizable content, not protection —
    // otherwise each compaction adds one more permanent summary and the
    // reclaimable window shrinks every cycle.
    while (systemCount > 0 && (messages[systemCount - 1].Text?.StartsWith(SummaryPrefix, StringComparison.Ordinal) ?? false))
    {
      systemCount--;
    }
```

And change line 80 to use the constant:

```csharp
    messages.Insert(systemCount, new(ChatRole.System, $"{SummaryPrefix}\n{summary}"));
```

- [ ] **Step 4: Run the full core test suite**

Run: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj`
Expected: PASS (existing compaction tests must still pass — none of them start with a summary message, so the back-off is a no-op for them).

- [ ] **Step 5: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj
mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
git add src/toimi.core/ContextManager.cs src/toimi.core.Tests/ContextManagerTests.cs
git commit -m "fix(core): fold prior summary into recompaction instead of accumulating"
```

---

### Task 3: core — tool-call content counts as zero tokens in context estimates

**Bug:** `ContextBudget.TotalChars` (`src/toimi.core/ContextBudget.cs:40-43`) and the fallback in `ContextManager.CompactIfNeeded` (`src/toimi.core/ContextManager.cs:19`) use `m.Text?.Length`, which only measures `TextContent`. Messages carrying only `FunctionCallContent`/`FunctionResultContent` measure 0 chars, so tool-heavy histories (worst: tietue's `AgentRunner`, which passes `budget: null`) never trigger compaction and blow the context window.

**Files:**
- Modify: `src/toimi.core/ContextBudget.cs`
- Modify: `src/toimi.core/ContextManager.cs`
- Test: `src/toimi.core.Tests/ContextManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

Append inside the `ContextManagerTests` class in `src/toimi.core.Tests/ContextManagerTests.cs`:

```csharp
  [Fact]
  public void Estimate_counts_function_call_and_result_content()
  {
    var budget = new ContextBudget();
    var payload = new string('r', 8000);
    var messages = new List<ChatMessage>
    {
      new(ChatRole.Assistant, [new FunctionCallContent("call1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
      new(ChatRole.Tool, [new FunctionResultContent("call1", payload)]),
    };

    // Tool-only messages have no TextContent; the estimate must still see their bulk
    // or tool-heavy histories never trigger compaction.
    Assert.True(budget.Estimate(messages) >= payload.Length / 4);
  }

  [Fact]
  public async Task Tool_result_heavy_history_triggers_compaction_without_an_anchor()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage>();
    for (var i = 0; i < 30; i++)
    {
      messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", new string('r', 1000))]));
    }

    // 30k chars of tool results ≈ 7.5k tokens — over a 5k budget. This is the
    // AgentRunner path (budget: null, chars/4 fallback only).
    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 5000, ct: default);

    Assert.True(compacted);
  }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter "FullyQualifiedName~Estimate_counts_function|FullyQualifiedName~Tool_result_heavy"`
Expected: both FAIL — estimate is 0, compaction returns false.

- [ ] **Step 3: Implement the fix**

Replace `TotalChars` in `src/toimi.core/ContextBudget.cs` (lines 40-43) with per-content measurement, and add the `System.Text.Json` using:

At the top of the file:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
```

Replace the `TotalChars` method:

```csharp
  internal static int TotalChars(List<ChatMessage> messages)
  {
    return messages.Sum(MessageChars);
  }

  private static int MessageChars(ChatMessage m)
  {
    var total = 0;
    foreach (var content in m.Contents)
    {
      total += content switch
      {
        TextContent t => t.Text?.Length ?? 0,
        FunctionCallContent fc => fc.Name.Length + (fc.Arguments is null ? 0 : JsonSerializer.Serialize(fc.Arguments).Length),
        FunctionResultContent fr => fr.Result?.ToString()?.Length ?? 0,
        _ => 0,
      };
    }

    return total;
  }
```

In `src/toimi.core/ContextManager.cs`, change line 19 so the anchorless fallback uses the same measurement:

```csharp
    var estimated = budget?.Estimate(messages) ?? ContextBudget.TotalChars(messages) / 4;
```

- [ ] **Step 4: Run the full core test suite**

Run: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj`
Expected: PASS. (The existing `Estimate_without_anchor_falls_back_to_chars_over_4` uses plain text and still passes.)

- [ ] **Step 5: Run the tietue suite too (it consumes ContextManager via AgentRunner)**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj
mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
git add src/toimi.core/ContextBudget.cs src/toimi.core/ContextManager.cs src/toimi.core.Tests/ContextManagerTests.cs
git commit -m "fix(core): count tool-call content in context token estimates"
```

---

### Task 4: notifications — ntfy priority silently downgrades on case mismatch

**Bug:** `PriorityMap` in `src/toimi.notifications/NtfyClient.cs:11-18` is a case-SENSITIVE dictionary and line 33 uses `GetValueOrDefault(priority, 3)`. tietue's `NotifyHandler` passes user-authored behavior-config priority straight through, so `"High"`/`"URGENT"` silently become normal priority. There is no `toimi.notifications.Tests` project yet.

**Files:**
- Create: `src/toimi.notifications.Tests/toimi.notifications.Tests.csproj`
- Create: `src/toimi.notifications.Tests/NtfyClientTests.cs`
- Modify: `src/toimi.notifications/NtfyClient.cs`
- Modify: `toimi.sln` (via `dotnet sln add`)

- [ ] **Step 1: Create the test project**

Write `src/toimi.notifications.Tests/toimi.notifications.Tests.csproj`:

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
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../toimi.notifications/toimi.notifications.csproj" />
  </ItemGroup>
</Project>
```

Then: `mise exec dotnet -- dotnet sln toimi.sln add src/toimi.notifications.Tests/toimi.notifications.Tests.csproj`

- [ ] **Step 2: Write the failing test**

Write `src/toimi.notifications.Tests/NtfyClientTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Toimi.Notifications;
using Xunit;

namespace Toimi.Notifications.Tests;

public class NtfyClientTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
      return new HttpResponseMessage(HttpStatusCode.OK);
    }
  }

  private static async Task<int> SentPriority(string priority)
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", priority: priority);

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    return doc.RootElement.GetProperty("priority").GetInt32();
  }

  [Theory]
  [InlineData("min", 1)]
  [InlineData("low", 2)]
  [InlineData("default", 3)]
  [InlineData("high", 4)]
  [InlineData("urgent", 5)]
  [InlineData("High", 4)]
  [InlineData("URGENT", 5)]
  [InlineData("Default", 3)]
  public async Task Priority_maps_case_insensitively(string priority, int expected)
  {
    // tietue's NotifyHandler passes user-authored behavior config straight through;
    // "High" or "URGENT" silently downgrading to normal means an urgent alert never
    // breaks through Do Not Disturb.
    Assert.Equal(expected, await SentPriority(priority));
  }

  [Theory]
  [InlineData("bogus")]
  [InlineData("")]
  public async Task Unknown_priority_falls_back_to_normal(string priority)
  {
    Assert.Equal(3, await SentPriority(priority));
  }
}
```

- [ ] **Step 3: Run the tests to verify the mixed-case rows fail**

Run: `mise exec dotnet -- dotnet test src/toimi.notifications.Tests/toimi.notifications.Tests.csproj`
Expected: `High`/`URGENT`/`Default` rows FAIL (got 3), lowercase rows and fallback rows PASS.

- [ ] **Step 4: Implement the fix**

In `src/toimi.notifications/NtfyClient.cs`, make the map case-insensitive (line 11):

```csharp
  private static readonly Dictionary<string, int> PriorityMap = new(StringComparer.OrdinalIgnoreCase)
  {
    ["min"] = 1,
    ["low"] = 2,
    ["default"] = 3,
    ["high"] = 4,
    ["urgent"] = 5
  };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `mise exec dotnet -- dotnet test src/toimi.notifications.Tests/toimi.notifications.Tests.csproj`
Expected: all PASS. Also run the verkko suite (its `SendNotificationTool` validates before calling — behavior unchanged): `mise exec dotnet -- dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj` — PASS.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.notifications/toimi.notifications.csproj
mise exec dotnet -- dotnet format src/toimi.notifications/toimi.notifications.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.notifications.Tests/toimi.notifications.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.notifications.Tests/toimi.notifications.Tests.csproj --verify-no-changes
git add src/toimi.notifications/NtfyClient.cs src/toimi.notifications.Tests/ toimi.sln
git commit -m "fix(notifications): map ntfy priority case-insensitively"
```

---

### Task 5: koti — CallService crashes on non-object data / empty response bodies

**Bugs** (all in the only write-path of the koti server):
1. `src/toimi.tools.koti/Tools/CallServiceTool.cs:23` only catches `JsonException`; valid-JSON-non-objects (`"[1,2]"`, `"5"`, `"null"`) pass through and `EnumerateObject()` at `HomeAssistantClient.cs:59` throws `InvalidOperationException` out of the MCP tool.
2. `HomeAssistantClient.CallServiceAsync` (`src/toimi.tools.koti/HomeAssistant/HomeAssistantClient.cs:73-74`) parses the response body after `EnsureSuccessStatusCode`; HA legitimately returns 200 with an empty body, so `JsonDocument.Parse("")` throws AFTER the device has already been switched — the user is told the call failed when it succeeded.
3. `entityId` param plus an `entity_id` key inside `data` writes `entity_id` twice into the payload; HA acts on whichever wins.

There is no `toimi.tools.koti.Tests` project yet.

**Files:**
- Create: `src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj`
- Create: `src/toimi.tools.koti.Tests/CallServiceToolTests.cs`
- Modify: `src/toimi.tools.koti/Tools/CallServiceTool.cs`
- Modify: `src/toimi.tools.koti/HomeAssistant/HomeAssistantClient.cs`
- Modify: `toimi.sln` (via `dotnet sln add`)

- [ ] **Step 1: Create the test project**

Write `src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj` (koti uses `Microsoft.NET.Sdk.Web`, so the test project needs the framework reference — same as `toimi.web.Tests`):

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
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../toimi.tools.koti/toimi.tools.koti.csproj" />
  </ItemGroup>
</Project>
```

Then: `mise exec dotnet -- dotnet sln toimi.sln add src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj`

- [ ] **Step 2: Write the failing tests**

Write `src/toimi.tools.koti.Tests/CallServiceToolTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using toimi.tools.koti.HomeAssistant;
using toimi.tools.koti.Tools;
using Xunit;

namespace toimi.tools.koti.Tests;

public class CallServiceToolTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Requests.Add(request);
      Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
      return respond(request);
    }
  }

  private static CallServiceTool Tool(StubHandler handler)
  {
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = "token" });
    return new CallServiceTool(client);
  }

  private static HttpResponseMessage JsonResponse(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
  }

  [Theory]
  [InlineData("[1,2]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"on\"")]
  public async Task Non_object_data_is_rejected_without_calling_home_assistant(string data)
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    var result = await tool.CallService("light", "turn_on", "light.living_room", data);

    // Valid JSON that is not an object must produce the friendly rejection, not an
    // InvalidOperationException escaping the MCP tool — and HA must never be called.
    Assert.Equal("Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.", result);
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task Empty_success_body_still_reports_success()
  {
    // HA returns 200 with an empty body for some service endpoints. By then the
    // device has already acted — reporting failure here is a lie.
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
    var tool = Tool(handler);

    var result = await tool.CallService("light", "turn_on", "light.living_room");

    Assert.Equal("Service called successfully.", result);
  }

  [Fact]
  public async Task Explicit_entity_id_parameter_wins_over_duplicate_in_data()
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    await tool.CallService("light", "turn_on", "light.living_room", /*lang=json,strict*/ """{"entity_id":"light.other","brightness":128}""");

    using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
    Assert.Equal("light.living_room", body.RootElement.GetProperty("entity_id").GetString());
    Assert.Equal(128, body.RootElement.GetProperty("brightness").GetInt32());
    // Exactly one entity_id key — a duplicate lets HA act on whichever wins.
    Assert.Equal(1, body.RootElement.EnumerateObject().Count(p => p.Name == "entity_id"));
  }

  [Fact]
  public async Task Posts_to_the_domain_service_path()
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    await tool.CallService("climate", "set_temperature", "climate.living_room", /*lang=json,strict*/ """{"temperature":22}""");

    var request = Assert.Single(handler.Requests);
    Assert.Equal("/api/services/climate/set_temperature", request.RequestUri!.AbsolutePath);
  }
}
```

- [ ] **Step 3: Run the tests to verify the first three fail**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj`
Expected: `Non_object_data_is_rejected...` FAILS with `InvalidOperationException`; `Empty_success_body...` FAILS with `JsonException`; `Explicit_entity_id_parameter_wins...` FAILS (two `entity_id` keys); `Posts_to_the_domain_service_path` PASSES.

- [ ] **Step 4: Implement the fixes**

In `src/toimi.tools.koti/Tools/CallServiceTool.cs`, replace the data-parsing block (lines 18-29) with:

```csharp
    JsonElement? parsedData = null;
    if (data is not null)
    {
      try
      {
        parsedData = JsonDocument.Parse(data).RootElement;
      }
      catch (JsonException)
      {
        return "Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.";
      }

      if (parsedData.Value.ValueKind != JsonValueKind.Object)
      {
        return "Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.";
      }
    }
```

In `src/toimi.tools.koti/HomeAssistant/HomeAssistantClient.cs`, in `CallServiceAsync`:

Replace the data-merge loop (lines 57-63) with:

```csharp
      if (data is not null)
      {
        foreach (var prop in data.Value.EnumerateObject())
        {
          if (entityId is not null && prop.NameEquals("entity_id"))
          {
            continue; // the explicit entityId parameter wins over a duplicate in data
          }

          prop.WriteTo(writer);
        }
      }
```

Replace the response handling (lines 71-74) with:

```csharp
    var response = await _http.PostAsync($"api/services/{domain}/{service}", content, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    // HA returns an empty body for some service endpoints; that is still success.
    return string.IsNullOrWhiteSpace(json)
      ? JsonDocument.Parse("null").RootElement
      : JsonDocument.Parse(json).RootElement;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj`
Expected: all PASS.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.tools.koti/toimi.tools.koti.csproj
mise exec dotnet -- dotnet format src/toimi.tools.koti/toimi.tools.koti.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj --verify-no-changes
git add src/toimi.tools.koti/ src/toimi.tools.koti.Tests/ toimi.sln
git commit -m "fix(koti): harden CallService against non-object data and empty responses"
```

---

### Task 6: web — SendMessage persistence failures escape the try/catch

**Bug:** In `src/toimi.web/Hubs/ToimiHub.cs`, `CreateAsync` (line 106) and the user-message `AddMessageAsync` (line 115) sit ABOVE the `try` opening at line 121. A DB failure there leaves the hub method as a generic `HubException` (no client `Error` event) and leaves the user message in `session.Messages` but not in the DB — in-memory context permanently diverges.

**Files:**
- Modify: `src/toimi.web.Tests/toimi.web.Tests.csproj` (add EF InMemory)
- Create: `src/toimi.web.Tests/ToimiHubTests.cs`
- Modify: `src/toimi.web/Hubs/ToimiHub.cs`

- [ ] **Step 1: Add the EF InMemory package to the test project**

In `src/toimi.web.Tests/toimi.web.Tests.csproj`, add to the PackageReference ItemGroup (version matches the tietue test project):

```xml
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.10" />
```

Verify it restores: `mise exec dotnet -- dotnet build src/toimi.web.Tests/toimi.web.Tests.csproj`

- [ ] **Step 2: Write the failing test (with the hub test fakes)**

Write `src/toimi.web.Tests/ToimiHubTests.cs`. The fakes make the hub fully offline: empty `McpServers` means the aggregator connects to nothing and `CallToolAsync` returns null; the fake LLM client streams one text update.

```csharp
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Toimi.Core.Llm;
using Toimi.Web.Hubs;
using Xunit;

namespace Toimi.Web.Tests;

public class ToimiHubTests
{
  private sealed class ThrowingDbContext(DbContextOptions<ToimiDbContext> options) : ToimiDbContext(options)
  {
    public bool ThrowOnSave { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
      return ThrowOnSave
        ? throw new InvalidOperationException("simulated database failure")
        : base.SaveChangesAsync(cancellationToken);
    }
  }

  private sealed class StreamingFakeChatClient : IChatClient
  {
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      yield return new ChatResponseUpdate(ChatRole.Assistant, "hello from fake");
      await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
      return null;
    }

    public void Dispose()
    {
    }
  }

  private sealed class FakeLlmProvider : ILlmClientProvider
  {
    public (IChatClient Client, ToolCallNotifier Notifier) Create()
    {
      var notifier = new ToolCallNotifier(new StreamingFakeChatClient());
      return (notifier, notifier);
    }
  }

  private sealed class RecordingClientProxy : ISingleClientProxy
  {
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
      Sent.Add((method, args));
      return Task.CompletedTask;
    }

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
    {
      throw new NotSupportedException();
    }
  }

  private sealed class FakeHubCallerClients : IHubCallerClients
  {
    public RecordingClientProxy CallerProxy { get; } = new();

    public ISingleClientProxy Caller => CallerProxy;
    public IClientProxy Others => CallerProxy;
    public IClientProxy All => CallerProxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => CallerProxy;
    public ISingleClientProxy Client(string connectionId) => CallerProxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => CallerProxy;
    public IClientProxy Group(string groupName) => CallerProxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => CallerProxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => CallerProxy;
    public IClientProxy OthersInGroup(string groupName) => CallerProxy;
    public IClientProxy User(string userId) => CallerProxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => CallerProxy;
    IClientProxy IHubCallerClients<IClientProxy>.Caller => CallerProxy;
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => CallerProxy;
  }

  private sealed class FakeHubCallerContext(string connectionId) : HubCallerContext
  {
    public override string ConnectionId => connectionId;
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
  }

  private static async Task<(ToimiHub Hub, FakeHubCallerClients Clients, ThrowingDbContext Db)> ConnectedHub()
  {
    var db = new ThrowingDbContext(new DbContextOptionsBuilder<ToimiDbContext>()
      .UseInMemoryDatabase($"hub-{Guid.NewGuid()}").Options);
    var hub = new ToimiHub(
      new ToimiConfiguration(), // empty McpServers: aggregator connects to nothing, fully offline
      new FakeLlmProvider(),
      new ConversationRepository(db),
      NullLogger<ToimiHub>.Instance)
    {
      Clients = new FakeHubCallerClients(),
      Context = new FakeHubCallerContext($"conn-{Guid.NewGuid()}"),
    };

    await hub.OnConnectedAsync();
    var clients = (FakeHubCallerClients)hub.Clients;
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Connected");
    return (hub, clients, db);
  }

  [Fact]
  public async Task SendMessage_streams_and_persists_user_and_assistant_messages()
  {
    var (hub, clients, db) = await ConnectedHub();

    await hub.SendMessage("hello");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ConversationCreated");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ReceiveToken" && (string?)s.Args[0] == "hello from fake");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    var conversation = Assert.Single(db.Conversations.ToList());
    var messages = db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToList();
    Assert.Equal(2, messages.Count);
    Assert.Contains(messages, m => m.Role == "user" && m.Content == "hello");
    Assert.Contains(messages, m => m.Role == "assistant" && m.Content == "hello from fake");

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Persistence_failure_sends_Error_and_keeps_session_consistent()
  {
    var (hub, clients, db) = await ConnectedHub();

    db.ThrowOnSave = true;
    // Must not throw a raw HubException out of the hub method.
    await hub.SendMessage("first try");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    // Recovery: the failed message must not haunt the in-memory session — the next
    // turn persists exactly its own user+assistant pair.
    db.ThrowOnSave = false;
    await hub.SendMessage("second try");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    var conversation = Assert.Single(db.Conversations.ToList());
    var messages = db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToList();
    Assert.Equal(2, messages.Count);
    Assert.DoesNotContain(messages, m => m.Content == "first try");

    await hub.OnDisconnectedAsync(null);
  }
}
```

Note: `ToimiHub`'s `Sessions` dictionary is static — every test must use a unique ConnectionId (the helper does) and call `OnDisconnectedAsync(null)` at the end.

- [ ] **Step 3: Run the tests to verify the failure-path test fails**

Run: `mise exec dotnet -- dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --filter "FullyQualifiedName~ToimiHubTests"`
Expected: `SendMessage_streams_and_persists...` PASSES (it pins current good behavior). `Persistence_failure_sends_Error...` FAILS — `hub.SendMessage("first try")` throws `InvalidOperationException` out of the hub method.

If the happy-path test fails to compile or run, fix the fakes first — it is the harness for the real test.

- [ ] **Step 4: Implement the fix**

In `src/toimi.web/Hubs/ToimiHub.cs`, replace lines 102-118 (from the `// Lazily create...` comment through `ToimiClientFactory.RefreshDynamicContext(session.Messages);`) with:

```csharp
    session.Messages.Add(new(ChatRole.User, message));

    try
    {
      // Lazily create the conversation row on the first message, so no-param
      // connects / reconnects / abandoned "New" sessions never leave orphan rows.
      if (session.ConversationId is null)
      {
        var created = await _repository.CreateAsync();
        session = session with { ConversationId = created.Id };
        Sessions[Context.ConnectionId] = session;
        await Clients.Caller.SendAsync("ConversationCreated", created.Id);
      }

      // Save user message to DB
      await _repository.AddMessageAsync(session.ConversationId.Value, "user", message);
    }
    catch (Exception ex)
    {
      // The user message never reached the DB; drop it from in-memory context too so
      // session and DB stay in step, and surface a client-visible Error instead of
      // letting SignalR fault the invocation with a generic HubException.
      session.Messages.RemoveAt(session.Messages.Count - 1);
      await Clients.Caller.SendAsync("Error", $"Failed to save your message: {ex.Message}");
      return;
    }

    // Update current time
    ToimiClientFactory.RefreshDynamicContext(session.Messages);
```

(The `session.Messages.Add` moves from its old position at line 112 to before the new try block; everything from `var assistantAppended = false;` down is unchanged.)

- [ ] **Step 5: Run the full web test suite**

Run: `mise exec dotnet -- dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj`
Expected: all PASS, including the four pre-existing test classes.

- [ ] **Step 6: Format and commit**

```bash
mise exec dotnet -- dotnet format src/toimi.web/toimi.web.csproj
mise exec dotnet -- dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj --verify-no-changes
git add src/toimi.web/Hubs/ToimiHub.cs src/toimi.web.Tests/
git commit -m "fix(web): surface persistence failures in SendMessage as Error events"
```

---

### Task 7: frontend — isStreaming leaks true when switching conversations mid-stream

**Bug:** `loadConversation` in `src/toimi.web/ClientApp/src/hooks/useToimi.ts:233-237` stops the connection and rebuilds it, but never clears `isStreaming`. If the user clicks another conversation while tokens are streaming, `MessageComplete` never arrives and the composer (`disabled={isStreaming || ...}`) stays disabled until a full page reload. `ConversationReset` (line 108-113) has the same gap for `newConversation`.

The ClientApp has NO test infrastructure — this task stands it up (vitest + @testing-library/react + jsdom) as its first step.

**Files:**
- Modify: `src/toimi.web/ClientApp/package.json` (devDeps + test script)
- Modify: `src/toimi.web/ClientApp/vite.config.ts` (vitest config)
- Create: `src/toimi.web/ClientApp/src/hooks/useToimi.test.ts`
- Modify: `src/toimi.web/ClientApp/src/hooks/useToimi.ts`

- [ ] **Step 1: Install the test harness**

```bash
cd src/toimi.web/ClientApp
mise exec node -- npm install -D vitest jsdom @testing-library/react @testing-library/dom
```

Add to `package.json` scripts:

```json
    "test": "vitest run",
```

- [ ] **Step 2: Wire vitest into vite.config.ts**

Replace the first line and add a `test` block, so the whole file becomes:

```ts
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  test: {
    environment: 'jsdom',
  },
  server: {
    port: 5173,
    proxy: {
      '/toimihub': {
        target: 'http://localhost:5000',
        ws: true,
      },
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
})
```

(`vitest/config`'s `defineConfig` is a superset of vite's; the dev/build behavior is unchanged.)

- [ ] **Step 3: Write the failing test**

Write `src/toimi.web/ClientApp/src/hooks/useToimi.test.ts`. The signalr mock must be built with `vi.hoisted` because `vi.mock` factories run before module-body statements:

```ts
import { renderHook, act, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const { fakes, FakeConnection } = vi.hoisted(() => {
  type Handler = (...args: unknown[]) => void

  class FakeConnection {
    handlers = new Map<string, Handler>()
    state = 'Connected'
    url: string

    constructor(url: string) {
      this.url = url
    }

    on(name: string, cb: Handler) {
      this.handlers.set(name, cb)
    }

    onreconnecting() {}
    onreconnected() {}
    onclose() {}

    start() {
      return Promise.resolve()
    }

    stop() {
      return Promise.resolve()
    }

    invoke() {
      return Promise.resolve()
    }

    fire(name: string, ...args: unknown[]) {
      this.handlers.get(name)?.(...args)
    }
  }

  return { fakes: [] as InstanceType<typeof FakeConnection>[], FakeConnection }
})

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private url = ''

    withUrl(url: string) {
      this.url = url
      return this
    }

    withAutomaticReconnect() {
      return this
    }

    configureLogging() {
      return this
    }

    build() {
      const connection = new FakeConnection(this.url)
      fakes.push(connection)
      return connection
    }
  }

  return {
    HubConnectionBuilder,
    HubConnectionState: { Connected: 'Connected' },
    LogLevel: { Warning: 'Warning' },
  }
})

import { useToimi } from './useToimi'

describe('useToimi streaming state', () => {
  beforeEach(() => {
    fakes.length = 0
  })

  it('clears isStreaming when switching conversations mid-stream', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const first = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    act(() => {
      first.fire('ReceiveToken', 'partial ')
    })
    expect(result.current.isStreaming).toBe(true)

    // Switching conversations tears down the connection: MessageComplete will never
    // arrive, so the flag must be cleared here or the composer stays disabled forever.
    act(() => {
      result.current.loadConversation('11111111-1111-1111-1111-111111111111')
    })
    await waitFor(() => expect(fakes.length).toBe(2))
    const second = fakes[fakes.length - 1]
    act(() => {
      second.fire(
        'ConversationLoaded',
        '11111111-1111-1111-1111-111111111111',
        JSON.stringify([{ role: 'user', content: 'old message' }]),
      )
    })

    expect(result.current.isStreaming).toBe(false)
  })

  it('clears isStreaming on ConversationReset', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const connection = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    expect(result.current.isStreaming).toBe(true)

    act(() => {
      connection.fire('ConversationReset')
    })

    expect(result.current.isStreaming).toBe(false)
    expect(result.current.messages).toEqual([])
  })
})
```

- [ ] **Step 4: Run the tests to verify they fail**

Run (from `src/toimi.web/ClientApp`): `mise exec node -- npm test`
Expected: both tests FAIL on the `isStreaming` assertion (received `true`).

- [ ] **Step 5: Implement the fix**

In `src/toimi.web/ClientApp/src/hooks/useToimi.ts`:

`loadConversation` (lines 233-237) becomes:

```ts
  const loadConversation = useCallback((id: string) => {
    connectionRef.current?.stop()
    // The old connection is gone mid-stream: MessageComplete will never arrive,
    // so clear the flag here or the composer stays disabled forever.
    setIsStreaming(false)
    conversationIdRef.current = id
    setReconnectCounter(c => c + 1)
  }, [])
```

The `ConversationReset` handler (lines 108-113) becomes:

```ts
    connection.on('ConversationReset', () => {
      setMessages([])
      setIsStreaming(false)
      setCurrentConversationId(null)
      currentConversationIdRef.current = null
      conversationIdRef.current = undefined
    })
```

- [ ] **Step 6: Run tests and lint**

Run (from `src/toimi.web/ClientApp`):

```bash
mise exec node -- npm test
mise exec node -- npm run lint
```

Expected: tests PASS, lint clean. If eslint flags the empty `onreconnecting() {}` bodies or similar in the test file, fix per its suggestion (e.g. add `// noop` comments) rather than disabling rules broadly.

- [ ] **Step 7: Commit (two commits: harness, then fix)**

```bash
cd /Users/jari/private/toimi
git add src/toimi.web/ClientApp/package.json src/toimi.web/ClientApp/package-lock.json src/toimi.web/ClientApp/vite.config.ts
git commit -m "chore(web): add vitest harness for ClientApp"
git add src/toimi.web/ClientApp/src/hooks/useToimi.test.ts src/toimi.web/ClientApp/src/hooks/useToimi.ts
git commit -m "fix(web): clear streaming flag when switching conversations mid-stream"
```

---

### Final verification (after all tasks)

- [ ] Run the whole solution: `mise exec dotnet -- dotnet test toimi.sln` — expected: all projects PASS (including the two new test projects).
- [ ] Run `./scripts/lint.sh` — expected: dotnet format and yamllint clean (shellcheck is skipped locally; CI covers it).
- [ ] Frontend: `cd src/toimi.web/ClientApp && mise exec node -- npm test && mise exec node -- npm run build` — expected: tests pass, production build succeeds.

### Known deferred items (NOT in this plan)

Tier 2/3 from the review (ToolCallNotifier tests, RecurrenceCalculator DST edges, UpdateTriggerTool, HtmlExtractor, McpToolAggregator, koti ListEntities, etc.) are deliberately out of scope — reassess after this plan lands.
