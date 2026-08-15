using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Toimi.Core.Configuration;
using Toimi.Core.Llm;

namespace Toimi.Core;

/// <summary>
/// Host-provided persistence for delegated subtasks. A subtask that runs where
/// conversations persist (the web host) is recorded as its own conversation,
/// marked and linked to the parent; hosts without conversation storage (tietue's
/// scheduled runs) pass no store and keep their existing occurrence-event record.
/// Store failures never fail the subtask — recording is best-effort.
/// </summary>
public interface ISubtaskStore
{
  /// <summary>Creates the subtask conversation row; returns its id (used as the parent link for nested delegation).</summary>
  Task<Guid> CreateAsync(Guid? parentConversationId, string title, CancellationToken ct = default);

  Task AddMessageAsync(
    Guid subtaskConversationId, string role, string content, string? toolCallsJson = null,
    int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
    string? model = null, CancellationToken ct = default);
}

/// <summary>
/// Delegation wiring for one agent session: where subtask transcripts go, how the
/// parent conversation id is resolved at delegation time (it may not exist yet when
/// the session starts — web conversations are lazy), and how deep this session
/// already is in the delegation chain.
/// </summary>
public sealed record SubtaskOptions(
  ISubtaskStore? Store = null,
  Func<Guid?>? ParentConversationId = null,
  int Depth = 0);

/// <summary>
/// The delegate tool: hands a self-contained task to a subtask — a fresh
/// <see cref="ToimiAgent"/> session on the requested tier that sees only the brief,
/// runs with full tool access, and returns its final text as the tool result.
/// Depth-capped at 2 (a subtask may delegate once more; its subtask may not), and
/// the result is truncated so a subtask cannot defeat the context-isolation purpose
/// by returning its whole haul. Never throws: failures come back as readable text.
/// </summary>
public static class Delegation
{
  /// <summary>A subtask may delegate once more; a subtask's subtask may not.</summary>
  public const int MaxDepth = 2;

  /// <summary>Result cap (~8k tokens' worth of characters).</summary>
  public const int MaxResultChars = 32_000;

  private const int TitleMaxChars = 50;

  public static AIFunction CreateTool(
    ToimiConfiguration config, ILlmClientProvider llmProvider, SubtaskOptions options, ILogger? logger)
  {
    return AIFunctionFactory.Create(
      async (
        [Description("The complete, self-contained task brief. The subtask cannot see this conversation — include every fact, id, URL, and constraint it needs.")]
        string task,
        [Description("Which model runs the subtask: \"fast\" (default) or \"smart\".")]
        string? model = null,
        CancellationToken cancellationToken = default) =>
          await RunSubtaskAsync(config, llmProvider, options, task, model, logger, cancellationToken),
      name: "delegate",
      description: BuildDescription(llmProvider.HasDistinctSmartModel));
  }

  private static string BuildDescription(bool hasDistinctSmartModel)
  {
    var description =
      "Delegate a self-contained task to a subtask: a fresh agent session that runs the task with " +
      "full tool access and returns its final answer as this tool's result. The subtask sees ONLY " +
      "the task text — none of this conversation. Three uses: " +
      "escalation (model=\"smart\" when the task needs more capability than you have); " +
      "cheap chores (model=\"fast\" for mechanical work); " +
      "context isolation (any tier, when the raw material would bloat this conversation — e.g. " +
      "fetching a large page to extract one fact: the bulk stays in the subtask, only the answer returns). " +
      "When relaying the subtask's answer, quote its substance faithfully rather than re-deriving it.";

    return hasDistinctSmartModel
      ? description
      : description + " Note: no separate smart model is configured — \"smart\" currently runs the " +
        "same model as \"fast\", so delegate for isolation, not for extra capability.";
  }

  private static async Task<string> RunSubtaskAsync(
    ToimiConfiguration config, ILlmClientProvider llmProvider, SubtaskOptions options,
    string task, string? model, ILogger? logger, CancellationToken ct)
  {
    var tier = ModelTiers.ParseOrFast(model);

    // Same wall-clock cap as a headless agent run: a hung subtask must not stall
    // the parent turn indefinitely.
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.AgentRunTimeoutSeconds));
    var token = timeoutCts.Token;

    Guid? subtaskId = null;
    if (options.Store is { } store)
    {
      // T is Guid? (not Guid) so a store failure yields null, never Guid.Empty.
      subtaskId = await TryStoreAsync<Guid?>(logger, async () =>
      {
        var id = await store.CreateAsync(options.ParentConversationId?.Invoke(), TitleFrom(task), token);
        await store.AddMessageAsync(id, "user", task, ct: token);
        return id;
      });
    }

    try
    {
      var nested = options with { ParentConversationId = () => subtaskId, Depth = options.Depth + 1 };
      await using var agent = await ToimiAgent.StartAsync(config, llmProvider, tier, nested, logger: logger, ct: token);
      var turn = await agent.RunTurnAsync(task, token);

      if (options.Store is { } sink && subtaskId is Guid sid)
      {
        _ = await TryStoreAsync<object?>(logger, async () =>
        {
          await sink.AddMessageAsync(
            sid, "assistant", turn.ResponseText, turn.ToolCallsJson,
            turn.PromptTokens, turn.CompletionTokens, turn.TotalTokens, turn.Model, CancellationToken.None);
          return null;
        });
      }

      return Truncate(turn.ResponseText);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return $"Subtask timed out after {config.AgentRunTimeoutSeconds}s. Its partial work is lost; retry with a narrower task.";
    }
    catch (OperationCanceledException)
    {
      // Genuine parent cancellation: propagate so the parent turn unwinds normally.
      throw;
    }
    catch (Exception ex)
    {
      return $"Subtask failed: {ex.Message}";
    }
  }

  private static string Truncate(string result)
  {
    return result.Length <= MaxResultChars
      ? result
      : result[..MaxResultChars] + $"\n\n[subtask result truncated at {MaxResultChars} characters]";
  }

  private static string TitleFrom(string task)
  {
    return task.Length > TitleMaxChars ? task[..TitleMaxChars] : task;
  }

  private static async Task<T?> TryStoreAsync<T>(ILogger? logger, Func<Task<T>> action)
  {
    try
    {
      return await action();
    }
    catch (Exception ex)
    {
      logger?.LogWarning(ex, "Subtask persistence failed; continuing without a transcript record.");
      return default;
    }
  }
}
