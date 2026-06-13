using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.muistutin.Data;
using Xunit;

namespace toimi.tools.muistutin.Tests;

public class AdminEndpointsTests : IDisposable
{
  private readonly MuistutinTestFactory _factory = new();

  [Fact]
  public async Task Summary_returns_reminder_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
      db.Reminders.Add(new Reminder
      {
        Id = Guid.NewGuid(),
        Title = "Buy milk",
        Description = null,
        DateTimeUtc = DateTimeOffset.UtcNow.AddHours(2),
        TimeZone = "Europe/Helsinki",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("reminder", item.Kind);
    Assert.Equal("Buy milk", item.Title);
  }

  [Fact]
  public async Task Complete_marks_reminder_completed()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
      db.Reminders.Add(new Reminder
      {
        Id = id, Title = "x", DateTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
        TimeZone = "UTC", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var resp = await client.PostAsync($"/admin/items/{id}/complete", null);
    resp.EnsureSuccessStatusCode();
    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<MuistutinDbContext>();
    Assert.True((await db2.Reminders.FindAsync(id))!.IsCompleted);
  }

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }
}

public class MuistutinTestFactory : WebApplicationFactory<Program>
{
  private readonly string _dbName = $"muistutin-{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Muistutin", "Server=ignored");
    builder.ConfigureServices(services =>
    {
      // Narrow swap: remove only the 4 EF descriptors tied to MuistutinDbContext.
      var configOptType = typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MuistutinDbContext>);
      var toRemove = services.Where(d =>
        d.ServiceType == typeof(DbContextOptions<MuistutinDbContext>)
        || d.ServiceType == typeof(DbContextOptions)
        || d.ServiceType == configOptType
        || d.ServiceType == typeof(MuistutinDbContext)).ToArray();
      foreach (var d in toRemove) services.Remove(d);

      services.AddDbContext<MuistutinDbContext>(o => o.UseInMemoryDatabase(_dbName));

      // Remove the hosted ReminderNotifier so it doesn't fire during tests.
      var hosted = services.Where(d => d.ImplementationType?.Name == "ReminderNotifier").ToArray();
      foreach (var h in hosted) services.Remove(h);
    });
  }
}
