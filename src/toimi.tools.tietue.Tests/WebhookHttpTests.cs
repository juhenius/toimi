using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Notifications;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using toimi.tools.tietue.Webhooks;
using Xunit;

namespace toimi.tools.tietue.Tests;

/// <summary>
/// End-to-end over the real host: routing (guid constraint), DI, the hosted
/// WebhookDispatcher, and the doorbell contract from POST to EntityEvent.
/// </summary>
public class WebhookHttpTests : IDisposable
{
  private readonly WebhookTestFactory _factory = new();

  [Fact]
  public async Task Post_fires_the_handler_through_the_background_dispatcher()
  {
    var trigger = await SeedWebhookTriggerAsync();
    var client = _factory.CreateClient();

    var response = await client.PostAsJsonAsync($"/hooks/{trigger.Id}/{trigger.Secret}?x=1", new { door = "front" });

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var accepted = await response.Content.ReadFromJsonAsync<WebhookEndpoints.WebhookAccepted>();
    Assert.False(string.IsNullOrEmpty(accepted!.Occurrence));

    var sent = await PollAsync(() => _factory.Notifier.Sent.Count > 0);
    Assert.True(sent, "expected the dispatcher to run the notify handler within the poll window");
    Assert.Equal("door: front", _factory.Notifier.Sent.Single().Message);
  }

  [Fact]
  public async Task Wrong_secret_is_404_with_no_body_difference()
  {
    var trigger = await SeedWebhookTriggerAsync();
    var client = _factory.CreateClient();

    var wrongSecret = await client.GetAsync($"/hooks/{trigger.Id}/nope");
    var unknownId = await client.GetAsync($"/hooks/{Guid.NewGuid()}/nope");

    Assert.Equal(HttpStatusCode.NotFound, wrongSecret.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);
    Assert.Equal(await unknownId.Content.ReadAsStringAsync(), await wrongSecret.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Non_guid_trigger_id_is_404_from_the_route_constraint()
  {
    var client = _factory.CreateClient();

    var response = await client.GetAsync("/hooks/not-a-guid/x");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  private async Task<Trigger> SeedWebhookTriggerAsync()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
    await new TypeRepository(db).DefineAsync("doorbell", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var entity = await new EntityRepository(db, new SchemaValidator()).CreateAsync("doorbell", JsonNode.Parse("""{"name":"front door"}"""), []);
    return await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      entity.Id, /*lang=json,strict*/ """{"webhook":{}}""", "notify",
      /*lang=json,strict*/ """{"messageTemplate":"door: {door}"}""", DateTimeOffset.UtcNow);
  }

  private static async Task<bool> PollAsync(Func<bool> condition)
  {
    for (var i = 0; i < 100; i++)
    {
      if (condition())
      {
        return true;
      }

      await Task.Delay(50);
    }

    return condition();
  }

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// TietueTestFactory plus the two swaps webhook flows need: a capturing notifier
/// (the real NtfyClient would call out) and an always-granted tick lock
/// (PostgresTickLock is relational-only and throws under the in-memory provider).
/// </summary>
public class WebhookTestFactory : TietueTestFactory
{
  public FakeNotifier Notifier { get; } = new();

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    base.ConfigureWebHost(builder);
    builder.ConfigureServices(services =>
    {
      foreach (var d in services.Where(d => d.ServiceType == typeof(INotifier) || d.ServiceType == typeof(ITickLock)).ToArray())
      {
        services.Remove(d);
      }

      services.AddSingleton<INotifier>(Notifier);
      services.AddSingleton<ITickLock, GrantedTickLock>();
    });
  }

  private sealed class GrantedTickLock : ITickLock
  {
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      return Task.FromResult<IAsyncDisposable?>(new NoopLease());
    }

    private sealed class NoopLease : IAsyncDisposable
    {
      public ValueTask DisposeAsync()
      {
        return ValueTask.CompletedTask;
      }
    }
  }
}
