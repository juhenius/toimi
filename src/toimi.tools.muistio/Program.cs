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
    await dbContext.Database.MigrateAsync();

    var memoryRepo = scope.ServiceProvider.GetRequiredService<MemoryRepository>();
    await memoryRepo.EnsureCollectionAsync();
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

// REST API for memory management
var api = app.MapGroup("/api/memories");

api.MapGet("/", async (MemoryRepository repo, string? category, string? tags, int limit = 20, int offset = 0, bool includeExpired = false) =>
{
    var tagArray = string.IsNullOrWhiteSpace(tags)
        ? null
        : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var results = await repo.ListAsync(limit, offset, category, tagArray, includeExpired);
    return Results.Ok(results);
});

api.MapGet("/{id:guid}", async (MemoryRepository repo, Guid id) =>
{
    var entry = await repo.GetByIdAsync(id);
    return entry is not null ? Results.Ok(entry) : Results.NotFound();
});

api.MapPut("/{id:guid}", async (MemoryRepository repo, EmbeddingService embeddings, Guid id, MemoryUpdateRequest request) =>
{
    var existing = await repo.GetByIdAsync(id);
    if (existing is null) return Results.NotFound();

    float[]? embedding = null;
    if (request.Content is not null)
        embedding = await embeddings.GenerateEmbeddingAsync(request.Content);

    await repo.UpdateAsync(id, request.Content, request.Category, request.Tags,
        request.Confirmed, request.ExpiresAt, embedding);
    var updated = await repo.GetByIdAsync(id);
    return Results.Ok(updated);
});

api.MapDelete("/{id:guid}", async (MemoryRepository repo, Guid id) =>
{
    var deleted = await repo.DeleteAsync(id);
    return deleted ? Results.Ok() : Results.NotFound();
});

api.MapPost("/rebuild-index", async (MemoryRepository repo, EmbeddingService embeddings) =>
{
    var count = await repo.RebuildIndexAsync(embeddings);
    return Results.Ok(new { rebuilt = count });
});

app.Run();

sealed record MemoryUpdateRequest(
    string? Content = null,
    string? Category = null,
    string[]? Tags = null,
    bool? Confirmed = null,
    DateTimeOffset? ExpiresAt = null);
