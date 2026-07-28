using Microsoft.EntityFrameworkCore;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Toimi.Web.Admin;

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
builder.Services.AddSingleton<Toimi.Core.Llm.ILlmClientProvider, Toimi.Core.Llm.OpenAiLlmClientProvider>();

var adminToolsOptions = builder.Configuration.GetSection("Toimi:Admin").Get<AdminToolsOptions>()
  ?? new AdminToolsOptions();
builder.Services.AddSingleton(adminToolsOptions);

foreach (var tool in adminToolsOptions.Tools)
{
  builder.Services.AddHttpClient($"admin-{tool}", client =>
  {
    var overrideUrl = builder.Configuration[$"Toimi:Admin:Urls:{tool}"];
    client.BaseAddress = new Uri(
      overrideUrl ?? $"http://toimi-tools-{tool}.apps.svc.cluster.local");
  });
}

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
app.UseAdminPathGuard();
app.MapGet("/health", () => Results.Ok());


AdminEndpoints.MapAdminEndpoints(app);
app.MapHub<Toimi.Web.Hubs.ToimiHub>("/toimihub");
app.MapFallbackToFile("index.html");

app.Run();
return 0;
