using System.Text.RegularExpressions;

namespace toimi.tools.ruutu.Rendering;

public static partial class TierLinter
{
  private sealed record Rule(string Name, Regex Pattern, string Message, bool ModernToo);

  [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase)]
  private static partial Regex ScriptPattern();

  [GeneratedRegex(@"display\s*:\s*(flex|grid)\b", RegexOptions.IgnoreCase)]
  private static partial Regex FlexOrGridPattern();

  [GeneratedRegex(@"var\(\s*--", RegexOptions.IgnoreCase)]
  private static partial Regex CssVariablesPattern();

  [GeneratedRegex(@"\.webp\b|image/webp", RegexOptions.IgnoreCase)]
  private static partial Regex WebpPattern();

  [GeneratedRegex(@"@import\b|@font-face\b", RegexOptions.IgnoreCase)]
  private static partial Regex ImportOrFontFacePattern();

  [GeneratedRegex(@"\b(clamp|min|max)\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex ClampMinMaxPattern();

  [GeneratedRegex(@":has\(|:is\(|:where\(", RegexOptions.IgnoreCase)]
  private static partial Regex HasIsWherePattern();

  private static readonly Rule[] Rules =
  [
    new("no-script",              ScriptPattern(),        "Templates must be declarative; no <script> tags.", ModernToo: true),
    new("no-flex-or-grid",        FlexOrGridPattern(),    "Legacy tier cannot use flexbox or CSS grid.", ModernToo: false),
    new("no-css-variables",       CssVariablesPattern(),  "Legacy tier cannot rely on CSS variables.", ModernToo: false),
    new("no-webp",                WebpPattern(),          "Legacy tier does not support WebP images.", ModernToo: false),
    new("no-import-or-font-face", ImportOrFontFacePattern(), "Legacy tier cannot load external CSS or fonts.", ModernToo: false),
    new("no-clamp-min-max-fn",    ClampMinMaxPattern(),   "Legacy tier cannot use clamp()/min()/max() CSS functions.", ModernToo: false),
    new("no-has-is-where",        HasIsWherePattern(),    "Legacy tier cannot use :has() / :is() / :where().", ModernToo: false)
  ];

  public static LintResult Lint(string tier, string html)
  {
    if (string.IsNullOrEmpty(html))
    {
      return LintResult.Ok();
    }

    var legacyMode = tier == "legacy";
    var issues = new List<LintIssue>();
    var lines = html.Split('\n');

    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      foreach (var rule in Rules)
      {
        if (!legacyMode && !rule.ModernToo)
        {
          continue;
        }

        if (rule.Pattern.IsMatch(line))
        {
          issues.Add(new LintIssue(i + 1, rule.Name, rule.Message));
        }
      }
    }

    return issues.Count == 0 ? LintResult.Ok() : LintResult.Failed(issues);
  }
}
