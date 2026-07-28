using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class OverlayStackTests
{
  [Fact]
  public void Push_makes_new_overlay_the_top()
  {
    var stack = OverlayStack.Parse("[]");
    var (next, _) = OverlayStack.Push(stack, new OverlayFrame("notification", /*lang=json,strict*/ "{\"x\":1}", DateTimeOffset.UnixEpoch));
    Assert.Single(next);
    Assert.Equal("notification", next[0].Template);
  }

  [Fact]
  public void Push_keeps_newest_on_top_lifo()
  {
    var stack = OverlayStack.Parse("[]");
    (stack, _) = OverlayStack.Push(stack, new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch));
    (stack, _) = OverlayStack.Push(stack, new OverlayFrame("b", "{}", DateTimeOffset.UnixEpoch.AddSeconds(1)));
    Assert.Equal("b", stack[0].Template);
    Assert.Equal("a", stack[1].Template);
  }

  [Fact]
  public void Pop_removes_top_and_returns_remaining_top()
  {
    var stack = new[]
    {
      new OverlayFrame("b", "{}", DateTimeOffset.UnixEpoch.AddSeconds(1)),
      new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch)
    };
    var (next, top) = OverlayStack.Pop(stack);
    Assert.Single(next);
    Assert.Equal("a", top!.Template);
  }

  [Fact]
  public void Pop_on_empty_returns_empty_and_null()
  {
    var (next, top) = OverlayStack.Pop([]);
    Assert.Empty(next);
    Assert.Null(top);
  }

  [Fact]
  public void Pop_returns_null_top_when_only_one_frame()
  {
    var stack = new[] { new OverlayFrame("a", "{}", DateTimeOffset.UnixEpoch) };
    var (next, top) = OverlayStack.Pop(stack);
    Assert.Empty(next);
    Assert.Null(top);
  }

  [Fact]
  public void Push_evicts_oldest_when_cap_exceeded()
  {
    var stack = Array.Empty<OverlayFrame>();
    for (var i = 0; i < OverlayStack.MaxDepth; i++)
    {
      (stack, _) = OverlayStack.Push(stack, new OverlayFrame($"t{i}", "{}", DateTimeOffset.UnixEpoch.AddSeconds(i)));
    }

    OverlayFrame? evicted;
    (stack, evicted) = OverlayStack.Push(stack, new OverlayFrame("new", "{}", DateTimeOffset.UnixEpoch.AddSeconds(100)));

    Assert.Equal(OverlayStack.MaxDepth, stack.Length);
    Assert.Equal("new", stack[0].Template);
    Assert.NotNull(evicted);
    Assert.Equal("t0", evicted.Template);
  }

  [Fact]
  public void Serialize_and_parse_round_trip()
  {
    var stack = new[] { new OverlayFrame("a", /*lang=json,strict*/ "{\"k\":1}", DateTimeOffset.UnixEpoch) };
    var json = OverlayStack.Serialize(stack);
    var parsed = OverlayStack.Parse(json);
    Assert.Single(parsed);
    Assert.Equal("a", parsed[0].Template);
    Assert.Equal(/*lang=json,strict*/ "{\"k\":1}", parsed[0].DataJson);
  }
}
