using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Toimi.Core.Admin;
using toimi.tools.taidot.Skills;
using Xunit;

namespace toimi.tools.taidot.Tests;

public class FakeSkillStore : ISkillStore
{
  public List<SkillEntry> Entries { get; } = [];
  public Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default)
    => Task.FromResult<IReadOnlyList<SkillEntry>>(Entries.ToList());
  public Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    => Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));
  public Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default)
  {
    var removed = Entries.RemoveAll(e => e.Id == id) > 0;
    return Task.FromResult(removed);
  }
  public Task UpsertPointAsync(Guid id, string name, string description, string instructions,
      string[] tags, float[] embedding, DateTimeOffset createdAt, CancellationToken ct = default)
  {
    Entries.RemoveAll(e => e.Id == id);
    Entries.Add(new SkillEntry(id, name, description, instructions, tags, createdAt, DateTimeOffset.UtcNow));
    return Task.CompletedTask;
  }
}

public class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
  public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
      IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
      CancellationToken cancellationToken = default)
  {
    var results = new GeneratedEmbeddings<Embedding<float>>(
        values.Select(_ => new Embedding<float>(new float[1536])).ToList());
    return Task.FromResult(results);
  }
  public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");
  public void Dispose() { GC.SuppressFinalize(this); }
  public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

public class TaidotTestFactory : WebApplicationFactory<Program>
{
  public FakeSkillStore Store { get; } = new();
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("OpenAI:ApiKey", "test");
    builder.ConfigureServices(services =>
    {
      // Replace ISkillStore with the fake so the seeder writes to it harmlessly.
      var storeReg = services.SingleOrDefault(d => d.ServiceType == typeof(ISkillStore));
      if (storeReg is not null) services.Remove(storeReg);
      services.AddSingleton<ISkillStore>(Store);

      // Remove SkillRepository (it would try to connect to Qdrant).
      var skillRepo = services.SingleOrDefault(d => d.ServiceType == typeof(SkillRepository));
      if (skillRepo is not null) services.Remove(skillRepo);

      // Remove the real QdrantClient and EnsureCollectionAsync will fail without it.
      var qdrant = services.SingleOrDefault(d => d.ServiceType == typeof(QdrantClient));
      if (qdrant is not null) services.Remove(qdrant);

      // Replace the real embedding generator with a fake.
      var emb = services.SingleOrDefault(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
      if (emb is not null) services.Remove(emb);
      services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
    });
  }
}

public class AdminEndpointsTests : IDisposable
{
  private readonly TaidotTestFactory _factory = new();

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task Summary_returns_skill_summaries()
  {
    // Trigger server startup (and seeder run) before manipulating the store.
    var client = _factory.CreateClient();
    // Clear seeder-populated entries so we have a known state.
    _factory.Store.Entries.Clear();
    _factory.Store.Entries.Add(new SkillEntry(
        Guid.NewGuid(), "How to brew coffee", "Steps for V60", "1. Boil water...",
        ["coffee"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("skill", item.Kind);
    Assert.Equal("How to brew coffee", item.Title);
  }
}
