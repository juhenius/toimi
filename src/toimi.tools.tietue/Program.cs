using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using Toimi.Core.Hosting;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.AddToimiDatabase<TietueDbContext>("Tietue");

builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddScoped<TypeRepository>();
builder.Services.AddScoped<EntityRepository>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.TriggerRepository>();
builder.Services.AddScoped<toimi.tools.tietue.Provisioning.TriggerProvisioner>();
builder.Services.AddScoped<toimi.tools.tietue.Provisioning.ExpiryReconciler>();

var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334", System.Globalization.CultureInfo.InvariantCulture);
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));

var openAiApiKey = builder.RequireValue("OpenAI:ApiKey");
var embeddingModel = builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
var openAiClient = new OpenAIClient(openAiApiKey);
var embeddingClient = openAiClient.GetEmbeddingClient(embeddingModel);
builder.Services.AddSingleton(embeddingClient.AsIEmbeddingGenerator());

builder.Services.AddSingleton<ISemanticIndex, QdrantSemanticIndex>();
builder.Services.AddScoped<SemanticSearch>();
builder.Services.AddScoped<SemanticOutbox>();

// Registration order = pipeline order: EntityRepository runs these IEntityBehaviors in the order registered.
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.SemanticIndexBehavior>();
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.TriggerProvisioningBehavior>();
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.ExpiryBehavior>();
builder.Services.AddScoped<toimi.tools.tietue.Seed.TypeSeeder>();
builder.Services.AddScoped<toimi.tools.tietue.Seed.SkillSeeder>();

var ntfyOptions = builder.Configuration.GetSection("Ntfy").Get<Toimi.Notifications.NtfyOptions>() ?? new Toimi.Notifications.NtfyOptions();
builder.Services.AddSingleton<Toimi.Notifications.INotifier>(new Toimi.Notifications.NtfyClient(ntfyOptions));

builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.NotifyHandler>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.SetFieldHandler>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.DeleteHandler>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.HandlerRegistry>();
builder.Services.AddScoped<toimi.tools.tietue.Events.EntityEventStore>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.ITickLock, toimi.tools.tietue.Scheduling.PostgresTickLock>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.OccurrenceRunner>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.SchedulerTick>();
builder.Services.AddHostedService<toimi.tools.tietue.Scheduling.TriggerWorker>();
builder.Services.AddHostedService<OutboxWorker>();

builder.Services.AddSingleton(builder.RequireConfig<Toimi.Core.Configuration.ToimiConfiguration>("Toimi"));
builder.Services.AddSingleton<Toimi.Core.Llm.ILlmClientProvider, Toimi.Core.Llm.OpenAiLlmClientProvider>();
builder.Services.AddSingleton<toimi.tools.tietue.Agents.IAgentRunner, toimi.tools.tietue.Agents.AgentRunner>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.MessageHandler>();

builder.Services.AddSingleton(
  builder.Configuration.GetSection("Scripts").Get<toimi.tools.tietue.Scripts.ScriptOptions>() ?? new toimi.tools.tietue.Scripts.ScriptOptions());
builder.Services.AddSingleton(sp =>
  toimi.tools.tietue.Scripts.ScriptBudget.From(sp.GetRequiredService<toimi.tools.tietue.Scripts.ScriptOptions>()));
builder.Services.AddSingleton(
  builder.Configuration.GetSection("Suoritin").Get<toimi.tools.tietue.Scripts.SuoritinOptions>() ?? new toimi.tools.tietue.Scripts.SuoritinOptions());
builder.Services.AddHttpClient(toimi.tools.tietue.Scripts.SuoritinClient.HttpClientName, (sp, client) =>
{
  client.BaseAddress = new Uri(sp.GetRequiredService<toimi.tools.tietue.Scripts.SuoritinOptions>().BaseUrl);
  client.Timeout = sp.GetRequiredService<toimi.tools.tietue.Scripts.ScriptBudget>().HttpTimeout;
  // Suoritin output is untrusted; an oversize body surfaces as an HttpRequestException.
  client.MaxResponseContentBufferSize = 1024 * 1024;
});
builder.Services.AddSingleton<toimi.tools.tietue.Scripts.ISuoritinClient, toimi.tools.tietue.Scripts.SuoritinClient>();
builder.Services.AddSingleton<toimi.tools.tietue.Scripts.RunTokenStore>();
builder.Services.AddSingleton<toimi.tools.tietue.Scripts.ILlmExtractor, toimi.tools.tietue.Scripts.LlmExtractor>();
builder.Services.AddScoped<toimi.tools.tietue.Agents.IMcpInvoker, toimi.tools.tietue.Agents.McpInvoker>();
builder.Services.AddScoped<toimi.tools.tietue.Scripts.ScriptEffectApplier>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.ScriptHandler>();

builder.Services.AddSingleton(
  builder.Configuration.GetSection("Webhooks").Get<toimi.tools.tietue.Webhooks.WebhookOptions>() ?? new toimi.tools.tietue.Webhooks.WebhookOptions());
builder.Services.AddSingleton<toimi.tools.tietue.Webhooks.WebhookRateLimiter>();
builder.Services.AddSingleton<toimi.tools.tietue.Webhooks.WebhookDispatchChannel>();
builder.Services.AddHostedService<toimi.tools.tietue.Webhooks.WebhookDispatcher>();

builder.AddToimiToolServer("tietue", typeof(Program).Assembly);

var app = builder.Build();

// A misconfigured Scripts:TimeoutSeconds must fail here at boot, not at the
// first trigger fire: the singleton is a lazy factory, so resolve it once now
// (ScriptBudgetTests documents the fail-fast contract).
_ = app.Services.GetRequiredService<toimi.tools.tietue.Scripts.ScriptBudget>();

var seededCollections = new[] { "memory", "skill" };
await app.MigrateAndSeedAsync<TietueDbContext>(async sp =>
{
  await sp.GetRequiredService<toimi.tools.tietue.Seed.TypeSeeder>().SeedAsync();
  await sp.GetRequiredService<toimi.tools.tietue.Seed.SkillSeeder>().SeedAsync();

  var index = sp.GetRequiredService<ISemanticIndex>();
  foreach (var name in seededCollections)
  {
    await index.EnsureCollectionAsync(name);
  }
});

app.MapToimiMcp();
app.MapToimiReadiness<TietueDbContext>();
toimi.tools.tietue.Admin.AdminEndpoints.MapAdminEndpoints(app);
toimi.tools.tietue.Scripts.ExtractEndpoints.MapExtractEndpoints(app);
toimi.tools.tietue.Webhooks.WebhookEndpoints.MapWebhookEndpoints(app);

app.Run();
