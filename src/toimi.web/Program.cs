using Microsoft.EntityFrameworkCore;
using Toimi.Core.Configuration;
using Toimi.Core.Data;

var builder = WebApplication.CreateBuilder(args);

var toimiConfig = builder.Configuration.GetSection("Toimi").Get<ToimiConfiguration>();

if (toimiConfig is null)
{
  Console.Error.WriteLine("Error: Toimi configuration section is missing from appsettings.json.");
  return 1;
}

if (string.IsNullOrEmpty(toimiConfig.OpenAI.ApiKey))
{
  Console.Error.WriteLine("Error: Toimi:OpenAI:ApiKey is required. Set it in appsettings.json or TOIMI__OPENAI__APIKEY env var.");
  return 1;
}

builder.Services.AddSignalR();
builder.Services.AddSingleton(toimiConfig);
builder.Services.AddHttpClient("ajastin", client =>
{
  client.BaseAddress = new Uri(builder.Configuration["AjastinApiUrl"]
    ?? "http://toimi-tools-ajastin.apps.svc.cluster.local");
});

var toimiConnectionString = builder.Configuration.GetConnectionString("Toimi")
  ?? throw new InvalidOperationException("ConnectionStrings:Toimi is required");

builder.Services.AddDbContext<ToimiDbContext>(options =>
  options.UseNpgsql(toimiConnectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<ConversationRepository>();

if (builder.Environment.IsDevelopment())
{
  builder.Services.AddCors(options =>
  {
    options.AddDefaultPolicy(policy =>
    {
      policy.WithOrigins("http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
  });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<ToimiDbContext>();
  await dbContext.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
  app.UseCors();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok());

app.MapGet("/api/activity", async (IHttpClientFactory httpFactory, int limit = 20) =>
{
  var client = httpFactory.CreateClient("ajastin");
  try
  {
    var response = await client.GetAsync($"/api/runs?limit={Math.Clamp(limit, 1, 100)}");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
  }
  catch (Exception ex)
  {
    return Results.Problem($"Failed to fetch activity: {ex.Message}");
  }
});

app.MapHub<Toimi.Web.Hubs.ToimiHub>("/toimihub");
app.MapFallbackToFile("index.html");

app.Run();
return 0;
