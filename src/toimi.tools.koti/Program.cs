using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Hosting;

var builder = WebApplication.CreateBuilder(args);

var haOptions = builder.Configuration.GetSection("HomeAssistant").Get<HomeAssistantOptions>()
  ?? throw new InvalidOperationException("HomeAssistant configuration is required");

builder.Services.AddSingleton(haOptions);
builder.Services.AddHttpClient<HomeAssistantClient>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton(sp =>
{
  var factory = sp.GetRequiredService<IHttpClientFactory>();
  var http = factory.CreateClient(nameof(HomeAssistantClient));
  return new HomeAssistantClient(http, haOptions);
});

builder.Services.AddToimiMcpServer("koti", typeof(Program).Assembly);

var app = builder.Build();

app.MapToimiMcp();

app.Run();
