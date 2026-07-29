using System.Text.Json;
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Shares a collection with ScriptHandlerTests: the watchdog test there abandons a thread
// that keeps burning CPU on a 2-core box, which thins the headroom this file's
// < 2s timing assertion depends on if the two ran in parallel.
[Collection("script-sandbox")]
public class ScriptEngineTests
{
  private readonly ScriptEngine _engine = new();

  [Fact]
  public void Returns_effects_json_from_script_using_data()
  {
    var effects = _engine.Evaluate(
      "return { notify: { message: 'hi ' + data.name } };",
      /*lang=json,strict*/ """{"name":"Jari"}""");
    Assert.Contains("hi Jari", effects);
  }

  [Fact]
  public void Script_with_no_return_yields_empty_object()
  {
    var effects = _engine.Evaluate("var x = 1;", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Has_no_clr_or_io_access()
  {
    var effects = _engine.Evaluate("return { x: System.IO.File }", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Infinite_loop_is_terminated_by_guard()
  {
    var effects = _engine.Evaluate("while(true){}", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Malformed_script_yields_empty_object()
  {
    Assert.Equal("{}", _engine.Evaluate("this is not js", """{}"""));
  }

  [Fact]
  public void Deep_recursion_is_terminated_by_guard()
  {
    var effects = _engine.Evaluate("function r(n){ return r(n + 1); } return r(0);", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Non_object_return_yields_empty_object()
  {
    Assert.Equal("{}", _engine.Evaluate("return 'a string';", """{}"""));
    Assert.Equal("{}", _engine.Evaluate("return 42;", """{}"""));
  }

  // --- Sandbox limit characterization (Tier 3) ---
  // These pin OBSERVED behavior of the configured Jint limits (2s timeout,
  // 8MB memory limit, 10k statements, recursion depth 100), not a spec.
  //
  // Two vectors were found (commit f76fd08) that defeat the cooperative
  // limits above because each is ONE atomic native call — Jint's
  // TimeoutInterval/LimitMemory checks only run between interpreter steps,
  // so a single native Regex.IsMatch or String.prototype.repeat call can
  // blow past every limit before the sandbox gets a chance to react:
  //
  // - Catastrophic regex backtracking (`/(a+)+$/.test('a'.repeat(40)+'b')`)
  //   ran 10+ seconds despite the 2s TimeoutInterval.
  // - `'a'.repeat(1e9)` reproducibly killed the test host process outright
  //   (SIGKILL from the OS OOM killer, exit 137) on every isolated run —
  //   not a catchable .NET exception, so LimitMemory never got a chance to
  //   apply. padStart/padEnd of the same magnitude were observed to fail
  //   safely (bounded to "{}"); repeat's allocation was not.
  //
  // Both got an explicit mitigation in ScriptEngine (see its header comment
  // for exactly what holds and what doesn't): `Constraints.RegexTimeout`
  // (500ms) bounds *literal* regex matching (`.test`/`.match`/`.replace`
  // with a regex literal), and a `String.prototype.repeat` guard installed
  // before the script body runs rejects counts above 1,000,000. Neither is a
  // complete fix for its whole vector class — see the residual-risk notes
  // in ScriptEngine's header (dynamically-constructed RegExp and `.split`
  // are NOT bounded by RegexTimeout) — but both close the specific
  // characterized crash, re-measured below rather than left as
  // documented-but-disabled findings.

  [Fact]
  public void Catastrophic_regex_is_interrupted_quickly()
  {
    // Bound tightened to 2s (observed ~0.8s): the dynamic-RegExp/`.split`
    // path is NOT covered by this guard and stalls up to ~5s (see
    // ScriptEngine's header comment), so a 5s bound here would sit exactly
    // on that other path's cap and flake rather than fail cleanly if this
    // literal-regex guard ever regressed.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var effects = _engine.Evaluate(
      "return { hit: /(a+)+$/.test('a'.repeat(40) + 'b') };", """{}""");
    sw.Stop();

    Assert.Equal("{}", effects);
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
  }

  [Fact]
  public void Huge_string_repeat_is_contained()
  {
    // Regression guard for the repeat(1e9) process-kill vector: the
    // String.prototype.repeat guard in ScriptEngine must reject this before
    // the native call ever runs. If this test starts crashing the host
    // again, the guard has regressed.
    var effects = _engine.Evaluate("return { s: 'a'.repeat(1e9) };", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Repeat_guard_resists_double_coercion_bypass()
  {
    // Regression guard for a since-fixed coercion TOCTOU: the guard used to
    // check `count` for the bound, then forward the SAME (possibly
    // object-typed) `count` to the real repeat, which coerces it AGAIN. This
    // object returns 1 on the FIRST coercion (passes the check) and 2,000,000
    // on any later one — a value over the 1,000,000 cap but small enough that
    // even a full regression back to double coercion only allocates ~4MB
    // here, not a repeat of the process-killing 1e9 case. The fix coerces
    // exactly once and forwards the resulting number, so `n` must be 1 (the
    // first-coercion value) and `calls` must be 1 (valueOf invoked exactly
    // once); under the original double-coercion bug this object discriminates
    // cleanly, coming back as n=2000000, calls=2 instead — verified by
    // reverting ScriptEngine.cs to the pre-fix commit and re-running this
    // test, which then fails on both counts.
    var effects = _engine.Evaluate(
      "var i = 0; var evil = { valueOf: function () { i++; return i === 1 ? 1 : 2000000; } }; " +
      "return { n: 'a'.repeat(evil).length, calls: i };",
      """{}""");

    using var doc = JsonDocument.Parse(effects);
    Assert.Equal(1, doc.RootElement.GetProperty("n").GetInt32());
    Assert.Equal(1, doc.RootElement.GetProperty("calls").GetInt32());
  }

  [Fact]
  public void Huge_array_fill_is_stopped()
  {
    // Observed: the 8MB memory limit trips before a 10-million-element fill
    // completes, so the sandbox falls back to its fail-safe empty object.
    var effects = _engine.Evaluate("return { a: new Array(1e7).fill(0).length };", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Malformed_data_json_yields_no_effects()
  {
    Assert.Equal("{}", _engine.Evaluate("return {ok:true}", "not json"));
  }

  [Fact]
  public void Sandbox_exposes_no_host_globals()
  {
    var effects = _engine.Evaluate(
      "return { globals: Object.getOwnPropertyNames(globalThis).join(',') };", """{}""");

    using var doc = JsonDocument.Parse(effects);
    var globals = doc.RootElement.GetProperty("globals").GetString();

    Assert.NotNull(globals);
    Assert.DoesNotContain("System", globals);
    Assert.DoesNotContain("importNamespace", globals);
    Assert.DoesNotContain("clr", globals);
  }
}
