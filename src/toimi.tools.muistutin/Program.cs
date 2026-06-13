using Microsoft.EntityFrameworkCore;
using Toimi.Notifications;
using toimi.tools.muistutin.Data;
using toimi.tools.muistutin.Worker;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Muistutin")
  ?? throw new InvalidOperationException("ConnectionStrings:Muistutin is required");

builder.Services.AddDbContext<MuistutinDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<ReminderRepository>();

// Notifications
var ntfyOptions = builder.Configuration.GetSection("Ntfy").Get<NtfyOptions>() ?? new NtfyOptions();
builder.Services.AddSingleton(ntfyOptions);
builder.Services.AddSingleton(new NtfyClient(ntfyOptions));
builder.Services.AddHostedService<ReminderNotifier>();

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
  if (dbContext.Database.IsRelational())
  {
    await dbContext.Database.MigrateAsync();
  }
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());
toimi.tools.muistutin.Admin.AdminEndpoints.MapAdminEndpoints(app);

app.Run();
