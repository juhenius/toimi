using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

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
}
