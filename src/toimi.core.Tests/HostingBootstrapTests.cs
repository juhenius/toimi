using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Toimi.Core.Data;
using Toimi.Core.Hosting;
using Xunit;

namespace Toimi.Core.Tests;

public class HostingBootstrapTests
{
  private sealed class FakeOptions
  {
    public string? BaseUrl { get; set; }
  }

  private static WebApplicationBuilder Builder(params (string Key, string Value)[] settings)
  {
    var builder = WebApplication.CreateBuilder();
    // Hermetic: drop the default sources (env vars, appsettings) so a developer's
    // real environment can neither satisfy nor break the missing-config assertions.
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
    return builder;
  }

  [Fact]
  public void RequireConfig_binds_a_present_section()
  {
    var options = Builder(("Ha:BaseUrl", "http://ha.test")).RequireConfig<FakeOptions>("Ha");

    Assert.Equal("http://ha.test", options.BaseUrl);
  }

  [Fact]
  public void RequireConfig_missing_section_throws_the_uniform_message()
  {
    // Byte-identical to koti's original hand-rolled message.
    var ex = Assert.Throws<InvalidOperationException>(
      () => Builder().RequireConfig<FakeOptions>("HomeAssistant"));

    Assert.Equal("HomeAssistant configuration is required", ex.Message);
  }

  [Fact]
  public void RequireConnectionString_returns_a_present_string()
  {
    var cs = Builder(("ConnectionStrings:Ruutu", "Host=x;Database=ruutu")).RequireConnectionString("Ruutu");

    Assert.Equal("Host=x;Database=ruutu", cs);
  }

  [Fact]
  public void RequireConnectionString_missing_throws_the_uniform_message()
  {
    var ex = Assert.Throws<InvalidOperationException>(() => Builder().RequireConnectionString("Ruutu"));

    Assert.Equal("ConnectionStrings:Ruutu is required", ex.Message);
  }

  [Fact]
  public void RequireValue_returns_a_present_value()
  {
    Assert.Equal("sk-test", Builder(("OpenAI:ApiKey", "sk-test")).RequireValue("OpenAI:ApiKey"));
  }

  [Fact]
  public void RequireValue_missing_throws_the_uniform_message()
  {
    var ex = Assert.Throws<InvalidOperationException>(() => Builder().RequireValue("OpenAI:ApiKey"));

    Assert.Equal("OpenAI:ApiKey is required", ex.Message);
  }

  [Fact]
  public async Task AddToimiDatabase_registers_npgsql_with_snake_case_naming()
  {
    var builder = Builder(("ConnectionStrings:Toimi", "Host=localhost;Database=x"));
    builder.AddToimiDatabase<ToimiDbContext>("Toimi");
    await using var app = builder.Build();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ToimiDbContext>();
    Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    var conversation = db.Model.FindEntityType(typeof(Conversation))!;
    Assert.Equal("created_at", conversation.FindProperty(nameof(Conversation.CreatedAt))!.GetColumnName());
  }

  [Fact]
  public void AddToimiDatabase_missing_connection_string_fails_at_boot()
  {
    var ex = Assert.Throws<InvalidOperationException>(
      () => Builder().AddToimiDatabase<ToimiDbContext>("Toimi"));

    Assert.Equal("ConnectionStrings:Toimi is required", ex.Message);
  }

  [Fact]
  public async Task MigrateAndSeedAsync_skips_migrate_and_seed_when_not_relational()
  {
    // The guard tietue had and ruutu/web lacked: test hosts swap in the EF
    // in-memory provider, where MigrateAsync throws and seeding is unwanted.
    var builder = WebApplication.CreateBuilder();
    builder.Services.AddDbContext<ToimiDbContext>(o => o.UseInMemoryDatabase($"bootstrap-{Guid.NewGuid()}"));
    await using var app = builder.Build();

    var seeded = false;
    await app.MigrateAndSeedAsync<ToimiDbContext>(_ =>
    {
      seeded = true;
      return Task.CompletedTask;
    });

    Assert.False(seeded);
  }

  [Fact]
  public async Task AddToimiToolServer_names_the_mcp_server()
  {
    var builder = WebApplication.CreateBuilder();
    builder.AddToimiToolServer("test-server", typeof(HostingBootstrapTests).Assembly);
    await using var app = builder.Build();

    var options = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
    Assert.Equal("test-server", options.ServerInfo!.Name);
  }
}
