using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Toimi.Core.Llm;
using Toimi.Web.Hubs;
using Xunit;

namespace Toimi.Web.Tests;

public class ToimiHubTests
{
  private sealed class ThrowingDbContext(DbContextOptions<ToimiDbContext> options) : ToimiDbContext(options)
  {
    public bool ThrowOnSave { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
      if (!ThrowOnSave)
      {
        return base.SaveChangesAsync(cancellationToken);
      }

      // In production, ToimiDbContext/ConversationRepository are DI-registered Scoped and
      // SignalR gives each hub method invocation its own DI scope, so a failed save's tracked
      // entities are discarded with that scope and never leak into the next call. This test
      // reuses one context across calls (to inspect final DB state), so clear the tracker here
      // to reproduce that same "failed write has zero durable effect" behavior.
      ChangeTracker.Clear();
      throw new InvalidOperationException("simulated database failure");
    }
  }

  private sealed class StreamingFakeChatClient : IChatClient
  {
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      yield return new ChatResponseUpdate(ChatRole.Assistant, "hello from fake");
      await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
      return null;
    }

    public void Dispose()
    {
    }
  }

  private sealed class FakeLlmProvider : ILlmClientProvider
  {
    public (IChatClient Client, ToolCallNotifier Notifier) Create()
    {
      var notifier = new ToolCallNotifier(new StreamingFakeChatClient());
      return (notifier, notifier);
    }
  }

  private sealed class RecordingClientProxy : ISingleClientProxy
  {
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
      Sent.Add((method, args));
      return Task.CompletedTask;
    }

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
    {
      throw new NotSupportedException();
    }
  }

  private sealed class FakeHubCallerClients : IHubCallerClients
  {
    public RecordingClientProxy CallerProxy { get; } = new();

    public ISingleClientProxy Caller => CallerProxy;

    public IClientProxy Others => CallerProxy;

    public IClientProxy All => CallerProxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)
    {
      return CallerProxy;
    }

    public ISingleClientProxy Client(string connectionId)
    {
      return CallerProxy;
    }

    public IClientProxy Clients(IReadOnlyList<string> connectionIds)
    {
      return CallerProxy;
    }

    public IClientProxy Group(string groupName)
    {
      return CallerProxy;
    }

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
    {
      return CallerProxy;
    }

    public IClientProxy Groups(IReadOnlyList<string> groupNames)
    {
      return CallerProxy;
    }

    public IClientProxy OthersInGroup(string groupName)
    {
      return CallerProxy;
    }

    public IClientProxy User(string userId)
    {
      return CallerProxy;
    }

    public IClientProxy Users(IReadOnlyList<string> userIds)
    {
      return CallerProxy;
    }

    IClientProxy IHubCallerClients<IClientProxy>.Caller => CallerProxy;

    IClientProxy IHubClients<IClientProxy>.Client(string connectionId)
    {
      return CallerProxy;
    }
  }

  private sealed class FakeHubCallerContext(string connectionId) : HubCallerContext
  {
    public override string ConnectionId => connectionId;

    public override string? UserIdentifier => null;

    public override ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
  }

  private static async Task<(ToimiHub Hub, FakeHubCallerClients Clients, ThrowingDbContext Db)> ConnectedHub()
  {
    var db = new ThrowingDbContext(new DbContextOptionsBuilder<ToimiDbContext>()
      .UseInMemoryDatabase($"hub-{Guid.NewGuid()}").Options);
    var hub = new ToimiHub(
      new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" } }, // empty McpServers: aggregator connects to nothing, fully offline
      new FakeLlmProvider(),
      new ConversationRepository(db),
      NullLogger<ToimiHub>.Instance)
    {
      Clients = new FakeHubCallerClients(),
      Context = new FakeHubCallerContext($"conn-{Guid.NewGuid()}"),
    };

    await hub.OnConnectedAsync();
    var clients = (FakeHubCallerClients)hub.Clients;
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Connected");
    return (hub, clients, db);
  }

  [Fact]
  public async Task SendMessage_streams_and_persists_user_and_assistant_messages()
  {
    var (hub, clients, db) = await ConnectedHub();

    await hub.SendMessage("hello");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ConversationCreated");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ReceiveToken" && (string?)s.Args[0] == "hello from fake");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    var conversation = Assert.Single(db.Conversations.ToList());
    var messages = db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToList();
    Assert.Equal(2, messages.Count);
    Assert.Contains(messages, m => m.Role == "user" && m.Content == "hello");
    Assert.Contains(messages, m => m.Role == "assistant" && m.Content == "hello from fake");

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Persistence_failure_sends_Error_and_keeps_session_consistent()
  {
    var (hub, clients, db) = await ConnectedHub();

    db.ThrowOnSave = true;
    // Must not throw a raw HubException out of the hub method.
    await hub.SendMessage("first try");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    // Recovery: the failed message must not haunt the in-memory session — the next
    // turn persists exactly its own user+assistant pair.
    db.ThrowOnSave = false;
    await hub.SendMessage("second try");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    var conversation = Assert.Single(db.Conversations.ToList());
    var messages = db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToList();
    Assert.Equal(2, messages.Count);
    Assert.DoesNotContain(messages, m => m.Content == "first try");

    await hub.OnDisconnectedAsync(null);
  }
}
