using Microsoft.EntityFrameworkCore;
using Toimi.Core.Hosting;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Transport;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ruutu")
  ?? throw new InvalidOperationException("ConnectionStrings:Ruutu is required");

builder.Services.AddDbContext<RuutuDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<DisplayRepository>();
builder.Services.AddScoped<TemplateRepository>();
builder.Services.AddScoped<DisplayEventRepository>();
builder.Services.AddScoped<TemplateSeeder>();
builder.Services.AddScoped<toimi.tools.ruutu.Rendering.DbTemplateSource>();

builder.Services.AddSingleton<SseHub>();
builder.Services.AddScoped<ContentPushService>();

builder.Services.AddControllers();

builder.Services.AddToimiMcpServer("ruutu", typeof(Program).Assembly);

var app = builder.Build();

app.UseStaticFiles(new StaticFileOptions
{
  RequestPath = "/ruutu/static"
});

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<RuutuDbContext>();
  await dbContext.Database.MigrateAsync();
}

using (var seedScope = app.Services.CreateScope())
{
  var seeder = seedScope.ServiceProvider.GetRequiredService<TemplateSeeder>();
  await seeder.SeedAsync();
}

app.MapToimiMcp();
app.MapToimiReadiness<RuutuDbContext>();

app.Run();
