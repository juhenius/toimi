namespace Toimi.Core.Admin;

/// <summary>
/// The federated-admin upstream path contract, shared by both halves of the
/// seam: admin-bearing tool servers map their endpoints under <see cref="Base"/>
/// (tietue Admin/AdminEndpoints.cs), and toimi.web's AdminForwarder /
/// AdminAggregator compose upstream URLs from the same constants — change a
/// value here and both halves move together (tests pin the literal wire paths,
/// so an accidental edit fails a test rather than shipping).
/// counterpart: the React client cannot consume C# constants — the web-facing
/// /api/admin/... prefix in front of these routes is hard-coded in
/// ClientApp/src/admin/useAdmin.ts (and useAdminSummary.ts / UsagePage.tsx).
/// </summary>
public static class AdminRoutes
{
  /// <summary>Path each admin-bearing server maps its admin route group at.</summary>
  public const string Base = "/admin";

  /// <summary>Cross-server summary route, relative to the admin group.</summary>
  public const string Summary = "/summary";

  /// <summary>Absolute upstream path of the summary endpoint the aggregator fans out to.</summary>
  public const string SummaryPath = Base + Summary;
}
