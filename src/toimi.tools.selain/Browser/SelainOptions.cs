namespace toimi.tools.selain.Browser;

public class SelainOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>External base URL displays use to reach /tabs/{id}/view (e.g. https://toimi.example).</summary>
  public string PublicBaseUrl { get; set; } = "";

  /// <summary>Private hosts navigation may still reach — used by integration tests (loopback fixtures).</summary>
  public List<string> AllowedPrivateHosts { get; set; } = [];

  public int IdleShutdownMinutes { get; set; } = 15;
}
