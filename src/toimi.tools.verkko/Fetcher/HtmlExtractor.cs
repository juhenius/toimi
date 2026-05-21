using HtmlAgilityPack;

namespace toimi.tools.verkko.Fetcher;

public static class HtmlExtractor
{
  private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
  {
    "script", "style", "nav", "footer", "header", "noscript", "svg", "iframe"
  };

  private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
  {
    "p", "div", "br", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr"
  };

  public static string ExtractText(string html)
  {
    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    var root = doc.DocumentNode.SelectSingleNode("//main")
      ?? doc.DocumentNode.SelectSingleNode("//article")
      ?? doc.DocumentNode.SelectSingleNode("//body")
      ?? doc.DocumentNode;

    var sb = new System.Text.StringBuilder();
    ExtractNode(root, sb);

    return sb.ToString().Trim();
  }

  private static void ExtractNode(HtmlNode node, System.Text.StringBuilder sb)
  {
    if (node.NodeType == HtmlNodeType.Text)
    {
      var text = HtmlEntity.DeEntitize(node.InnerText);
      if (!string.IsNullOrWhiteSpace(text))
      {
        sb.Append(text.Trim()).Append(' ');
      }

      return;
    }

    if (node.NodeType != HtmlNodeType.Element)
    {
      return;
    }

    if (SkipTags.Contains(node.Name))
    {
      return;
    }

    var isBlock = BlockTags.Contains(node.Name);

    foreach (var child in node.ChildNodes)
    {
      ExtractNode(child, sb);
    }

    if (isBlock)
    {
      sb.AppendLine();
    }
  }
}
