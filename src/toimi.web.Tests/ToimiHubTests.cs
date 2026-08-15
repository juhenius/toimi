using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
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
    public int SaveCalls { get; private set; }
    public int? FailOnSaveCall { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
      SaveCalls++;
      if (!ThrowOnSave && SaveCalls != FailOnSaveCall)
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
    public List<ChatResponseUpdate> Updates { get; set; } = [new(ChatRole.Assistant, "hello from fake")];
    public int? ThrowAfterEmit { get; set; }
    public List<List<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      Requests.Add([.. messages]);
      var emitted = 0;
      foreach (var update in Updates)
      {
        yield return update;
        emitted++;
        if (ThrowAfterEmit is { } n && emitted >= n)
        {
          throw new InvalidOperationException("simulated stream failure");
        }
      }

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

  private sealed class FakeSubtaskStore : ISubtaskStore
  {
    public Task<Guid> CreateAsync(Guid? parentConversationId, string title, CancellationToken ct = default)
    {
      return Task.FromResult(Guid.NewGuid());
    }

    public Task AddMessageAsync(
      Guid subtaskConversationId, string role, string content, string? toolCallsJson = null,
      int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
      string? model = null, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }
  }

  private sealed class FakeLlmProvider : ILlmClientProvider
  {
    public StreamingFakeChatClient ChatClient { get; } = new();

    public string ResolveModel(ModelTier tier)
    {
      return "fake-model";
    }

    public LlmSession Create(ModelTier tier = ModelTier.Fast)
    {
      var notifier = new ToolCallNotifier(ChatClient);
      return new LlmSession(notifier, notifier, ResolveModel(tier));
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

  private sealed class FakeHubCallerContext(string connectionId, string? conversationId = null) : HubCallerContext
  {
    public override string ConnectionId => connectionId;

    public override string? UserIdentifier => null;

    public override ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = BuildFeatures(conversationId);

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }

    private static FeatureCollection BuildFeatures(string? conversationId)
    {
      var features = new FeatureCollection();
      if (conversationId is not null)
      {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString($"?conversationId={conversationId}");
        // Microsoft.AspNetCore.Http.Features has no public HttpContextFeature in .NET 10;
        // SignalR's HubCallerContext.GetHttpContext() reads Http.Connections' own
        // IHttpContextFeature (verified empirically), so implement that one-property
        // interface inline.
        features.Set<IHttpContextFeature>(new FakeHttpContextFeature { HttpContext = http });
      }

      return features;
    }
  }

  private sealed class FakeHttpContextFeature : IHttpContextFeature
  {
    public HttpContext? HttpContext { get; set; }
  }

  private static async Task<(ToimiHub Hub, FakeHubCallerClients Clients, ThrowingDbContext Db, StreamingFakeChatClient Chat)> ConnectedHub(
    string? conversationId = null, ToimiConfiguration? config = null, ThrowingDbContext? existingDb = null)
  {
    var db = existingDb ?? new ThrowingDbContext(new DbContextOptionsBuilder<ToimiDbContext>()
      .UseInMemoryDatabase($"hub-{Guid.NewGuid()}").Options);
    var llm = new FakeLlmProvider();
    var hub = new ToimiHub(
      config ?? new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" } }, // empty McpServers: aggregator connects to nothing, fully offline
      llm,
      new ConversationRepository(db),
      new FakeSubtaskStore(),
      NullLogger<ToimiHub>.Instance)
    {
      Clients = new FakeHubCallerClients(),
      Context = new FakeHubCallerContext($"conn-{Guid.NewGuid()}", conversationId),
    };

    await hub.OnConnectedAsync();
    var clients = (FakeHubCallerClients)hub.Clients;
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Connected");
    return (hub, clients, db, llm.ChatClient);
  }

  [Fact]
  public async Task SendMessage_streams_and_persists_user_and_assistant_messages()
  {
    var (hub, clients, db, _) = await ConnectedHub();

    await hub.SendMessage("hello");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ConversationCreated");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ReceiveToken" && (string?)s.Args[0] == "hello from fake");
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    var conversation = Assert.Single(db.Conversations.ToList());
    var messages = db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToList();
    Assert.Equal(2, messages.Count);
    Assert.Contains(messages, m => m.Role == "user" && m.Content == "hello");
    Assert.Contains(messages, m => m.Role == "assistant" && m.Content == "hello from fake");
    Assert.Equal("hello", conversation.Title);

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Compaction_must_not_retrigger_auto_title_on_an_old_conversation()
  {
    var db = new ThrowingDbContext(new DbContextOptionsBuilder<ToimiDbContext>()
      .UseInMemoryDatabase($"hub-{Guid.NewGuid()}").Options);

    var conversation = new Conversation { Title = "original title" };
    db.Conversations.Add(conversation);

    // Stagger CreatedAt: InMemory ignores the OrderBy-relevant DB defaults, and the
    // GetByIdAsync include orders by CreatedAt, so the replay must see the user row
    // first and the twelve assistant rows after it, in order.
    var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
    db.ConversationMessages.Add(new ConversationMessage
    {
      ConversationId = conversation.Id,
      Role = "user",
      Content = "old question",
      CreatedAt = baseTime,
    });
    for (var i = 0; i < 12; i++)
    {
      db.ConversationMessages.Add(new ConversationMessage
      {
        ConversationId = conversation.Id,
        Role = "assistant",
        Content = $"old answer {i}",
        CreatedAt = baseTime.AddSeconds(i + 1),
      });
    }

    await db.SaveChangesAsync();

    var (hub, clients, _, _) = await ConnectedHub(
      conversationId: conversation.Id.ToString(),
      config: new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" }, MaxContextTokens = 1 },
      existingDb: db);

    await hub.SendMessage("brand new topic that must not become the title");

    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.Equal("original title", db.Conversations.Single().Title);

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Persistence_failure_sends_Error_and_keeps_session_consistent()
  {
    var (hub, clients, db, _) = await ConnectedHub();

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

  [Fact]
  public async Task Tool_call_events_reach_the_client_and_persist_with_pascal_case_keys()
  {
    var (hub, clients, db, chat) = await ConnectedHub();
    chat.Updates =
    [
      new(ChatRole.Assistant, [new FunctionCallContent("c1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
      new(ChatRole.Assistant, "found it"),
    ];

    await hub.SendMessage("find milk");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ToolCallStart" && (string?)s.Args[0] == "c1");

    // The persisted shape is the client's replay contract (useToimi.ts reads
    // CallId/Name/Arguments in PascalCase). A serializer-options change would
    // break conversation replay with no other signal — pin it.
    var assistant = db.ConversationMessages.Single(m => m.Role == "assistant");
    Assert.NotNull(assistant.ToolCallsJson);
    Assert.Contains("\"type\":\"call\"", assistant.ToolCallsJson);
    Assert.Contains("\"CallId\":\"c1\"", assistant.ToolCallsJson);
    Assert.Contains("search", assistant.ToolCallsJson);

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Mid_stream_failure_keeps_the_user_message_and_persists_no_assistant_row()
  {
    var (hub, clients, db, chat) = await ConnectedHub();
    chat.Updates = [new(ChatRole.Assistant, "partial ")];
    chat.ThrowAfterEmit = 1;

    await hub.SendMessage("doomed turn");

    // The user message persisted BEFORE the stream started; a mid-stream failure
    // must send Error, keep that row, and persist no assistant row.
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    var rows = db.ConversationMessages.ToList();
    var failedUser = Assert.Single(rows);
    Assert.Equal("user", failedUser.Role);
    Assert.Equal("doomed turn", failedUser.Content);

    // Recovery: the in-memory session must not carry a phantom assistant message.
    chat.ThrowAfterEmit = null;
    chat.Updates = [new(ChatRole.Assistant, "second answer")];
    await hub.SendMessage("second turn");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    rows = [.. db.ConversationMessages];
    Assert.Equal(3, rows.Count); // failed user + second turn's user/assistant pair
    Assert.Contains(rows, m => m.Role == "assistant" && m.Content == "second answer");

    // The user message of the failed turn was persisted and must have STAYED in the
    // in-memory context — a blind rollback that strips it would desync session from DB.
    Assert.Contains(chat.Requests[^1], m => (m.Text ?? "").Contains("doomed turn"));

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Assistant_persist_failure_rolls_the_appended_message_back_out_of_context()
  {
    var (hub, clients, db, chat) = await ConnectedHub();
    chat.Updates = [new(ChatRole.Assistant, "partial answer")];
    // A fresh conversation's first SendMessage does 3 saves: 1 = CreateAsync,
    // 2 = user AddMessageAsync, 3 = assistant AddMessageAsync. Failing on save 3
    // means the stream completes and the assistant message IS appended to
    // session.Messages before the persist fails — this is what exercises the
    // assistantAppended && !assistantPersisted rollback guard.
    db.FailOnSaveCall = 3;

    await hub.SendMessage("first");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    var rows = db.ConversationMessages.ToList();
    var failedUser = Assert.Single(rows);
    Assert.Equal("user", failedUser.Role);

    // Recovery: the in-memory session must not carry the phantom assistant message
    // that was appended (for the streamed response) but never persisted.
    db.FailOnSaveCall = null;
    chat.Updates = [new(ChatRole.Assistant, "second answer")];
    await hub.SendMessage("second");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");

    // The key assertion: if the guard didn't strip the un-persisted assistant
    // message out of session.Messages, it would ride along as context on the
    // next request.
    var lastRequest = chat.Requests[^1];
    Assert.DoesNotContain(lastRequest, m => (m.Text ?? "").Contains("partial answer"));

    rows = [.. db.ConversationMessages];
    Assert.Equal(3, rows.Count); // failed user + second turn's user/assistant pair
    Assert.Contains(rows, m => m.Role == "assistant" && m.Content == "second answer");

    await hub.OnDisconnectedAsync(null);
  }
}
