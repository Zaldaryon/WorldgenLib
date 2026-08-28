---
uid: worldgenlib.consumer-guide
---

# Consumer guide

Register hooks from your mod's `StartServerSide` method. WorldgenLib freezes registration after world-generation initialization, so registrations must happen during startup.

```csharp
using Vintagestory.API.Server;
using Vintagestory.API.Common;
using WorldgenLib;

public sealed class ExampleWorldgenMod : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        if (!WorldgenLibAPI.IsLoaded)
            return;

        WorldgenLibAPI.RegisterStep7(
            "examplemod",
            OrderBands.AfterVanillaMin + 10,
            ApplyThreshold);
    }

    private static double ApplyThreshold(
        ChunkContext chunk,
        ref ColumnContext column,
        int posY,
        double threshold)
    {
        return threshold;
    }
}
```

Choose an order band that matches the role of the hook. Use `BeforeVanillaMin` through `BeforeVanillaMax` for input rewrites, `Vanilla` for the built-in chain, `AfterVanillaMin` through `AfterVanillaMax` for ordinary effects, and `FinalOverrideMin` for a terminal override.

Hooks run in the world-generation thread or parallel column loop associated with their stage. Keep shared state synchronized, use the context's request-local data for composition, and persist region data through the supported registry APIs.

When no consumer hook is registered, WorldgenLib preserves the vanilla-equivalent path. A consumer should return `false` from a terminal adapter unless it generated the complete pass itself.
