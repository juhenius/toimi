using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectsTests
{
  [Fact]
  public void Parses_notify_and_setfield()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/ """{"notify":{"message":"hi","priority":"high"},"setField":{"path":"status","value":"done"}}""");
    Assert.Equal("hi", e.Notify!.Message);
    Assert.Equal("high", e.Notify.Priority);
    Assert.Equal("status", e.SetField!.Path);
    Assert.Equal("\"done\"", e.SetField.ValueJson);
  }

  [Fact]
  public void Parses_escalate_string()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/ """{"escalate":"check the price trend"}""");
    Assert.Equal("check the price trend", e.Escalate);
  }

  [Fact]
  public void Empty_or_malformed_yields_no_effects()
  {
    Assert.Null(ScriptEffects.Parse("{}").Notify);
    Assert.Null(ScriptEffects.Parse("not json").Escalate);
  }
}
