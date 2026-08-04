using Xunit;

namespace toimi.tools.tietue.Tests;

/// <summary>
/// A Fact that skips itself when no Docker daemon is reachable, so the suite still
/// passes on a machine without Docker while CI (ubuntu-latest, Docker present) runs it.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
  private static readonly Lazy<bool> DockerAvailable = new(Probe);

  public DockerFactAttribute()
  {
    if (!DockerAvailable.Value)
    {
      Skip = "Docker is not available; skipping this integration test.";
    }
  }

  private static bool Probe()
  {
    return Environment.GetEnvironmentVariable("DOCKER_HOST") is not null
      || File.Exists("/var/run/docker.sock")
      || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock"));
  }
}
