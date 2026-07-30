namespace toimi.tools.selain.Streaming;

/// <summary>
/// Self-contained HTML viewer for a tab (inline CSS/JS only — display webviews
/// may have no outbound internet). Paints WebSocket screencast frames onto a
/// canvas; after repeated connection failures it degrades to polling the
/// screenshot endpoint into an img, keeps retrying the WebSocket in the
/// background, and settles on a plain "tab closed" note once polls fail too.
/// </summary>
public static class ViewerPage
{
  public static string Html(Guid tabId)
  {
    return $$"""
      <!doctype html>
      <html>
      <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>selain viewer</title>
      <style>
      html,body{margin:0;height:100%;background:#000}canvas,img{display:block;width:100%;height:100%;object-fit:contain}
      #closed{display:none;height:100%;align-items:center;justify-content:center;color:#888;font:16px sans-serif}
      </style>
      </head>
      <body>
      <canvas id="c"></canvas><img id="f" style="display:none" alt="tab screenshot"><div id="closed">tab closed</div>
      <script>
      const id = "{{tabId}}";
      const canvas = document.getElementById("c"), ctx = canvas.getContext("2d");
      const img = document.getElementById("f"), closed = document.getElementById("closed");
      let wsFailures = 0, pollErrors = 0;
      let pollTimer = null, retryTimer = null;
      let ws = null, done = false;
      let seq = 0, painted = 0; // monotonic: a slow decode must not paint over a newer frame

      function connect() {
        // One socket at a time: a retry against an idle (non-repainting) tab
        // opens fine but stays silent, and stacking another on each retry tick
        // would pile up server-side screencast sessions.
        if (done || (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN))) return;
        const proto = location.protocol === "https:" ? "wss" : "ws";
        ws = new WebSocket(proto + "://" + location.host + "/tabs/" + id + "/stream");
        ws.binaryType = "blob";
        ws.onmessage = async (e) => {
          wsFailures = 0;
          stopPolling();
          const mine = ++seq;
          try {
            const bmp = await createImageBitmap(e.data);
            if (mine > painted) {
              painted = mine;
              canvas.width = bmp.width; canvas.height = bmp.height;
              ctx.drawImage(bmp, 0, 0);
            }
            bmp.close();
          } catch { /* undecodable frame — skip it */ }
        };
        ws.onclose = () => {
          if (done || pollTimer) return; // closed page / polling mode owns reconnects
          wsFailures++;
          if (wsFailures >= 3) { startPolling(); } else { setTimeout(connect, 2000 * wsFailures); }
        };
      }

      function startPolling() {
        if (pollTimer) return;
        pollErrors = 0;
        // Keep the last canvas frame up until a poll actually loads.
        img.onload = () => { pollErrors = 0; canvas.style.display = "none"; img.style.display = "block"; };
        img.onerror = () => { pollErrors++; if (pollErrors >= 4) showClosed(); };
        const refresh = () => { img.src = "/tabs/" + id + "/screenshot?t=" + Date.now(); };
        refresh();
        pollTimer = setInterval(refresh, 15000);
        retryTimer = setInterval(connect, 150000); // the socket may recover — retry every 2.5 min
      }

      function stopPolling() {
        if (!pollTimer) return;
        clearInterval(pollTimer); clearInterval(retryTimer);
        pollTimer = retryTimer = null;
        img.style.display = "none"; canvas.style.display = "block";
      }

      function showClosed() {
        done = true; // terminal: no straggler onclose may restart polling over the note
        clearInterval(pollTimer); clearInterval(retryTimer);
        pollTimer = retryTimer = null;
        if (ws) { try { ws.close(); } catch { /* already dead */ } }
        canvas.style.display = "none"; img.style.display = "none"; closed.style.display = "flex";
      }

      connect();
      </script>
      </body>
      </html>
      """;
  }
}
