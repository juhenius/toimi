namespace toimi.tools.ruutu.Data;

public record SeedTemplate(string Name, string Description, string SchemaJson, string ModernHtml, string LegacyHtml);

public static class SeedTemplates
{
#pragma warning disable CA1819
  public static readonly SeedTemplate[] All =
#pragma warning restore CA1819
  [
    new(
      Name: "splash",
      Description: "Default idle scene. Shows the Toimi splash and the display identifier — useful for confirming the right URL was opened.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": { "message": { "type": "string" } },
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;align-items:center;justify-content:center;min-height:100vh;background:#f5f3ef;font-family:-apple-system,Segoe UI,system-ui,sans-serif">
          <div style="text-align:center">
            <div style="font-size:48px;font-weight:300;color:#222">Toimi</div>
            <div style="font-size:14px;color:#888;margin-top:12px">{{ message ?? "" }}</div>
          </div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" style="background:#f5f3ef;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr>
            <td align="center" valign="middle">
              <div style="font-size:48px;color:#222">Toimi</div>
              <div style="font-size:14px;color:#888;margin-top:12px">{{ message ?? "" }}</div>
            </td>
          </tr>
        </table>
        """
    ),
    new(
      Name: "clock",
      Description: "Large current time + date. Ticks client-side from Date.now() in the device's local time zone. Optional 24h/12h format. Useful as a single-tile glanceable element.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "format": { "type": "string", "enum": ["24h", "12h"] }
          },
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div data-clock="{{ format ?? "24h" }}"
             style="display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;background:#fff;font-family:-apple-system,system-ui,sans-serif">
          <div data-clock-time style="font-size:96px;font-weight:200;color:#111">--:--</div>
          <div data-clock-date style="font-size:18px;color:#666;margin-top:8px"></div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr>
            <td align="center" valign="middle" data-clock="{{ format ?? "24h" }}">
              <div data-clock-time style="font-size:96px;color:#111">--:--</div>
              <div data-clock-date style="font-size:18px;color:#666;margin-top:8px"></div>
            </td>
          </tr>
        </table>
        """
    ),
    new(
      Name: "message",
      Description: "Big text card with optional title. Use for short standalone messages like 'Welcome home' or 'Leave for school in 5 min'.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "body":  { "type": "string" }
          },
          "required": ["body"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;align-items:center;justify-content:center;min-height:100vh;background:#fafaf7;padding:40px;font-family:-apple-system,system-ui,sans-serif">
          <div style="max-width:600px;text-align:center">
            {{ if title }}<div style="font-size:14px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ title }}</div>{{ end }}
            <div style="font-size:36px;color:#222;margin-top:18px;line-height:1.3">{{ body }}</div>
          </div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" style="background:#fafaf7;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr>
            <td align="center" valign="middle" style="padding:40px">
              {{ if title }}<div style="font-size:14px;color:#888;text-transform:uppercase">{{ title }}</div>{{ end }}
              <div style="font-size:36px;color:#222;margin-top:18px">{{ body }}</div>
            </td>
          </tr>
        </table>
        """
    ),
    new(
      Name: "notification",
      Description: "Notification card. Most commonly used as an overlay. Tap anywhere dismisses. Severity styles the accent color.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "title":    { "type": "string" },
            "body":     { "type": "string" },
            "icon":     { "type": "string" },
            "severity": { "type": "string", "enum": ["info", "warn", "alert"] }
          },
          "required": ["title", "body"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div data-tap="dismiss" data-target="overlay"
             style="background:#222;color:#fff;padding:20px 24px;border-radius:10px;min-width:280px;max-width:400px;
                    box-shadow:0 8px 24px rgba(0,0,0,0.3);margin:24px auto;font-family:-apple-system,system-ui,sans-serif">
          <div style="font-size:11px;letter-spacing:2px;color:#aaa;text-transform:uppercase">{{ severity ?? "info" }}</div>
          <div style="font-size:18px;font-weight:500;margin-top:6px">{{ title }}</div>
          <div style="font-size:14px;color:#ccc;margin-top:8px">{{ body }}</div>
          <div style="font-size:11px;color:#888;margin-top:12px">tap to dismiss</div>
        </div>
        """,
      LegacyHtml: """
        <table data-tap="dismiss" data-target="overlay"
               cellpadding="20" style="background:#222;color:#fff;margin:24px auto;border:0;font-family:-apple-system,Helvetica,Arial,sans-serif;width:300px">
          <tr><td>
            <div style="font-size:11px;color:#aaa;text-transform:uppercase">{{ severity ?? "info" }}</div>
            <div style="font-size:18px;margin-top:6px">{{ title }}</div>
            <div style="font-size:14px;color:#ccc;margin-top:8px">{{ body }}</div>
            <div style="font-size:11px;color:#888;margin-top:12px">tap to dismiss</div>
          </td></tr>
        </table>
        """
    ),
    new(
      Name: "todo_list",
      Description: "Title plus a checkbox list. Tap a row to record a check event with target=step.id. Use for in-progress routines (e.g. evening routine).",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "steps": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":    { "type": "string" },
                  "label": { "type": "string" },
                  "done":  { "type": "boolean" }
                },
                "required": ["id", "label"]
              }
            }
          },
          "required": ["title", "steps"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="background:#f5f3ef;min-height:100vh;padding:24px;font-family:-apple-system,system-ui,sans-serif">
          <div style="max-width:520px;margin:0 auto;background:#fff;border:1px solid #e0d8cc;padding:20px;border-radius:8px">
            <div style="font-size:20px;font-weight:600;color:#222">{{ title }}</div>
            <div style="margin-top:14px">
              {{ for step in steps }}
                <div data-tap="check" data-target="{{ step.id }}" data-value="{{ if step.done }}false{{ else }}true{{ end }}"
                     style="display:flex;align-items:center;padding:10px 0;border-bottom:1px solid #eee">
                  <div style="width:24px;font-size:18px">{{ if step.done }}&#9745;{{ else }}&#9744;{{ end }}</div>
                  <div style="flex:1;font-size:15px;{{ if step.done }}text-decoration:line-through;color:#999{{ else }}color:#333{{ end }}">{{ step.label }}</div>
                </div>
              {{ end }}
            </div>
          </div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" style="background:#f5f3ef;font-family:-apple-system,Helvetica,Arial,sans-serif">
          <tr><td align="center" style="padding:24px">
            <table width="520" cellpadding="16" style="background:#fff;border:1px solid #e0d8cc">
              <tr><td>
                <div style="font-size:20px;color:#222">{{ title }}</div>
                <table width="100%" cellpadding="6" style="margin-top:14px">
                  {{ for step in steps }}
                  <tr data-tap="check" data-target="{{ step.id }}" data-value="{{ if step.done }}false{{ else }}true{{ end }}">
                    <td width="28" style="font-size:18px">{{ if step.done }}&#9745;{{ else }}&#9744;{{ end }}</td>
                    <td style="font-size:15px;{{ if step.done }}text-decoration:line-through;color:#999{{ else }}color:#333{{ end }}">{{ step.label }}</td>
                  </tr>
                  {{ end }}
                </table>
              </td></tr>
            </table>
          </td></tr>
        </table>
        """
    ),
    new(
      Name: "weather",
      Description: "Current temperature plus brief outlook. AI populates from koti (Home Assistant weather entity).",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "location": { "type": "string" },
            "current":  {
              "type": "object",
              "properties": {
                "temp":       { "type": "number" },
                "condition":  { "type": "string" },
                "feels_like": { "type": "number" }
              },
              "required": ["temp", "condition"]
            },
            "today": {
              "type": "object",
              "properties": {
                "high":  { "type": "number" },
                "low":   { "type": "number" },
                "notes": { "type": "string" }
              }
            }
          },
          "required": ["location", "current"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
          <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ location }}</div>
          <div style="font-size:64px;font-weight:200;color:#222;margin-top:6px;line-height:1">{{ current.temp }}&deg;</div>
          <div style="font-size:14px;color:#666;margin-top:4px">{{ current.condition }}{{ if current.feels_like }} &middot; feels {{ current.feels_like }}&deg;{{ end }}</div>
          {{ if today }}
            <div style="font-size:12px;color:#888;margin-top:14px">
              {{ if today.low }}&darr; {{ today.low }}&deg;  {{ end }}{{ if today.high }}&uarr; {{ today.high }}&deg;{{ end }}
              {{ if today.notes }} &middot; {{ today.notes }}{{ end }}
            </div>
          {{ end }}
        </div>
        """,
      LegacyHtml: """
        <table cellpadding="20" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
          <tr><td>
            <div style="font-size:11px;color:#888;text-transform:uppercase">{{ location }}</div>
            <div style="font-size:64px;color:#222;margin-top:6px">{{ current.temp }}&deg;</div>
            <div style="font-size:14px;color:#666">{{ current.condition }}{{ if current.feels_like }} &middot; feels {{ current.feels_like }}&deg;{{ end }}</div>
            {{ if today }}
              <div style="font-size:12px;color:#888;margin-top:14px">
                {{ if today.low }}&darr; {{ today.low }}&deg;  {{ end }}{{ if today.high }}&uarr; {{ today.high }}&deg;{{ end }}
                {{ if today.notes }} &middot; {{ today.notes }}{{ end }}
              </div>
            {{ end }}
          </td></tr>
        </table>
        """
    ),
    new(
      Name: "calendar_day",
      Description: "Today's events as a vertical list with times. AI populates from Google Calendar.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "date":   { "type": "string" },
            "events": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "time":  { "type": "string" },
                  "title": { "type": "string" }
                },
                "required": ["time", "title"]
              }
            }
          },
          "required": ["date", "events"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
          <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">{{ date }}</div>
          <div style="margin-top:14px">
            {{ for e in events }}
              <div style="padding:8px 0;border-bottom:1px solid #eee;font-size:14px;color:#333">
                <strong style="color:#222">{{ e.time }}</strong>&nbsp;&nbsp;{{ e.title }}
              </div>
            {{ end }}
            {{ if (events | array.size) == 0 }}
              <div style="color:#888;font-size:13px">No events today.</div>
            {{ end }}
          </div>
        </div>
        """,
      LegacyHtml: """
        <table cellpadding="16" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
          <tr><td>
            <div style="font-size:11px;color:#888;text-transform:uppercase">{{ date }}</div>
            <table width="100%" cellpadding="6" style="margin-top:14px">
              {{ for e in events }}
                <tr><td style="border-bottom:1px solid #eee;font-size:14px;color:#333"><strong>{{ e.time }}</strong>&nbsp;&nbsp;{{ e.title }}</td></tr>
              {{ end }}
              {{ if (events | array.size) == 0 }}
                <tr><td style="color:#888;font-size:13px">No events today.</td></tr>
              {{ end }}
            </table>
          </td></tr>
        </table>
        """
    ),
    new(
      Name: "reminders",
      Description: "Upcoming reminders, time-ordered. AI populates from tietue reminders.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "due_at": { "type": "string" },
                  "title":  { "type": "string" }
                },
                "required": ["due_at", "title"]
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="background:#fff;padding:20px;font-family:-apple-system,system-ui,sans-serif;border:1px solid #ddd;border-radius:8px">
          <div style="font-size:11px;letter-spacing:2px;color:#888;text-transform:uppercase">Reminders</div>
          <div style="margin-top:14px">
            {{ for it in items }}
              <div style="padding:8px 0;border-bottom:1px solid #eee;font-size:14px;color:#333">
                <span style="color:#888;font-size:12px;display:inline-block;width:120px">{{ it.due_at }}</span>{{ it.title }}
              </div>
            {{ end }}
            {{ if (items | array.size) == 0 }}
              <div style="color:#888;font-size:13px">No upcoming reminders.</div>
            {{ end }}
          </div>
        </div>
        """,
      LegacyHtml: """
        <table cellpadding="16" style="background:#fff;font-family:-apple-system,Helvetica,Arial,sans-serif;border:1px solid #ddd">
          <tr><td>
            <div style="font-size:11px;color:#888;text-transform:uppercase">Reminders</div>
            <table width="100%" cellpadding="6" style="margin-top:14px">
              {{ for it in items }}
                <tr>
                  <td width="130" style="color:#888;font-size:12px">{{ it.due_at }}</td>
                  <td style="font-size:14px;color:#333">{{ it.title }}</td>
                </tr>
              {{ end }}
              {{ if (items | array.size) == 0 }}
                <tr><td colspan="2" style="color:#888;font-size:13px">No upcoming reminders.</td></tr>
              {{ end }}
            </table>
          </td></tr>
        </table>
        """
    ),
    new(
      Name: "split_horizontal",
      Description: "Two tiles side by side. Sub-templates declared as { template, data } in 'left' and 'right'. Renders each at the display's capability tier.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "left":  { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] },
            "right": { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] }
          },
          "required": ["left","right"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;gap:12px;padding:12px;min-height:100vh;background:#f5f3ef;box-sizing:border-box">
          <div style="flex:1;min-width:0">{{ left_html }}</div>
          <div style="flex:1;min-width:0">{{ right_html }}</div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" cellpadding="6" cellspacing="0" style="background:#f5f3ef">
          <tr>
            <td width="50%" valign="top">{{ left_html }}</td>
            <td width="50%" valign="top">{{ right_html }}</td>
          </tr>
        </table>
        """
    ),
    new(
      Name: "split_vertical",
      Description: "Two tiles stacked top over bottom. Sub-templates in 'top' and 'bottom'.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "top":    { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] },
            "bottom": { "type": "object", "properties": { "template": {"type":"string"}, "data": {"type":"object"} }, "required": ["template","data"] }
          },
          "required": ["top","bottom"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;flex-direction:column;gap:12px;padding:12px;min-height:100vh;background:#f5f3ef;box-sizing:border-box">
          <div style="flex:1;min-height:0">{{ top_html }}</div>
          <div style="flex:1;min-height:0">{{ bottom_html }}</div>
        </div>
        """,
      LegacyHtml: """
        <table width="100%" height="100%" cellpadding="6" cellspacing="0" style="background:#f5f3ef">
          <tr><td valign="top">{{ top_html }}</td></tr>
          <tr><td valign="top">{{ bottom_html }}</td></tr>
        </table>
        """
    ),
    new(
      Name: "stack",
      Description: "N tiles stacked vertically with optional gap. 'items' is an array of { template, data }.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "template": { "type":"string" }, "data": { "type":"object" } },
                "required": ["template","data"]
              }
            },
            "gap": { "type": "integer", "minimum": 0 }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        <div style="display:flex;flex-direction:column;gap:{{ gap ?? 12 }}px;padding:12px;background:#f5f3ef;min-height:100vh;box-sizing:border-box">
          {{ for it in items_html }}<div>{{ it }}</div>{{ end }}
        </div>
        """,
      LegacyHtml: """
        <table width="100%" cellpadding="{{ (gap ?? 12) / 2 }}" cellspacing="0" style="background:#f5f3ef">
          {{ for it in items_html }}<tr><td>{{ it }}</td></tr>{{ end }}
        </table>
        """
    ),
    // sandbox="allow-scripts allow-same-origin" is intentional: the embedded page
    // must run its own scripts and use its own cookies/storage to function (e.g. a
    // tracking page). The "escape the sandbox" risk of this token combination only
    // applies to SAME-ORIGIN framed content; here `url` is validated by safe_url
    // (https-only, internal hosts blocked) so the page is always cross-origin to the
    // display shell, where `frameElement` is null and it cannot reach the parent.
    new(
      Name: "webview",
      Description: "Embed an external web page (e.g. a parcel-tracking page) in a sandboxed iframe. Provide an https `url`; an optional `title` shows a header bar. Works on modern and legacy displays. Note: sites that forbid framing (X-Frame-Options / CSP frame-ancestors) will appear blank.",
      SchemaJson: /*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "url":   { "type": "string", "description": "https URL to embed" },
            "title": { "type": "string", "description": "optional header label" }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """,
      ModernHtml: """
        {{ if title }}<div style="height:40px;background:#222;color:#fff;font:500 15px -apple-system,Helvetica,Arial,sans-serif;line-height:40px;padding:0 14px;overflow:hidden;white-space:nowrap">{{ title | html.escape }}</div>{{ end }}
        <iframe src="{{ url | safe_url }}" sandbox="allow-scripts allow-same-origin" referrerpolicy="no-referrer" style="display:block;width:100%;height:{{ if title }}calc(100% - 40px){{ else }}100%{{ end }};border:0;background:#fff"></iframe>
        """,
      LegacyHtml: """
        {{ if title }}<div style="height:40px;background:#222;color:#fff;font:500 15px -apple-system,Helvetica,Arial,sans-serif;line-height:40px;padding:0 14px;overflow:hidden;white-space:nowrap">{{ title | html.escape }}</div>{{ end }}
        <iframe src="{{ url | safe_url }}" sandbox="allow-scripts allow-same-origin" referrerpolicy="no-referrer" style="display:block;width:100%;height:{{ if title }}calc(100% - 40px){{ else }}100%{{ end }};border:0;background:#fff"></iframe>
        """
    )
  ];
}
