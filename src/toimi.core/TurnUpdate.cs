namespace Toimi.Core;

/// <summary>Streamed progress of one conversation turn, yielded by <see cref="ToimiAgent.SendAsync"/>.</summary>
public abstract record TurnUpdate;

/// <summary>A chunk of assistant response text.</summary>
public sealed record TokenUpdate(string Text) : TurnUpdate;

/// <summary>A tool invocation started (Arguments is serialized JSON).</summary>
public sealed record ToolCallUpdate(string CallId, string Name, string Arguments) : TurnUpdate;

/// <summary>A tool invocation finished.</summary>
public sealed record ToolResultUpdate(string CallId, string Result, long DurationMs) : TurnUpdate;

/// <summary>
/// Terminal update of a successful turn — everything a host needs to persist.
/// ToolCallsJson is the unified wire shape (see <see cref="ToolEventJson"/>).
/// Token counts are the provider's real usage when reported, otherwise the same
/// chars-based estimates the web host has always persisted. Model is the concrete
/// model name that served the turn, for per-message usage attribution.
/// </summary>
public sealed record TurnCompleted(string ResponseText, string? ToolCallsJson, int PromptTokens, int CompletionTokens, int TotalTokens, string? Model = null) : TurnUpdate;
