using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

/// <summary>
/// Runs a real script through the real SuoritinClient against the suoritin image
/// built from its Dockerfile — the seam every other script test fakes.
/// </summary>
public class SuoritinIntegrationTests
{
  // The image build is shared across tests, but lazily: it only starts when a
  // [DockerFact] body actually runs, so a docker-less machine (where the tests
  // are skipped) never attempts it. Containers stay per-test, mirroring
  // PostgresTickLockTests' rationale.
  private static readonly Lazy<Task<IFutureDockerImage>> Image = new(BuildImageAsync);

  private static async Task<IFutureDockerImage> BuildImageAsync()
  {
    // Build context = repo root (where toimi.sln lives), per repo convention.
    var image = new ImageFromDockerfileBuilder()
      .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
      .WithDockerfile("src/toimi.tools.suoritin/Dockerfile")
      .Build();
    await image.CreateAsync();
    return image;
  }

  private static async Task<IContainer> StartContainerAsync()
  {
    var container = new ContainerBuilder(await Image.Value)
      .WithPortBinding(8080, assignRandomHostPort: true)
      .WithWaitStrategy(Wait.ForUnixContainer()
        .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)))
      .Build();
    await container.StartAsync();
    return container;
  }

  private static SuoritinClient ClientFor(IContainer container)
  {
    return new SuoritinClient(new FixedFactory(new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}")));
  }

  private sealed class FixedFactory(Uri baseAddress) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }
  }

  [DockerFact]
  public async Task Executes_a_real_script_end_to_end()
  {
    await using var container = await StartContainerAsync();
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{"n":20}}""");

    var result = await ClientFor(container).ExecuteAsync(new SuoritinRequest(
      "export default function run(input) { console.log('doubling'); return { setField: [{ path: 'n', value: input.data.n * 2 }] }; }",
      input.RootElement.Clone(), 10000, [], ["setField"], null, null));

    Assert.True(result.Ok, result.Error);
    var effects = ScriptEffects.Parse(result.EffectsJson!);
    Assert.Equal("40", Assert.Single(effects.SetFields).ValueJson);
    Assert.Contains(result.Logs, l => l.Contains("doubling"));
  }

  [DockerFact]
  public async Task Denied_fetch_fails_inside_the_container()
  {
    await using var container = await StartContainerAsync();
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{}}""");

    // No allowedHosts: the per-run worker has net:[] permissions, so the fetch
    // must be denied by Deno itself, inside the container.
    var result = await ClientFor(container).ExecuteAsync(new SuoritinRequest(
      "export default async function run() { await fetch('http://example.com/'); return {}; }",
      input.RootElement.Clone(), 10000, [], [], null, null));

    Assert.False(result.Ok);
    Assert.NotNull(result.Error);
    Assert.Contains("net", result.Error, StringComparison.OrdinalIgnoreCase);
  }
}
