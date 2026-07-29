using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class HtmlExtractorTests
{
  [Fact]
  public void Script_style_and_nav_content_is_stripped()
  {
    var text = HtmlExtractor.ExtractText(
      "<body><script>var a=1;</script><style>.x{}</style><nav>menu</nav><p>keep me</p></body>");

    Assert.Contains("keep me", text);
    Assert.DoesNotContain("var a=1", text);
    Assert.DoesNotContain(".x{}", text);
    Assert.DoesNotContain("menu", text);
  }

  [Fact]
  public void Main_element_is_preferred_over_body_noise()
  {
    var text = HtmlExtractor.ExtractText(
      "<body><div>sidebar junk</div><main><p>the article</p></main></body>");

    Assert.Contains("the article", text);
    Assert.DoesNotContain("sidebar junk", text);
  }

  [Fact]
  public void Block_elements_produce_line_breaks_not_run_together_text()
  {
    var text = HtmlExtractor.ExtractText("<body><p>first para</p><p>second para</p></body>");

    Assert.DoesNotContain("parasecond", text.Replace(" ", ""));
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Assert.Contains(lines, l => l.Contains("first para"));
    Assert.Contains(lines, l => l.Contains("second para"));
  }

  [Fact]
  public void Html_entities_are_decoded()
  {
    var text = HtmlExtractor.ExtractText("<body><p>fish &amp; chips</p></body>");

    Assert.Contains("fish & chips", text);
  }
}
