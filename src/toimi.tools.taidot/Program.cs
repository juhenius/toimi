using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using toimi.tools.taidot.Skills;

var builder = WebApplication.CreateBuilder(args);

// Qdrant
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334", System.Globalization.CultureInfo.InvariantCulture);
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<SkillRepository>();

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
      Name = "taidot",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

var skillRepo = app.Services.GetRequiredService<SkillRepository>();
await skillRepo.EnsureCollectionAsync();

var seeder = new SkillSeeder(skillRepo, app.Services.GetRequiredService<EmbeddingService>());
await seeder.SeedAsync();

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();
