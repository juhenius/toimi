using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Toimi.Core.Admin;
using Xunit;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tests;

public class AdminEndpointsTests : IDisposable
{
  private readonly TietueTestFactory _factory = new();

  [Fact]
  public async Task Summary_returns_entity_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity
      {
        Id = Guid.NewGuid(),
        Type = "note",
        Data = JsonDocument.Parse("""{"title":"x"}"""),
        Tags = ["a"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("note", item.Kind);
  }

  [Fact]
  public async Task Delete_removes_entity()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity
      {
        Id = id,
        Type = "note",
        Data = JsonDocument.Parse("{}"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    var resp = await client.DeleteAsync($"/admin/items/{id}");
    resp.EnsureSuccessStatusCode();

    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<TietueDbContext>();
    Assert.Null(await db2.Entities.FindAsync(id));
  }

  [Fact]
  public async Task Items_returns_entity_with_data()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity
      {
        Id = id,
        Type = "note",
        Data = JsonDocument.Parse("""{"title":"hi"}"""),
        Tags = ["a"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    using var doc = JsonDocument.Parse(await client.GetStringAsync("/admin/items"));
    var item = doc.RootElement.GetProperty("items")[0];
    Assert.Equal("note", item.GetProperty("type").GetString());
    Assert.Contains("title", item.GetProperty("data").GetString());
  }

  [Fact]
  public async Task Types_lists_type_definitions()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var repo = scope.ServiceProvider.GetRequiredService<Types.TypeRepository>();
      await repo.DefineAsync("note", /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""",
                             /*lang=json,strict*/
                             """[{"behavior":"SemanticIndex","config":{"fields":["title"]}}]""");
    }

    var client = _factory.CreateClient();
    using var doc = JsonDocument.Parse(await client.GetStringAsync("/admin/types"));
    var first = doc.RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == "note");
    Assert.Contains("title", first.GetProperty("jsonSchema").GetString());
    Assert.Contains("SemanticIndex", first.GetProperty("behaviors").GetString());
  }

  [Fact]
  public async Task Type_detail_returns_single_definition()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var repo = scope.ServiceProvider.GetRequiredService<Types.TypeRepository>();
      await repo.DefineAsync("note", /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""",
                             /*lang=json,strict*/
                             """[{"behavior":"SemanticIndex","config":{"fields":["title"]}}]""");
    }

    var client = _factory.CreateClient();
    using var doc = JsonDocument.Parse(await client.GetStringAsync("/admin/types/note"));
    Assert.Equal("note", doc.RootElement.GetProperty("name").GetString());
    Assert.Contains("title", doc.RootElement.GetProperty("jsonSchema").GetString());
  }

  [Fact]
  public async Task Type_detail_returns_404_for_unknown_type()
  {
    var client = _factory.CreateClient();
    var response = await client.GetAsync("/admin/types/does-not-exist");
    Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Item_triggers_are_listed()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity { Id = id, Type = "reminder", Data = JsonDocument.Parse("""{"title":"x"}"""), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
      db.Triggers.Add(new Trigger { Id = Guid.NewGuid(), EntityId = id, Schedule = /*lang=json,strict*/ """{"at":"2026-07-01T09:00:00Z"}""", HandlerKind = "notify", Enabled = true, NextFireAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    using var triggers = JsonDocument.Parse(await client.GetStringAsync($"/admin/items/{id}/triggers"));
    Assert.Equal("notify", triggers.RootElement[0].GetProperty("handlerKind").GetString());
  }

  [Fact]
  public async Task Item_events_are_listed()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity { Id = id, Type = "reminder", Data = JsonDocument.Parse("""{"title":"x"}"""), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
      db.EntityEvents.Add(new EntityEvent { Id = Guid.NewGuid(), EntityId = id, OccurrenceUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Kind = "notify", Status = "pending", CreatedAt = DateTimeOffset.UtcNow });
      db.EntityEvents.Add(new EntityEvent { Id = Guid.NewGuid(), EntityId = id, OccurrenceUtc = DateTimeOffset.UtcNow, Kind = "notify", Status = "sent", CreatedAt = DateTimeOffset.UtcNow });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    using var events = JsonDocument.Parse(await client.GetStringAsync($"/admin/items/{id}/events"));
    Assert.Equal("sent", events.RootElement[0].GetProperty("status").GetString());
  }

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }
}

public class TietueTestFactory : WebApplicationFactory<Program>
{
  private readonly string _dbName = $"tietue-{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Tietue", "Server=ignored");
    builder.UseSetting("OpenAI:ApiKey", "test-key");
    builder.UseSetting("Toimi:OpenAI:ApiKey", "test-key");
    builder.UseSetting("Toimi:OpenAI:Model", "gpt-4o");
    builder.ConfigureServices(services =>
    {
      var configOptType = typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TietueDbContext>);
      var toRemove = services.Where(d =>
        d.ServiceType == typeof(DbContextOptions<TietueDbContext>)
        || d.ServiceType == typeof(DbContextOptions)
        || d.ServiceType == configOptType
        || d.ServiceType == typeof(TietueDbContext)).ToArray();
      foreach (var d in toRemove)
      {
        services.Remove(d);
      }

      services.AddDbContext<TietueDbContext>(o => o.UseInMemoryDatabase(_dbName));
    });
  }
}
