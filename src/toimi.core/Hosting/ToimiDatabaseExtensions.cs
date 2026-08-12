using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Toimi.Core.Hosting;

/// <summary>
/// The pod database triad in one place: Npgsql + snake_case registration from
/// a required connection string, and the migrate-then-seed boot scope guarded
/// by IsRelational() (test hosts swap in the EF in-memory provider, where
/// MigrateAsync throws and seeding is unwanted).
/// </summary>
public static class ToimiDatabaseExtensions
{
  public static WebApplicationBuilder AddToimiDatabase<TContext>(this WebApplicationBuilder builder, string connectionStringName)
    where TContext : DbContext
  {
    var connectionString = builder.RequireConnectionString(connectionStringName);
    builder.Services.AddDbContext<TContext>(options =>
      options.UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention());
    return builder;
  }

  /// <summary>
  /// Boot-time migration plus optional seeding, both inside the relational
  /// guard and sharing one scope. Call after Build(), before Run().
  /// </summary>
  public static async Task MigrateAndSeedAsync<TContext>(this WebApplication app, Func<IServiceProvider, Task>? seed = null)
    where TContext : DbContext
  {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TContext>();
    if (!db.Database.IsRelational())
    {
      return;
    }

    await db.Database.MigrateAsync();
    if (seed is not null)
    {
      await seed(scope.ServiceProvider);
    }
  }
}
