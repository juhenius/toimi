using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectsTests
{
  [Fact]
  public void Parses_setfield_and_mcpcall_arrays()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/
      """{"setField":[{"path":"a","value":1},{"path":"b","value":"x"}],"mcpCall":[{"tool":"display_show","args":{"identifier":"hall"}}]}""");

    Assert.Equal(2, e.SetFields.Count);
    Assert.Equal("a", e.SetFields[0].Path);
    Assert.Equal("1", e.SetFields[0].ValueJson);
    Assert.Equal("\"x\"", e.SetFields[1].ValueJson);
    var call = Assert.Single(e.McpCalls);
    Assert.Equal("display_show", call.Tool);
    Assert.Contains("hall", call.ArgsJson);
  }

  [Fact]
  public void Empty_object_yields_no_effects()
  {
    var e = ScriptEffects.Parse("{}");
    Assert.Empty(e.SetFields);
    Assert.Empty(e.McpCalls);
  }

  [Fact]
  public void Malformed_json_yields_no_effects()
  {
    var e = ScriptEffects.Parse("{nope");
    Assert.Empty(e.SetFields);
    Assert.Empty(e.McpCalls);
  }

  [Fact]
  public void Non_array_effect_values_are_ignored()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/ """{"setField":{"path":"a","value":1},"mcpCall":"x"}""");
    Assert.Empty(e.SetFields);
    Assert.Empty(e.McpCalls);
  }

  [Fact]
  public void Items_missing_required_fields_are_skipped()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/
      """{"setField":[{"value":1},{"path":"ok","value":2}],"mcpCall":[{"args":{}},{"tool":"t","args":{}}]}""");
    Assert.Equal("ok", Assert.Single(e.SetFields).Path);
    Assert.Equal("t", Assert.Single(e.McpCalls).Tool);
  }

  [Fact]
  public void Mcpcall_without_args_defaults_to_empty_object()
  {
    var e = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"list_types"}]}""");
    Assert.Equal("{}", Assert.Single(e.McpCalls).ArgsJson);
  }
}
