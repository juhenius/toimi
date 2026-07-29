using Jint;

namespace toimi.tools.tietue.Scripts;

public class ScriptEngine
{
  // Jint's cooperative limits (TimeoutInterval/LimitMemory/MaxStatements) are only
  // checked between interpreter steps, so a single atomic native call can blow past
  // all of them before the sandbox gets a chance to react. Two such calls are guarded
  // explicitly below rather than relying on the cooperative limits alone:
  //
  //   - Regex.IsMatch backtracking (e.g. /(a+)+$/.test('a'.repeat(40)+'b')) can run
  //     for many seconds inside one native call; Constraints.RegexTimeout (set below
  //     to 500ms) bounds this for *literal* regex matching — `.test`/`.match`/
  //     `.replace` against a regex literal parsed from the script's own source text
  //     (observed: throws within ~1s). It does NOT bound a dynamically constructed
  //     `new RegExp(pattern)` or a `.split(regex)` call (literal or dynamic pattern
  //     alike) — both were observed to stall a flat ~5s regardless of the configured
  //     500ms timeout, AND regardless of input size (n=40/44/48 all measured ~5s,
  //     not the exponential spread catastrophic backtracking would otherwise produce
  //     across that range). That flatness means it is NOT simply "uninterrupted
  //     backtracking running to completion" — something in Jint 4.15.1 is capping it
  //     around 5s on these dynamic/`.split` paths, just not the configured
  //     Constraints.RegexTimeout. This looks like a real gap in Jint rather than a
  //     misconfiguration on our part (no other Constraints/Options knob changed the
  //     result), but it is still a bounded (~5s), not unbounded, stall, so it
  //     degrades a scheduler tick rather than hanging it forever. Closing this
  //     residual belongs at the handler/tick level (e.g. a wall-clock watchdog
  //     around the whole script invocation, or a process-level timeout), not inside
  //     ScriptEngine. ScriptHandler now enforces such a wall-clock budget
  //     (Scripts:TimeoutSeconds) around the whole evaluation, so this residual
  //     degrades a single handler run (recorded as a "timeout" result) rather than
  //     the tick — the underlying ~5s stall on the abandoned thread still happens.
  //   - String.prototype.repeat can allocate a multi-gigabyte string in one native
  //     call. padStart/padEnd of the same magnitude were observed to fail safely
  //     (bounded to a caught exception); repeat's allocation was not — it reliably
  //     killed the process outright (SIGKILL from the OS OOM killer, not a catchable
  //     .NET exception) before control ever returned to managed code. There is no
  //     Jint option for a max string length, so RepeatGuardScript replaces
  //     String.prototype.repeat with a bounds-checked wrapper before the script body
  //     runs, rejecting counts above MaxRepeatCount with a catchable RangeError.
  //     What actually makes this hold: the wrapper coerces its argument to a number
  //     EXACTLY ONCE (`Number(count)`) and forwards that number — never the original
  //     argument — to the real repeat, so an object whose valueOf/toString/
  //     Symbol.toPrimitive returns a different value on a second coercion can't
  //     answer differently the second time, because there is no second coercion; the
  //     property is installed non-writable/non-configurable so the script can't
  //     redefine it back to the raw original; and the raw original is reachable only
  //     through this wrapper's own closure, never handed to the script directly. An
  //     earlier version of this guard forwarded the original (possibly object-typed)
  //     `count` to the real repeat, which coerced it AGAIN — a TOCTOU an attacker
  //     could exploit by returning a small value on the first coercion (passing the
  //     check) and a huge one on the second (see Repeat_guard_resists_double_
  //     coercion_bypass in ScriptEngineTests.cs for the regression test).
  private const int MaxRepeatCount = 1_000_000;

  private static readonly string RepeatGuardScript = $$"""
    (function () {
      var __origRepeat = String.prototype.repeat;
      Object.defineProperty(String.prototype, 'repeat', {
        value: function (count) {
          // Coerce exactly once and pass the resulting NUMBER (not the original
          // argument) to the real repeat. If `count` were forwarded as-is, an
          // object with a `valueOf`/`Symbol.toPrimitive`/`toString` that returns a
          // small number on its first call (satisfying this check) and a huge one
          // on its second (when __origRepeat coerces it again) would sail straight
          // through: same object, coerced twice, two different answers (TOCTOU).
          var n = Number(count);
          if (n > {{MaxRepeatCount}}) {
            throw new RangeError('repeat count exceeds sandbox limit');
          }
          return __origRepeat.call(this, n);
        },
        writable: false,
        configurable: false,
      });
    })();
    """;

  public string Evaluate(string source, string dataJson)
  {
    try
    {
      var engine = new Engine(options =>
      {
        options
          .TimeoutInterval(TimeSpan.FromSeconds(2))
          .LimitMemory(8_000_000)
          .MaxStatements(10_000)
          .LimitRecursion(100)
          .Strict();
        options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(500);
      });

      engine.SetValue("__dataJson", dataJson);
      var wrapped = $"{RepeatGuardScript}\nJSON.stringify(((data) => {{ {source} }})(JSON.parse(__dataJson)) || {{}})";
      var result = engine.Evaluate(wrapped);
      if (!result.IsString())
      {
        return "{}";
      }

      var json = result.AsString();
      return json.TrimStart().StartsWith('{') ? json : "{}";
    }
    catch (Exception)
    {
      // Sandbox must be fail-safe: any script error/timeout/limit → no effects.
      return "{}";
    }
  }
}
