using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Toimi.Core.Hosting;

/// <summary>
/// Required-configuration binding with the uniform fail-at-boot error message,
/// replacing the per-pod "Get&lt;T&gt;() ?? throw" copies. Optional config with
/// a fallback (e.g. "?? new NtfyOptions()") deliberately has no helper here —
/// absence is not an error at those sites.
/// </summary>
public static class ToimiConfigurationExtensions
{
  /// <summary>Binds a required section; throws "{section} configuration is required" when absent.</summary>
  public static T RequireConfig<T>(this WebApplicationBuilder builder, string section)
  {
    return builder.Configuration.GetSection(section).Get<T>()
      ?? throw new InvalidOperationException($"{section} configuration is required");
  }

  /// <summary>Required connection string; throws "ConnectionStrings:{name} is required" when absent.</summary>
  public static string RequireConnectionString(this WebApplicationBuilder builder, string name)
  {
    return builder.Configuration.GetConnectionString(name)
      ?? throw new InvalidOperationException($"ConnectionStrings:{name} is required");
  }

  /// <summary>Required single value; throws "{key} is required" when absent.</summary>
  public static string RequireValue(this WebApplicationBuilder builder, string key)
  {
    return builder.Configuration[key]
      ?? throw new InvalidOperationException($"{key} is required");
  }
}
