using System.Text.Json.Nodes;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Scripts;

public class ScriptEffectApplier(EntityRepository entities, IMcpInvoker mcp)
{
  public const int MaxMcpCalls = 10;
  private const int MaxErrorChars = 300;

  public async Task<IReadOnlyList<string>> ApplyAsync(
    Entity entity,
    ScriptEffects effects,
    string[] capabilities,
    string[]? reservedPaths = null,
    TimeSpan? effectsBudget = null,
    CancellationToken ct = default)
  {
    var granted = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var reserved = (reservedPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var applied = new List<string>();

    // Effects come from the untrusted suoritin pod and are applied while the
    // scheduler tick holds the advisory tick lock, so application must be
    // bounded (same pattern as AgentRunner): a hung MCP server would otherwise
    // stall every future tick. Genuine caller cancellation still propagates.
    using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (effectsBudget is { } budget)
    {
      budgetCts.CancelAfter(budget);
    }

    var token = budgetCts.Token;

    if (effects.SetFields.Count > 0)
    {
      await ApplySetFieldsAsync(entity, effects.SetFields, granted, reserved, applied, ct, token);
    }

    var invoked = 0;
    for (var i = 0; i < effects.McpCalls.Count; i++)
    {
      var call = effects.McpCalls[i];
      if (invoked >= MaxMcpCalls)
      {
        applied.Add("mcpCall:skipped:limit");
        break;
      }

      if (!granted.Contains($"mcp:{call.Tool}"))
      {
        applied.Add($"mcpCall:{call.Tool}:denied");
        continue;
      }

      try
      {
        invoked++;
        var result = await mcp.CallToolAsync(call.Tool, call.ArgsJson, token);
        applied.Add(result is null ? $"mcpCall:{call.Tool}:error:no such tool" : $"mcpCall:{call.Tool}:ok");
      }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested)
      {
        applied.Add($"mcpCall:{call.Tool}:error:timeout");
        if (i < effects.McpCalls.Count - 1)
        {
          applied.Add("mcpCall:skipped:timeout");
        }

        break;
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        applied.Add($"mcpCall:{call.Tool}:error:{Cap(ex.Message)}");
      }
    }

    return applied;
  }

  private async Task ApplySetFieldsAsync(
    Entity entity,
    IReadOnlyList<SetFieldEffect> setFields,
    HashSet<string> granted,
    HashSet<string> reserved,
    List<string> applied,
    CancellationToken ct,
    CancellationToken token)
  {
    if (!granted.Contains("setField"))
    {
      applied.Add("setField:denied");
      return;
    }

    // One batched update: successive single-field updates would each
    // re-read stale in-memory data and drop the earlier writes.
    var data = JsonNode.Parse(entity.Data.RootElement.GetRawText())!.AsObject();
    var written = 0;
    foreach (var sf in setFields)
    {
      if (reserved.Contains(sf.Path))
      {
        // Job-mode control fields: letting hostile effects rewrite code/grants/
        // allowedHosts/enabled would grant arbitrary code on the next tick.
        applied.Add($"setField:denied:reserved:{sf.Path}");
        continue;
      }

      data[sf.Path] = JsonNode.Parse(sf.ValueJson);
      written++;
    }

    if (written == 0)
    {
      return;
    }

    try
    {
      await entities.UpdateAsync(entity.Id, data, null, token);
      applied.Add($"setField:{written}");
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      applied.Add("setField:error:timeout");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Per-effect isolation: a rejected update (e.g. schema violation) must
      // not prevent the remaining mcpCall effects from running.
      applied.Add($"setField:error:{Cap(ex.Message)}");
    }
  }

  private static string Cap(string message)
  {
    return message.Length > MaxErrorChars ? message[..MaxErrorChars] + "…" : message;
  }
}
