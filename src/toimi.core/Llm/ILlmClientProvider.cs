using Microsoft.Extensions.AI;

namespace Toimi.Core.Llm;

/// <summary>
/// The two model tiers. Fast is the default every agent turn runs on; Smart is
/// the more capable model harder work is delegated to. When no distinct smart
/// model is configured, Smart resolves to the fast model.
/// </summary>
public enum ModelTier
{
  Fast,
  Smart,
}

/// <summary>
/// The one place the "fast"/"smart" wire vocabulary is parsed. Callers choose
/// their unknown-value contract: write-time validators reject when TryParse
/// returns false; fire-time paths coerce to Fast via ParseOrFast (a stored
/// config must never make a trigger unrunnable).
/// </summary>
public static class ModelTiers
{
  public static bool TryParse(string? value, out ModelTier tier)
  {
    if (value is null || string.Equals(value, "fast", StringComparison.OrdinalIgnoreCase))
    {
      tier = ModelTier.Fast;
      return true;
    }

    if (string.Equals(value, "smart", StringComparison.OrdinalIgnoreCase))
    {
      tier = ModelTier.Smart;
      return true;
    }

    tier = ModelTier.Fast;
    return false;
  }

  public static ModelTier ParseOrFast(string? value)
  {
    _ = TryParse(value, out var tier);
    return tier;
  }
}

/// <summary>
/// A constructed LLM pipeline for one session or agent run. Client is the outermost
/// chat client to invoke; Notifier is the ToolCallNotifier the provider embedded
/// BELOW the function-invocation layer, so tool calls and results are observed
/// while the invocation loop runs. The layering is the provider's knowledge —
/// callers only consume the pair. Model is the concrete model name the session's
/// tier resolved to, for per-message usage attribution.
/// </summary>
public sealed record LlmSession(IChatClient Client, ToolCallNotifier Notifier, string Model);

/// <summary>Constructs the chat client + tool-call notifier for a session or agent run.</summary>
public interface ILlmClientProvider
{
  LlmSession Create(ModelTier tier = ModelTier.Fast);

  /// <summary>The concrete model name a tier resolves to (Smart falls back to the fast model when unconfigured).</summary>
  string ResolveModel(ModelTier tier);

  /// <summary>False when no smart model is configured (or it equals the fast model) — the Smart tier then buys isolation, not capability. Derived, so it can never disagree with ResolveModel.</summary>
  bool HasDistinctSmartModel => ResolveModel(ModelTier.Smart) != ResolveModel(ModelTier.Fast);
}
