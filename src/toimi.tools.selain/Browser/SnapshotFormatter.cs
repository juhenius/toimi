using System.Security.Cryptography;
using System.Text;

namespace toimi.tools.selain.Browser;

/// <summary>
/// Token-budget rules for page snapshots: action tools return at most ActionCap
/// chars (each action's snapshot lands in LLM context, every step), read_page may
/// return up to ReadCap. Hash powers the "(page unchanged)" suppression.
/// </summary>
public static class SnapshotFormatter
{
  public const int ActionCap = 15_000;
  public const int ReadCap = 50_000;
  public const string TruncationMarker = "\n\n[Truncated — use read_page for full text or wait_for + snapshot to inspect further]";
  public const string ReadTruncationMarker = "\n\n[Truncated at 50K chars — the page text continues beyond this]";

  public static string Truncate(string content, int cap)
  {
    if (content.Length <= cap)
    {
      return content;
    }

    // read_page output is already the "full text" escalation — pointing back
    // at read_page there would be self-referential.
    var marker = cap == ReadCap ? ReadTruncationMarker : TruncationMarker;
    return content[..cap] + marker;
  }

  public static string Hash(string content)
  {
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
  }
}
