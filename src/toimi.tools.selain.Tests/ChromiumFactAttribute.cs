using Xunit;

namespace toimi.tools.selain.Tests;

/// <summary>
/// A Fact that skips itself when Playwright's Chromium isn't installed, mirroring
/// tietue's DockerFactAttribute pattern. Install once with:
/// mise exec dotnet -- dotnet run --project src/toimi.tools.selain -- install-browsers
/// </summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
  private static readonly Lazy<bool> ChromiumAvailable = new(Probe);

  public ChromiumFactAttribute()
  {
    if (!ChromiumAvailable.Value)
    {
      Skip = "Playwright Chromium is not installed; run 'dotnet run --project src/toimi.tools.selain -- install-browsers'.";
    }
  }

  private static bool Probe()
  {
    var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
    return Directory.Exists(root) && Directory.EnumerateDirectories(root, "chromium*").Any();
  }
}
