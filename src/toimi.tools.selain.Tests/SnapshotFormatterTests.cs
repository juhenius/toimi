using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class SnapshotFormatterTests
{
  [Fact]
  public void Truncate_returns_short_input_unchanged()
  {
    Assert.Equal("hello", SnapshotFormatter.Truncate("hello", SnapshotFormatter.ActionCap));
  }

  [Fact]
  public void Truncate_caps_long_input_with_marker()
  {
    var input = new string('x', SnapshotFormatter.ActionCap + 500);
    var result = SnapshotFormatter.Truncate(input, SnapshotFormatter.ActionCap);
    Assert.StartsWith(new string('x', 100), result);
    Assert.EndsWith(SnapshotFormatter.TruncationMarker, result);
    Assert.True(result.Length < input.Length);
  }

  [Fact]
  public void Truncate_at_read_cap_uses_its_own_marker_not_a_read_page_pointer()
  {
    var input = new string('x', SnapshotFormatter.ReadCap + 500);
    var result = SnapshotFormatter.Truncate(input, SnapshotFormatter.ReadCap);
    Assert.EndsWith(SnapshotFormatter.ReadTruncationMarker, result);
    // read_page output must not tell the caller to use read_page.
    Assert.DoesNotContain("read_page", result[SnapshotFormatter.ReadCap..]);
  }

  [Fact]
  public void Caps_are_15k_for_actions_and_50k_for_read_page()
  {
    Assert.Equal(15_000, SnapshotFormatter.ActionCap);
    Assert.Equal(50_000, SnapshotFormatter.ReadCap);
  }

  [Fact]
  public void Hash_is_stable_for_equal_input_and_differs_otherwise()
  {
    Assert.Equal(SnapshotFormatter.Hash("abc"), SnapshotFormatter.Hash("abc"));
    Assert.NotEqual(SnapshotFormatter.Hash("abc"), SnapshotFormatter.Hash("abd"));
  }
}
