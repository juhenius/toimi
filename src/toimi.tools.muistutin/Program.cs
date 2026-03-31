using Microsoft.EntityFrameworkCore;
using toimi.tools.muistutin.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Muistutin")
  ?? throw new InvalidOperationException("ConnectionStrings:Muistutin is required");

builder.Services.AddDbContext<MuistutinDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<ReminderRepository>();

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "muistutin",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
  await dbContext.Database.MigrateAsync();
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
