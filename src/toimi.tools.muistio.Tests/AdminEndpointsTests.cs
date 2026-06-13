using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.muistio.Admin;
using toimi.tools.muistio.Data;
using MemoryEntity = toimi.tools.muistio.Data.Memory;
using Xunit;

namespace toimi.tools.muistio.Tests;

public class AdminEndpointsTests : IDisposable
{
  private readonly MuistioTestFactory _factory = new();

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task Summary_returns_memory_summaries()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
    db.Memories.Add(new MemoryEntity
    {
      Id = Guid.NewGuid(),
      Content = "User likes oat milk",
      Source = "user",
      Confirmed = true,
      CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
      UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    });
    await db.SaveChangesAsync();

    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");

    Assert.NotNull(summary);
    var item = Assert.Single(summary!);
    Assert.Equal("memory", item.Kind);
    Assert.Equal("User likes oat milk", item.Title);
  }

  [Fact]
  public async Task Items_paginates()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
    for (var i = 0; i < 25; i++)
    {
      db.Memories.Add(new MemoryEntity
      {
        Id = Guid.NewGuid(),
        Content = $"Memory {i}",
        Source = "user",
        Confirmed = true,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
      });
    }
    await db.SaveChangesAsync();

    var client = _factory.CreateClient();
    var page1 = await client.GetFromJsonAsync<AdminEndpoints.PagedResult<AdminEndpoints.MemoryItem>>("/admin/items?page=1&size=10");
    Assert.Equal(10, page1!.Items.Count);
    Assert.Equal(25, page1.Total);
  }

  [Fact]
  public async Task Put_with_stale_If_Unmodified_Since_returns_409()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
      db.Memories.Add(new MemoryEntity
      {
        Id = id,
        Content = "old",
        Source = "user",
        Confirmed = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var req = new HttpRequestMessage(HttpMethod.Put, $"/admin/items/{id}")
    {
      Content = JsonContent.Create(new { content = "new" }),
    };
    req.Headers.IfUnmodifiedSince = DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime;
    var resp = await client.SendAsync(req);
    Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
  }

  [Fact]
  public async Task Delete_returns_204_and_removes_row()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
      db.Memories.Add(new MemoryEntity
      {
        Id = id,
        Content = "x",
        Source = "user",
        Confirmed = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var resp = await client.DeleteAsync($"/admin/items/{id}");
    Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<MuistioDbContext>();
    Assert.Null(await db2.Memories.FindAsync(id));
  }
}

public class MuistioTestFactory : WebApplicationFactory<Program>
{
  private readonly string _dbName = $"muistio-{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Muistio", "Server=ignored");
    builder.UseSetting("OpenAI:ApiKey", "test-key");
    builder.ConfigureServices(services =>
    {
      // Remove all descriptors registered by AddDbContext<MuistioDbContext>(UseNpgsql):
      // DbContextOptions<MuistioDbContext>, DbContextOptions (base), MuistioDbContext, and
      // IDbContextOptionsConfiguration<MuistioDbContext> (the factory that holds the Npgsql options).
      var configOptType = typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MuistioDbContext>);
      var toRemove = services
        .Where(d =>
          d.ServiceType == typeof(DbContextOptions<MuistioDbContext>) ||
          d.ServiceType == typeof(DbContextOptions) ||
          d.ServiceType == typeof(MuistioDbContext) ||
          d.ServiceType == configOptType)
        .ToList();
      foreach (var d in toRemove)
      {
        services.Remove(d);
      }
      services.AddDbContext<MuistioDbContext>(
        o => o.UseInMemoryDatabase(_dbName));
    });
  }
}
