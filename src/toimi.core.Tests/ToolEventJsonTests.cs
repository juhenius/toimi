using Xunit;

namespace Toimi.Core.Tests;

public class ToolEventJsonTests
{
  [Fact]
  public void Serializes_call_and_result_events_in_the_client_replay_shape()
  {
    var json = ToolEventJson.Serialize(
    [
      new ToolCallEvent("c1", "search", /*lang=json,strict*/ """{"query":"milk"}"""),
      new ToolResultEvent("c1", "found 3", 42),
    ]);

    Assert.NotNull(json);
    // PascalCase keys + lowercase "type" discriminator: pinned because useToimi.ts
    // parses exactly these keys on conversation replay, and tietue's EntityEvent
    // results must be the same dialect.
    Assert.Contains("\"type\":\"call\"", json);
    Assert.Contains("\"CallId\":\"c1\"", json);
    Assert.Contains("\"Name\":\"search\"", json);
    Assert.Contains("\"Arguments\":", json);
    Assert.Contains("\"type\":\"result\"", json);
    Assert.Contains("\"Result\":\"found 3\"", json);
    Assert.Contains("\"DurationMs\":42", json);
  }

  [Fact]
  public void Empty_input_serializes_to_null_not_an_empty_array()
  {
    Assert.Null(ToolEventJson.Serialize([]));
  }

  [Fact]
  public void Unknown_event_objects_are_skipped()
  {
    Assert.Null(ToolEventJson.Serialize([new object()]));
  }
}
