using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Toimi.Core.Admin;

public static class AdminEndpointBuilder
{
  public static RouteGroupBuilder MapAdmin(this IEndpointRouteBuilder app)
    => app.MapGroup("/admin");
}
