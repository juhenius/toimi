using Microsoft.EntityFrameworkCore;
using Toimi.Core.Configuration;
using toimi.tools.ajastin.Data;
using toimi.tools.ajastin.Worker;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ajastin")
  ?? throw new InvalidOperationException("ConnectionStrings:Ajastin is required");

builder.Services.AddDbContext<AjastinDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<ScheduleRepository>();
builder.Services.AddHostedService<ScheduleWorker>();

builder.Services.AddSingleton(
  builder.Configuration.GetSection("Toimi").Get<ToimiConfiguration>()
    ?? throw new InvalidOperationException("Toimi configuration is required"));

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "ajastin",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<AjastinDbContext>();
  await dbContext.Database.MigrateAsync();
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

// REST API for run history
app.MapGet("/api/runs", async (AjastinDbContext db, int limit = 20) =>
{
  var runs = await db.ScheduleRuns
    .Include(r => r.Schedule)
    .OrderByDescending(r => r.StartedAt)
    .Take(Math.Clamp(limit, 1, 100))
    .Select(r => new
    {
      r.Id,
      r.ScheduleId,
      ScheduleName = r.Schedule.Name,
      StartedAt = r.StartedAt.ToString("o"),
      CompletedAt = r.CompletedAt != null ? r.CompletedAt.Value.ToString("o") : null,
      DurationMs = r.CompletedAt != null ? (long)(r.CompletedAt.Value - r.StartedAt).TotalMilliseconds : (long?)null,
      r.Response,
      r.ToolCallsJson,
      r.Success,
      r.Error,
    })
    .ToListAsync();

  return Results.Ok(runs);
});

app.Run();
