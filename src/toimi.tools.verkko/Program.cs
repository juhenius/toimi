using toimi.tools.verkko.Fetcher;
using Toimi.Notifications;

var builder = WebApplication.CreateBuilder(args);

// Web fetching
builder.Services.AddHttpClient<WebFetcher>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("Toimi/1.0 (personal assistant)");
});
builder.Services.AddSingleton<FetchCache>();

// Notifications (ntfy)
var ntfyOptions = builder.Configuration.GetSection("Ntfy").Get<NtfyOptions>() ?? new NtfyOptions();
builder.Services.AddSingleton(ntfyOptions);
builder.Services.AddSingleton(new NtfyClient(ntfyOptions));

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "verkko",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
