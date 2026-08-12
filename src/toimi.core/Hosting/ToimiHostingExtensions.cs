using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Toimi.Core.Hosting;

/// <summary>
/// Shared bootstrap for the tool servers so the MCP registration, liveness, and
/// readiness endpoints live in one place instead of being copy-pasted per pod.
/// </summary>
public static class ToimiHostingExtensions
{
  /// <summary>
  /// Registers an MCP server (HTTP transport) whose tools are discovered from the
  /// caller's own assembly. The assembly MUST be passed explicitly (<c>typeof(Program).Assembly</c>):
  /// the no-arg <c>WithToolsFromAssembly()</c> resolves via <c>Assembly.GetCallingAssembly()</c>,
  /// which from here would bind to toimi.core and discover zero tools.
  /// </summary>
  public static IServiceCollection AddToimiMcpServer(this IServiceCollection services, string name, Assembly toolAssembly)
  {
    services
      .AddMcpServer(o => o.ServerInfo = new() { Name = name, Version = "1.0.0" })
      .WithHttpTransport()
      .WithToolsFromAssembly(toolAssembly);
    return services;
  }

  /// <summary>
  /// Builder-level entry point for an MCP tool-server pod. Thin today — the
  /// MCP registration is the only config-free bootstrap all five pods share —
  /// but it is the single home future shared bootstrap goes, and the pod
  /// Program.cs files read uniformly. The tool assembly MUST be passed
  /// explicitly (typeof(Program).Assembly) — see <see cref="AddToimiMcpServer"/>
  /// for the assembly-scan footgun.
  /// </summary>
  public static WebApplicationBuilder AddToimiToolServer(this WebApplicationBuilder builder, string serverName, Assembly toolsAssembly)
  {
    builder.Services.AddToimiMcpServer(serverName, toolsAssembly);
    return builder;
  }

  /// <summary>Maps the MCP endpoint plus a liveness /health (bare 200).</summary>
  public static void MapToimiMcp(this WebApplication app)
  {
    app.MapMcp();
    app.MapGet("/health", () => Results.Ok());
  }

  /// <summary>Adds a readiness /ready that verifies the DbContext can reach its database.</summary>
  public static void MapToimiReadiness<TContext>(this WebApplication app) where TContext : DbContext
  {
    app.MapGet("/ready", async (TContext db) =>
    {
      try
      {
        return await db.Database.CanConnectAsync() ? Results.Ok() : Results.StatusCode(503);
      }
      catch
      {
        return Results.StatusCode(503);
      }
    });
  }
}
