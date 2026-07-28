using toimi.tools.ruutu.Data.Repositories;
using Xunit;

namespace toimi.tools.ruutu.Tests;

public class DisplayIdentifierTests
{
  [Theory]
  [InlineData("living-room")]
  [InlineData("kitchen")]
  [InlineData("display-1")]
  [InlineData("a")]
  [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // exactly 64 chars
  public async Task Accepts_valid_slugs(string id)
  {
    using var db = TestDb.New();
    var repo = new DisplayRepository(db);
    var d = await repo.RegisterAsync(id, null);
    Assert.Equal(id, d.Identifier);
  }

  [Theory]
  [InlineData("x\";<script>alert(1)</script>//")]
  [InlineData("has space")]
  [InlineData("Upper")]
  [InlineData("-leading-dash")]
  [InlineData("has\"quote")]
  [InlineData("")]
  [InlineData("way-too-long-way-too-long-way-too-long-way-too-long-way-too-long-x")] // >64
  public async Task Rejects_non_slug_identifiers(string id)
  {
    using var db = TestDb.New();
    var repo = new DisplayRepository(db);
    await Assert.ThrowsAsync<ArgumentException>(() => repo.RegisterAsync(id, null));
  }
}
