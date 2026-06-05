using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class TemplateTools(TemplateRepository templates, DbTemplateSource source)
{
  [McpServerTool, Description("List all available templates with their schemas. Read this at session start to know what shapes you can push to a display without writing HTML.")]
  public async Task<string> DisplayListTemplates(CancellationToken ct = default)
  {
    var list = await templates.ListAsync(ct);
    var view = list.Select(t => new {
      t.Name,
      t.Description,
      schema = JsonDocument.Parse(t.SchemaJson).RootElement,
      has_modern = !string.IsNullOrEmpty(t.ModernHtml),
      has_legacy = !string.IsNullOrEmpty(t.LegacyHtml),
      t.IsSeeded
    });
    return JsonSerializer.Serialize(view);
  }

  [McpServerTool, Description("Fetch the full definition of a single template including both modern_html and legacy_html variants. Useful when modifying an existing template.")]
  public async Task<string> DisplayGetTemplate(
    [Description("Template name.")] string name,
    CancellationToken ct = default)
  {
    var t = await templates.GetAsync(name, ct);
    if (t is null) return $"Template '{name}' not found.";
    return JsonSerializer.Serialize(new
    {
      t.Name, t.Description,
      schema = JsonDocument.Parse(t.SchemaJson).RootElement,
      modern_html = t.ModernHtml,
      legacy_html = t.LegacyHtml,
      t.IsSeeded
    });
  }

  [McpServerTool, Description("Create a new template. Both modern_html and legacy_html variants are required and are LINTED before saving. Templates are declarative HTML — no <script> tags. Use data-tap/data-target/data-value attributes for interactivity. Variables come from the data object via Scriban syntax: {{ name }}, {{ for x in items }}…{{ end }}. For composite layouts: any data field shaped {template, data} is auto-rendered and the result is exposed as {fieldname}_html variable to the parent template. MODERN tier: Safari 14+/Chrome 90+ (≈2020+). Flexbox, grid, gap, vw/vh/rem/clamp/min/max, CSS variables, modern color syntax, WebP images, transitions, transforms allowed. LEGACY tier: iOS Safari 9-12 (iPad 2/3/4/Air 1). NO flexbox/grid (use tables/floats). NO var(--*). NO WebP. NO @import/@font-face. NO clamp/min/max CSS functions. NO :has()/:is()/:where(). Use system font stack only. Tune layouts for ~1024×768 either orientation.")]
  public async Task<string> DisplayCreateTemplate(
    [Description("Template name (unique).")] string name,
    [Description("Short human-readable description of what the template shows. Other AI sessions read this when picking which template to use.")] string description,
    [Description("JSON Schema for the data parameter as a JSON string.")] string schemaJson,
    [Description("Scriban template for modern-tier displays.")] string modernHtml,
    [Description("Scriban template for legacy-tier displays.")] string legacyHtml,
    CancellationToken ct = default)
  {
    var modernLint = TierLinter.Lint("modern", modernHtml);
    var legacyLint = TierLinter.Lint("legacy", legacyHtml);
    if (!modernLint.Valid || !legacyLint.Valid)
    {
      return JsonSerializer.Serialize(new
      {
        valid = false,
        modern_issues = modernLint.Issues,
        legacy_issues = legacyLint.Issues
      });
    }

    try { JsonDocument.Parse(schemaJson); }
    catch (JsonException ex) { return $"Error: schemaJson is not valid JSON: {ex.Message}"; }

    try
    {
      await templates.UpsertAiAsync(name, description, schemaJson, modernHtml, legacyHtml, ct);
      return "ok";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Update an existing template. Cannot modify seeded templates. modernHtml and legacyHtml are optional — pass null to keep current value. Linted before save.")]
  public async Task<string> DisplayUpdateTemplate(
    [Description("Template name.")] string name,
    [Description("New description, or null to keep current.")] string? description = null,
    [Description("New schema JSON, or null to keep current.")] string? schemaJson = null,
    [Description("New modern_html, or null to keep current.")] string? modernHtml = null,
    [Description("New legacy_html, or null to keep current.")] string? legacyHtml = null,
    CancellationToken ct = default)
  {
    var existing = await templates.GetAsync(name, ct);
    if (existing is null) return $"Template '{name}' not found.";

    var modernLint = modernHtml is null ? null : TierLinter.Lint("modern", modernHtml);
    var legacyLint = legacyHtml is null ? null : TierLinter.Lint("legacy", legacyHtml);

    if ((modernLint is not null && !modernLint.Valid) ||
        (legacyLint is not null && !legacyLint.Valid))
    {
      return JsonSerializer.Serialize(new
      {
        valid = false,
        modern_issues = modernLint?.Issues,
        legacy_issues = legacyLint?.Issues
      });
    }

    try
    {
      await templates.UpsertAiAsync(
        name,
        description ?? existing.Description,
        schemaJson ?? existing.SchemaJson,
        modernHtml ?? existing.ModernHtml,
        legacyHtml ?? existing.LegacyHtml,
        ct);
      return "ok";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Delete a non-seeded template. Seeded templates cannot be deleted.")]
  public async Task<string> DisplayDeleteTemplate(
    [Description("Template name.")] string name,
    CancellationToken ct = default)
  {
    try
    {
      var ok = await templates.DeleteAsync(name, ct);
      return ok ? "ok" : $"Template '{name}' not found.";
    }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Render a template+data combination without pushing it to a display. Returns the HTML string. Use to sanity-check a new template's output before saving it.")]
  public async Task<string> DisplayPreview(
    [Description("Template name.")] string template,
    [Description("Data JSON.")] string dataJson,
    [Description("Tier: 'modern' or 'legacy'.")] string tier,
    CancellationToken ct = default)
  {
    if (tier is not "modern" and not "legacy") return "Error: tier must be 'modern' or 'legacy'.";
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      return await ScribanRenderer.RenderAsync(template, data, tier, source, ct);
    }
    catch (JsonException ex) { return $"Error: dataJson is not valid JSON: {ex.Message}"; }
    catch (RenderException ex) { return $"Error: {ex.Message}"; }
  }

  [McpServerTool, Description("Return the full author brief for a capability tier: the rules and constraints to follow when authoring templates for it. Use if you need a refresher beyond the inline create-template description.")]
  public string DisplayGetTierBrief(
    [Description("Tier: 'modern' or 'legacy'.")] string tier)
  {
    return tier switch
    {
      "modern" => TierBriefs.MODERN,
      "legacy" => TierBriefs.LEGACY,
      _ => "Error: tier must be 'modern' or 'legacy'."
    };
  }
}
