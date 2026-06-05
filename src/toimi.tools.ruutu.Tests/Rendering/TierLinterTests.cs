using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class TierLinterTests
{
  [Fact]
  public void Both_tiers_reject_script_tags()
  {
    var modernResult = TierLinter.Lint("modern", "<div><script>x()</script></div>");
    Assert.False(modernResult.Valid);
    Assert.Contains(modernResult.Issues, i => i.Rule == "no-script");

    var legacyResult = TierLinter.Lint("legacy", "<div><script>x()</script></div>");
    Assert.False(legacyResult.Valid);
    Assert.Contains(legacyResult.Issues, i => i.Rule == "no-script");
  }

  [Fact]
  public void Legacy_rejects_display_flex()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"display: flex\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-flex-or-grid");
  }

  [Fact]
  public void Legacy_rejects_display_grid()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"display:grid\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-flex-or-grid");
  }

  [Fact]
  public void Legacy_rejects_css_variables()
  {
    var result = TierLinter.Lint("legacy", "<div style=\"color: var(--primary)\">x</div>");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-css-variables");
  }

  [Fact]
  public void Legacy_rejects_webp_images()
  {
    var result = TierLinter.Lint("legacy", "<img src=\"a.webp\">");
    Assert.False(result.Valid);
    Assert.Contains(result.Issues, i => i.Rule == "no-webp");
  }

  [Fact]
  public void Legacy_rejects_font_face_and_import()
  {
    var importResult = TierLinter.Lint("legacy", "<style>@import 'x.css';</style>");
    Assert.Contains(importResult.Issues, i => i.Rule == "no-import-or-font-face");

    var faceResult = TierLinter.Lint("legacy", "<style>@font-face{font-family:X}</style>");
    Assert.Contains(faceResult.Issues, i => i.Rule == "no-import-or-font-face");
  }

  [Fact]
  public void Modern_accepts_what_legacy_rejects()
  {
    var html = "<div style=\"display: flex; color: var(--p)\"><img src=\"a.webp\"></div>";
    var result = TierLinter.Lint("modern", html);
    Assert.True(result.Valid);
  }

  [Fact]
  public void Clean_html_passes_both_tiers()
  {
    var html = "<table><tr><td>Hello</td></tr></table>";
    Assert.True(TierLinter.Lint("legacy", html).Valid);
    Assert.True(TierLinter.Lint("modern", html).Valid);
  }

  [Fact]
  public void Issues_include_line_numbers()
  {
    var html = "<div>\n<div>\n<script>x</script>\n</div>";
    var result = TierLinter.Lint("modern", html);
    Assert.Contains(result.Issues, i => i.Line == 3);
  }
}
