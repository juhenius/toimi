using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Data.Entities;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Rendering;
using toimi.tools.ruutu.Transport;
using Xunit;

namespace toimi.tools.ruutu.Tests;

public class ActionForwardingTests
{
  [Fact]
  public async Task Matched_event_forwards_params_and_records_ok()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    var result = await world.PostEventAsync("check", "step-3", false);

    Assert.IsType<OkResult>(result);
    Assert.NotNull(world.Handler.LastRequest);
    Assert.Equal("http://tietue.local/hooks/t1/s1", world.Handler.LastRequest!.RequestUri!.ToString());

    var body = JsonDocument.Parse(world.Handler.LastBody!).RootElement;
    Assert.Equal("check", body.GetProperty("type").GetString());
    Assert.Equal("step-3", body.GetProperty("target").GetString());
    Assert.False(body.GetProperty("value").GetBoolean());
    Assert.Equal("wall", body.GetProperty("display").GetString());

    Assert.Equal("ok", world.SingleEvent().ForwardOutcome);
  }

  [Fact]
  public async Task Unmatched_event_forwards_nothing()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    var result = await world.PostEventAsync("tap", "elsewhere", null);

    Assert.IsType<OkResult>(result);
    Assert.Null(world.Handler.LastRequest);
    Assert.Null(world.SingleEvent().ForwardOutcome);
  }

  [Fact]
  public async Task No_actions_map_forwards_nothing()
  {
    var world = await World.CreateAsync(
      currentActions: null,
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    await world.PostEventAsync("check", "step-3", true);

    Assert.Null(world.Handler.LastRequest);
    Assert.Null(world.SingleEvent().ForwardOutcome);
  }

  [Fact]
  public async Task Failed_forward_records_error_and_pushes_notification_overlay()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
    var channel = world.Hub.Subscribe("wall");

    await world.PostEventAsync("check", "step-3", true);

    Assert.Equal("error: 404", world.SingleEvent().ForwardOutcome);

    Assert.True(channel.Reader.TryRead(out var sse));
    Assert.Equal("overlay", sse.EventType);
    Assert.Contains("Action failed", sse.JsonPayload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Connection_error_records_error_outcome()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => throw new HttpRequestException("connection refused"));

    await world.PostEventAsync("check", "step-3", true);

    Assert.Equal("error: unreachable", world.SingleEvent().ForwardOutcome);
  }

  [Fact]
  public async Task Overlay_dismissal_never_forwards_even_when_dismiss_is_wired()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "dismiss": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    await world.PostEventAsync("dismiss", "overlay", null);

    Assert.Null(world.Handler.LastRequest);
    Assert.Null(world.SingleEvent().ForwardOutcome);
  }

  [Fact]
  public async Task Repeated_failures_do_not_stack_identical_overlays()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
    var channel = world.Hub.Subscribe("wall");

    await world.PostEventAsync("check", "step-1", true);
    await world.PostEventAsync("check", "step-2", true);

    var overlayEvents = 0;
    while (channel.Reader.TryRead(out var sse))
    {
      if (sse.EventType == "overlay")
      {
        overlayEvents++;
      }
    }

    Assert.Equal(1, overlayEvents);
    var display = await world.Displays.GetAsync("wall");
    Assert.Single(OverlayStack.Parse(display!.OverlayStack));
  }

  [Fact]
  public async Task Public_hook_url_is_rewritten_to_the_internal_service()
  {
    var world = await World.CreateAsync(
      currentActions: /*lang=json,strict*/ """{ "check": "https://toimi.example.com/hooks/t1/s1" }""",
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted),
      options: new ActionOptions
      {
        PublicHookHost = "toimi.example.com",
        InternalHookBase = "http://toimi-tools-tietue.apps.svc.cluster.local"
      });

    await world.PostEventAsync("check", "step-3", true);

    Assert.Equal(
      "http://toimi-tools-tietue.apps.svc.cluster.local/hooks/t1/s1",
      world.Handler.LastRequest!.RequestUri!.ToString());
    Assert.Equal("ok", world.SingleEvent().ForwardOutcome);
  }

  [Theory]
  [InlineData("https://toimi.example.com/hooks/t1/s1", "http://tietue.local/hooks/t1/s1")]
  [InlineData("https://TOIMI.example.COM/hooks/t1/s1?x=1", "http://tietue.local/hooks/t1/s1?x=1")]
  [InlineData("https://other.example.com/hooks/t1/s1", "https://other.example.com/hooks/t1/s1")]
  [InlineData("https://toimi.example.com/other/t1/s1", "https://toimi.example.com/other/t1/s1")]
  [InlineData("not a url", "not a url")]
  public void RewriteForCluster_rewrites_only_public_hook_urls(string url, string expected)
  {
    Assert.Equal(expected,
      ActionForwarder.RewriteForCluster(url, "toimi.example.com", "http://tietue.local/"));
  }

  [Fact]
  public void RewriteForCluster_is_a_noop_when_unconfigured()
  {
    const string url = "https://toimi.example.com/hooks/t1/s1";
    Assert.Equal(url, ActionForwarder.RewriteForCluster(url, null, null));
    Assert.Equal(url, ActionForwarder.RewriteForCluster(url, "toimi.example.com", null));
    Assert.Equal(url, ActionForwarder.RewriteForCluster(url, null, "http://tietue.local"));
  }

  [Fact]
  public async Task Scene_push_replaces_actions_and_clear_nulls_them()
  {
    var world = await World.CreateAsync(
      currentActions: null,
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    var data = JsonDocument.Parse(/*lang=json,strict*/ """{ "body": "hi" }""").RootElement;
    var actions = /*lang=json,strict*/ """{ "check": "http://tietue.local/hooks/t1/s1" }""";

    await world.Pusher.ShowSceneAsync("wall", "message", data, actions);
    Assert.Equal(actions, (await world.Displays.GetAsync("wall"))!.CurrentActions);

    await world.Pusher.ShowSceneAsync("wall", "message", data);
    Assert.Null((await world.Displays.GetAsync("wall"))!.CurrentActions);

    await world.Pusher.ShowSceneAsync("wall", "message", data, actions);
    await world.Pusher.ClearAsync("wall");
    Assert.Null((await world.Displays.GetAsync("wall"))!.CurrentActions);
  }

  [Fact]
  public async Task Scene_push_with_invalid_actions_is_rejected_before_saving()
  {
    var world = await World.CreateAsync(
      currentActions: null,
      respond: _ => new HttpResponseMessage(HttpStatusCode.Accepted));

    var data = JsonDocument.Parse(/*lang=json,strict*/ """{ "body": "hi" }""").RootElement;
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      world.Pusher.ShowSceneAsync("wall", "message", data, /*lang=json,strict*/ """{ "check": "not a url" }"""));

    var display = await world.Displays.GetAsync("wall");
    Assert.Null(display!.CurrentTemplate);
    Assert.Null(display.CurrentActions);
  }

  private sealed class World
  {
    public required RuutuDbContext Db { get; init; }
    public required DisplayRepository Displays { get; init; }
    public required ContentPushService Pusher { get; init; }
    public required ActionForwarder Forwarder { get; init; }
    public required DisplayApiController Controller { get; init; }
    public required FakeHandler Handler { get; init; }
    public required SseHub Hub { get; init; }
    public required ActionForwardChannel Forwards { get; init; }

    public static async Task<World> CreateAsync(
      string? currentActions, Func<HttpRequestMessage, HttpResponseMessage> respond,
      ActionOptions? options = null)
    {
      var db = TestDb.New();
      var templates = new TemplateRepository(db);
      await new TemplateSeeder(templates, NullLogger<TemplateSeeder>.Instance).SeedAsync();

      var displays = new DisplayRepository(db);
      var display = await displays.RegisterAsync("wall", "modern");
      display.CurrentActions = currentActions;
      await db.SaveChangesAsync();

      var events = new DisplayEventRepository(db);
      var hub = new SseHub();
      var pusher = new ContentPushService(
        displays, templates, events, db, hub, NullLogger<ContentPushService>.Instance);
      var handler = new FakeHandler(respond);
      var forwarder = new ActionForwarder(
        new FakeHttpClientFactory(handler), pusher, db,
        Microsoft.Extensions.Options.Options.Create(options ?? new ActionOptions()),
        NullLogger<ActionForwarder>.Instance);
      var controller = new DisplayApiController(
        displays, new FakeWebHostEnvironment(), NullLogger<DisplayApiController>.Instance);

      return new World
      {
        Db = db,
        Displays = displays,
        Pusher = pusher,
        Forwarder = forwarder,
        Controller = controller,
        Handler = handler,
        Hub = hub,
        Forwards = new ActionForwardChannel()
      };
    }

    /// <summary>Posts the event, then drains the forward queue inline (standing in for ActionForwardWorker).</summary>
    public async Task<IActionResult> PostEventAsync(string type, string? target, object? value)
    {
      var result = await Controller.PostEvent(
        "wall", new DisplayApiController.EventRequest(type, target, value),
        new DisplayEventRepository(Db), Pusher, Forwards, CancellationToken.None);
      while (Forwards.Reader.TryRead(out var forward))
      {
        await Forwarder.ForwardAsync(forward);
      }

      return result;
    }

    public DisplayEvent SingleEvent()
    {
      return Db.DisplayEvents.Single();
    }
  }

  private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
    {
      LastRequest = request;
      LastBody = request.Content is null ? null
        : await request.Content.ReadAsStringAsync(cancellationToken);
      return respond(request);
    }
  }

  private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new HttpClient(handler, disposeHandler: false);
    }
  }

  private sealed class FakeWebHostEnvironment : IWebHostEnvironment
  {
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = null!;
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "toimi.tools.ruutu.Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
  }
}
