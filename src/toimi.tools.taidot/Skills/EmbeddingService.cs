using Microsoft.Extensions.AI;

namespace toimi.tools.taidot.Skills;

public class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        var vector = await generator.GenerateVectorAsync(text);
        return vector.ToArray();
    }
}
