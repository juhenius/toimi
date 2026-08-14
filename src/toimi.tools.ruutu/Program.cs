using Toimi.Core.Hosting;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Transport;

var builder = WebApplication.CreateBuilder(args);

builder.AddToimiDatabase<RuutuDbContext>("Ruutu");

builder.Services.AddScoped<DisplayRepository>();
builder.Services.AddScoped<TemplateRepository>();
builder.Services.AddScoped<DisplayEventRepository>();
builder.Services.AddScoped<TemplateSeeder>();
builder.Services.AddScoped<toimi.tools.ruutu.Rendering.DbTemplateSource>();

builder.Services.AddSingleton<SseHub>();
builder.Services.AddScoped<ContentPushService>();
builder.Services.AddScoped<ActionForwarder>();
builder.Services.AddSingleton<ActionForwardChannel>();
builder.Services.AddHostedService<ActionForwardWorker>();
builder.Services.Configure<ActionOptions>(builder.Configuration.GetSection("Actions"));
builder.Services.AddHttpClient(ActionForwarder.HttpClientName,
  client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddControllers();

builder.AddToimiToolServer("ruutu", typeof(Program).Assembly);

var app = builder.Build();

app.UseStaticFiles(new StaticFileOptions
{
  RequestPath = "/ruutu/static"
});

app.MapControllers();

await app.MigrateAndSeedAsync<RuutuDbContext>(sp => sp.GetRequiredService<TemplateSeeder>().SeedAsync());

app.MapToimiMcp();
app.MapToimiReadiness<RuutuDbContext>();

app.Run();
