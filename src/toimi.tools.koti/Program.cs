using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Hosting;

var builder = WebApplication.CreateBuilder(args);

var haOptions = builder.RequireConfig<HomeAssistantOptions>("HomeAssistant");

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

builder.AddToimiToolServer("koti", typeof(Program).Assembly);

var app = builder.Build();

app.MapToimiMcp();

app.Run();
