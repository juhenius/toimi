using toimi.tools.selain.Browser;
using toimi.tools.selain.Endpoints;
using toimi.tools.selain.Streaming;
using Toimi.Core.Hosting;

if (args is ["install-browsers"])
{
  // Dev helper: install the Chromium build matching the Microsoft.Playwright package.
  Environment.Exit(Microsoft.Playwright.Program.Main(["install", "chromium"]));
}

var builder = WebApplication.CreateBuilder(args);

var selainOptions = builder.Configuration.GetSection("Selain").Get<SelainOptions>() ?? new SelainOptions();
builder.Services.AddSingleton(selainOptions);
builder.Services.AddSingleton<UrlPolicy>();
builder.Services.AddSingleton<TabManager>();
builder.Services.AddSingleton<BrowserHost>();
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddSingleton<ScreencastService>();

builder.Services.AddToimiMcpServer("selain", typeof(Program).Assembly);

var app = builder.Build();

app.UseWebSockets();
app.MapToimiMcp();
app.MapTabEndpoints();

app.Run();
