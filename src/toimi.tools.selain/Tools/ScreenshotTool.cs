using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

/// <summary>
/// Returns a CallToolResult (not string) so the PNG travels as an MCP image
/// block the model can actually see. ImageContentBlock.FromBytes is mandatory:
/// in SDK 1.4.1 Data is base64 stored as UTF-8 bytes, so assigning raw or
/// Convert.ToBase64String bytes yourself corrupts the wire format.
/// </summary>
[McpServerToolType]
public class ScreenshotTool(SelainOptions options, TabManager tabs, BrowserHost host)
{
  [McpServerTool, Description("Screenshot the active tab as a PNG image — for pages whose visual layout matters or when the snapshot is unclear.")]
  public async Task<CallToolResult> Screenshot(
    [Description("Capture the full scrollable page instead of just the viewport")] bool fullPage = false)
  {
    if (TabGuard.Disabled(options) is { } off)
    {
      return Text(off);
    }

    host.Touch();
    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return Text("No open tab — use browse first.");
      }

      var page = ((PlaywrightSession)active.Session).Page;
      try
      {
        var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png, FullPage = fullPage });
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(bytes, "image/png")] };
      }
      catch (TimeoutException)
      {
        // A slow capture (huge fullPage layouts) is retryable — don't report
        // it as a lost tab.
        return Text("Screenshot timed out — try again, or without fullPage.");
      }
      catch (PlaywrightException)
      {
        // Same friendly-error contract as TabGuard.WithActiveTabAsync — the
        // dominant failure is the tab/browser dying mid-capture, and
        // Playwright's TargetClosedException is internal, so it can't be
        // told apart from other failures without string-matching.
        return Text(TabGuard.TabLostMessage);
      }
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  private static CallToolResult Text(string message)
  {
    return new CallToolResult { Content = [new TextContentBlock { Text = message }] };
  }
}
