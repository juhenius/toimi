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

app.Run();
