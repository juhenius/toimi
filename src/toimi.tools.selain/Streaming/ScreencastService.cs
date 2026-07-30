using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Playwright;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Streaming;

/// <summary>
/// Relays Chromium's CDP Page.startScreencast JPEG frames to a WebSocket.
/// Frames only flow when the page repaints, so an idle tab costs nothing.
/// The bounded channel drops stale frames when the socket is slower than the
/// page (a live view wants the newest frame, not a backlog).
/// </summary>
public sealed class ScreencastService(BrowserHost host, ILogger<ScreencastService> logger)
{
  public async Task StreamAsync(IPage page, WebSocket socket, CancellationToken ct)
  {
    var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    {
      FullMode = BoundedChannelFullMode.DropOldest
    });
    ICDPSession? cdp = null;

    void OnFrame(object? sender, JsonElement? payload)
    {
      // Defensive reads: CDP shape drift must skip a frame, not throw inside
      // Playwright's event dispatch.
      if (cdp is not { } session
        || payload is not { } evt
        || !evt.TryGetProperty("data", out var dataProp)
        || !evt.TryGetProperty("sessionId", out var sessionIdProp)
        || !sessionIdProp.TryGetInt32(out var sessionId))
      {
        return;
      }
      if (dataProp.ValueKind == JsonValueKind.String && dataProp.GetString() is { } data)
      {
        frames.Writer.TryWrite(Convert.FromBase64String(data));
      }
      _ = AckFrameAsync(session, sessionId);
    }

    // Without these, a page closing mid-stream would leave ReadAllAsync parked
    // on an empty channel forever (no frames, no cancellation) — complete the
    // channel so the relay loop drains and exits.
    void OnPageClose(object? sender, IPage closed)
    {
      frames.Writer.TryComplete();
    }
    void OnCdpClose(object? sender, ICDPSession closed)
    {
      frames.Writer.TryComplete();
    }

    page.Close += OnPageClose;
    host.StreamStarted();
    try
    {
      // Inside the try: the tab can close between the endpoint's lookup and
      // this attach (viewers reconnect on a timer, so the race is routine) —
      // that must end the stream, not escape the endpoint mid-response.
      cdp = await page.Context.NewCDPSessionAsync(page);
      cdp.Event("Page.screencastFrame").OnEvent += OnFrame;
      cdp.Close += OnCdpClose;

      await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
      {
        ["format"] = "jpeg",
        ["quality"] = 60
      });

      // The relay only ever sends, so a client-initiated close would go
      // unnoticed (its CloseAsync would hang awaiting our reply while frames
      // keep flowing) — drain incoming messages concurrently and complete the
      // channel when the client says goodbye.
      _ = DrainIncomingAsync(socket, frames.Writer, ct);

      await foreach (var frame in frames.Reader.ReadAllAsync(ct))
      {
        await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
      }

      // Channel completed (page closed or client-initiated close) — finish the
      // close handshake. CloseOutputAsync, not CloseAsync: the drain loop owns
      // the receive side, and two concurrent receives are illegal.
      if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
      {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client went away or tab closed — normal end of stream.
    }
    catch (WebSocketException ex)
    {
      logger.LogDebug(ex, "Screencast socket closed.");
    }
    catch (PlaywrightException ex)
    {
      // Tab or browser died before/while streaming — normal end of stream.
      logger.LogDebug(ex, "Screencast page lost.");
    }
    finally
    {
      host.StreamEnded();
      page.Close -= OnPageClose;
      if (cdp is not null)
      {
        cdp.Event("Page.screencastFrame").OnEvent -= OnFrame;
        cdp.Close -= OnCdpClose;
        try
        {
          // No explicit stopScreencast: the screencast is scoped to this CDP
          // session, so detaching ends it.
          await cdp.DetachAsync();
        }
        catch (PlaywrightException)
        {
          // Page/browser already gone.
        }
      }
    }
  }

  private static async Task AckFrameAsync(ICDPSession session, int sessionId)
  {
    try
    {
      await session.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = sessionId });
    }
    catch (PlaywrightException)
    {
      // Teardown race — the session is gone, nothing left to ack.
    }
  }

  private static async Task DrainIncomingAsync(WebSocket socket, ChannelWriter<byte[]> writer, CancellationToken ct)
  {
    var buffer = new byte[1024];
    try
    {
      while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
      {
        var result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
        {
          break;
        }
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
    {
      // Socket torn down — same outcome as a polite close.
    }
    finally
    {
      // Structural guarantee: whatever ends the drain (close frame, teardown,
      // or an unexpected throw), the relay loop must not stay parked.
      writer.TryComplete();
    }
  }
}
