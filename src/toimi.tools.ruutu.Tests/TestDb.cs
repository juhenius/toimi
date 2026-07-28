using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data;

namespace toimi.tools.ruutu.Tests;

public static class TestDb
{
  public static RuutuDbContext New()
  {
    return new(Options());
  }

  private static DbContextOptions<RuutuDbContext> Options()
  {
    return new DbContextOptionsBuilder<RuutuDbContext>()
      .UseInMemoryDatabase($"ruutu-{Guid.NewGuid()}")
      .Options;
  }
}
