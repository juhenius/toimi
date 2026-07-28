using Microsoft.AspNetCore.Http.Features;

namespace Toimi.Web.Admin;

/// <summary>
/// Defense-in-depth behind the Traefik basicAuth router: Traefik matches the RAW
/// request path case-sensitively, while ASP.NET routes the DECODED path
/// case-insensitively — so "/Api/admin" or "/api/%61dmin" would skip the auth
/// router yet still reach the admin endpoints. This middleware rejects any
/// request that IS an admin request after decoding but whose raw target is not
/// the exact lowercase canonical form the auth router matches.
/// </summary>
public static class AdminPathGuard
{
  public static IApplicationBuilder UseAdminPathGuard(this IApplicationBuilder app)
  {
    return app.Use(async (context, next) =>
    {
      var isAdmin = context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase);

      if (isAdmin)
      {
        // Kestrel always sets RawTarget; hosts that don't (e.g. TestServer's
        // client) leave it empty — fall back to the decoded path, which still
        // preserves case so case-variants remain caught.
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
          raw = context.Request.Path.Value ?? "";
        }
        var canonical = raw.StartsWith("/admin", StringComparison.Ordinal)
          || raw.StartsWith("/api/admin", StringComparison.Ordinal);
        if (!canonical)
        {
          context.Response.StatusCode = StatusCodes.Status404NotFound;
          return;
        }
      }

      await next();
    });
  }
}
