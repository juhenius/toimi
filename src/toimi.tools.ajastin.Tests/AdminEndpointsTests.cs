using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.ajastin.Data;
using Xunit;

namespace toimi.tools.ajastin.Tests;

public class AdminEndpointsTests : IDisposable
{
  private readonly AjastinTestFactory _factory = new();

  [Fact]
  public async Task Summary_returns_schedule_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AjastinDbContext>();
      db.Schedules.Add(new Schedule
      {
        Id = Guid.NewGuid(),
        Name = "Morning check",
        CronExpression = "0 8 * * *",
        Prompt = "Summarize calendar",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("schedule", item.Kind);
    Assert.Equal("Morning check", item.Title);
  }

  [Fact]
  public async Task RunNow_sets_RunAt_to_recent_time()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AjastinDbContext>();
      db.Schedules.Add(new Schedule
      {
        Id = id, Name = "x", Prompt = "p", Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    (await client.PostAsync($"/admin/items/{id}/run-now", null)).EnsureSuccessStatusCode();
    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<AjastinDbContext>();
    var s = await db2.Schedules.FindAsync(id);
    Assert.NotNull(s!.RunAt);
    Assert.True((DateTimeOffset.UtcNow - s.RunAt.Value).TotalSeconds < 5);
  }

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }
}

public class AjastinTestFactory : WebApplicationFactory<Program>
{
  private readonly string _dbName = $"ajastin-{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Ajastin", "Server=ignored");
    builder.UseSetting("Toimi:OpenAI:ApiKey", "test");
    builder.UseSetting("Toimi:OpenAI:Model", "gpt-4");
    builder.ConfigureServices(services =>
    {
      var toRemove = services.Where(d =>
        d.ServiceType == typeof(DbContextOptions<AjastinDbContext>)
        || d.ServiceType == typeof(DbContextOptions)
        || d.ServiceType == typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<AjastinDbContext>)
        || d.ServiceType == typeof(AjastinDbContext)).ToArray();
      foreach (var d in toRemove) services.Remove(d);

      services.AddDbContext<AjastinDbContext>(o => o.UseInMemoryDatabase(_dbName));

      var hosted = services.Where(d => d.ImplementationType?.Name == "ScheduleWorker").ToArray();
      foreach (var h in hosted) services.Remove(h);
    });
  }
}
