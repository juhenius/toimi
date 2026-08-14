using toimi.tools.ruutu.Transport;
using Xunit;

namespace toimi.tools.ruutu.Tests;

public class SceneActionsTests
{
  [Fact]
  public void Resolve_prefers_type_target_over_bare_type()
  {
    const string actions = /*lang=json,strict*/
      """{ "check": "http://host/hooks/a/1", "check:step-3": "http://host/hooks/b/2" }""";
    Assert.Equal("http://host/hooks/b/2", SceneActions.Resolve(actions, "check", "step-3"));
  }

  [Fact]
  public void Resolve_falls_back_to_bare_type()
  {
    const string actions = /*lang=json,strict*/ """{ "check": "http://host/hooks/a/1" }""";
    Assert.Equal("http://host/hooks/a/1", SceneActions.Resolve(actions, "check", "step-9"));
  }

  [Fact]
  public void Resolve_matches_bare_type_when_target_is_null()
  {
    const string actions = /*lang=json,strict*/ """{ "tap": "http://host/hooks/a/1" }""";
    Assert.Equal("http://host/hooks/a/1", SceneActions.Resolve(actions, "tap", null));
  }

  [Fact]
  public void Resolve_returns_null_when_nothing_wired()
  {
    const string actions = /*lang=json,strict*/ """{ "check": "http://host/hooks/a/1" }""";
    Assert.Null(SceneActions.Resolve(actions, "tap", "snooze"));
  }

  [Fact]
  public void Resolve_returns_null_for_null_or_empty_map()
  {
    Assert.Null(SceneActions.Resolve(null, "check", "x"));
    Assert.Null(SceneActions.Resolve("", "check", "x"));
  }

  [Fact]
  public void Resolve_never_throws_on_malformed_stored_map()
  {
    Assert.Null(SceneActions.Resolve("not json", "check", "x"));
    Assert.Null(SceneActions.Resolve(/*lang=json,strict*/ """["array"]""", "check", "x"));
    Assert.Null(SceneActions.Resolve(/*lang=json,strict*/ """{ "check": 42 }""", "check", "x"));
  }

  [Fact]
  public void Validate_accepts_a_wellformed_map()
  {
    SceneActions.Validate(/*lang=json,strict*/
      """{ "check": "http://host/hooks/a/1", "tap:snooze": "https://host/hooks/b/2" }""");
  }

  [Theory]
  [InlineData("not json")]
  [InlineData(/*lang=json,strict*/ """["array"]""")]
  [InlineData(/*lang=json,strict*/ """{ "check": 42 }""")]
  [InlineData(/*lang=json,strict*/ """{ "check": "not a url" }""")]
  [InlineData(/*lang=json,strict*/ """{ "check": "ftp://host/x" }""")]
  [InlineData(/*lang=json,strict*/ """{ " ": "http://host/hooks/a/1" }""")]
  public void Validate_rejects_malformed_maps(string actionsJson)
  {
    Assert.Throws<InvalidOperationException>(() => SceneActions.Validate(actionsJson));
  }
}
