using Vintagestory.API.Server;

namespace WorldgenLib
{
    /// <summary>
    /// Terminal complete-pass hook for vanilla GenBlockLayers. Return true
    /// when the consumer handled the request and the vanilla pass should be
    /// skipped; return false to let the next hook or vanilla continue.
    /// </summary>
    public delegate bool BlockLayersGenerationHook(IChunkColumnGenerateRequest request);
}
