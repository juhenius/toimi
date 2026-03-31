using toimi.tools.koti.HomeAssistant;

var builder = WebApplication.CreateBuilder(args);

var haOptions = builder.Configuration.GetSection("HomeAssistant").Get<HomeAssistantOptions>()
  ?? throw new InvalidOperationException("HomeAssistant configuration is required");

builder.Services.AddSingleton(haOptions);
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddSingleton(sp =>
{
  var factory = sp.GetRequiredService<IHttpClientFactory>();
  var http = factory.CreateClient(nameof(HomeAssistantClient));
  return new HomeAssistantClient(http, haOptions);
});

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "koti",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
