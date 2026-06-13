using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using toimi.tools.muistio.Data;
using toimi.tools.muistio.Memory;

var builder = WebApplication.CreateBuilder(args);

// PostgreSQL
var connectionString = builder.Configuration.GetConnectionString("Muistio")
  ?? throw new InvalidOperationException("ConnectionStrings:Muistio is required");

builder.Services.AddDbContext<MuistioDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

// Qdrant
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334", System.Globalization.CultureInfo.InvariantCulture);
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddScoped<MemoryRepository>();

// OpenAI embeddings
var openAiApiKey = builder.Configuration["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey is required");
var embeddingModel = builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";

var openAiClient = new OpenAIClient(openAiApiKey);
var embeddingClient = openAiClient.GetEmbeddingClient(embeddingModel);
builder.Services.AddSingleton(embeddingClient.AsIEmbeddingGenerator());
builder.Services.AddSingleton<EmbeddingService>();

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "muistio",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
  if (dbContext.Database.IsRelational())
  {
    await dbContext.Database.MigrateAsync();

    var memoryRepo = scope.ServiceProvider.GetRequiredService<MemoryRepository>();
    await memoryRepo.EnsureCollectionAsync();
  }
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());
toimi.tools.muistio.Admin.AdminEndpoints.MapAdminEndpoints(app);

app.Run();
