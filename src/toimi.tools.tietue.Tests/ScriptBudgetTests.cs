using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptBudgetTests
{
  [Fact]
  public void From_defaults_reproduce_the_documented_ladder()
  {
    var b = ScriptBudget.From(new ScriptOptions());

    Assert.Equal(TimeSpan.FromSeconds(20), b.Script);
    Assert.Equal(20_000, b.ScriptMs);
    Assert.Equal(TimeSpan.FromSeconds(25), b.HttpTimeout);
    Assert.Equal(TimeSpan.FromSeconds(30), b.Watchdog);
    Assert.Equal(TimeSpan.FromSeconds(50), b.TokenTtl); // == the old TimeoutSeconds + 30
    Assert.Equal(TimeSpan.FromSeconds(60), b.Effects);
  }

  [Fact]
  public void From_clamps_script_time_to_suoritins_max()
  {
    // suoritin clamps timeoutMs at 60s (executor.ts MAX_TIMEOUT_MS); budgeting
    // beyond it would make the outer layers wait for time the sandbox never grants.
    var b = ScriptBudget.From(new ScriptOptions { TimeoutSeconds = 120 });

    Assert.Equal(TimeSpan.FromSeconds(ScriptBudget.MaxScriptSeconds), b.Script);
    Assert.Equal(TimeSpan.FromSeconds(65), b.HttpTimeout);
    Assert.Equal(TimeSpan.FromSeconds(70), b.Watchdog);
  }

  [Fact]
  public void Ladder_ordering_holds_by_construction()
  {
    var b = ScriptBudget.From(new ScriptOptions { TimeoutSeconds = 7 });

    Assert.True(b.Script < b.HttpTimeout);
    Assert.True(b.HttpTimeout < b.Watchdog);
    Assert.True(b.Watchdog < b.TokenTtl);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-10)]
  public void Non_positive_script_time_fails_fast(int seconds)
  {
    // A misconfigured Scripts:TimeoutSeconds must fail at startup, not produce
    // a zero-length watchdog at fire time (the old -10 test hack's territory).
    Assert.Throws<ArgumentOutOfRangeException>(
      () => ScriptBudget.From(new ScriptOptions { TimeoutSeconds = seconds }));
  }

  [Fact]
  public void Watchdog_margin_below_http_margin_is_rejected()
  {
    // The watchdog must not fire before the HTTP client has had its chance to
    // time out cleanly — equal margins are allowed (tests), inverted are not.
    Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptBudget(
      TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60)));
  }

  [Fact]
  public void Equal_margins_are_allowed_for_tiny_test_budgets()
  {
    var b = new ScriptBudget(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(60));

    Assert.Equal(TimeSpan.FromMilliseconds(40), b.Watchdog);
  }
}
