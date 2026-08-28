# WorldgenLib

A composable world-generation interoperability library for [Vintage Story](https://www.vintagestory.at/).

WorldgenLib installs narrow runtime seams around the vanilla `GenTerra`,
`GenMaps`, `GenTerraPostProcess`, and `GenBlockLayers` callbacks and runs one
canonical, hookable worldgen pass. Consumer mods register ordered delegates at
specific pipeline steps instead of competing through full replacements. Vanilla
initialization and unrelated callbacks remain available; duplicate generation
is suppressed only when WorldgenLib owns the corresponding pass. Empty-hook parity
is a target verified incrementally by the parity harness, not an unqualified
byte-identical guarantee.

## Quick start

### 1. Build WorldgenLib

```bash
dotnet build WorldgenLib/WorldgenLib.VintageStory.csproj
```

### 2. Reference WorldgenLib in your mod

Add a project reference to `WorldgenLib.VintageStory.csproj` in your `.csproj`:

```xml
<ProjectReference Include="path/to/WorldgenLib/WorldgenLib.VintageStory.csproj" />
```

### 3. Register hooks in `StartServerSide`

```csharp
using WorldgenLib;
using Vintagestory.API.Server;

public class MyWorldgenMod : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        // Check if WorldgenLib is available
        if (!WorldgenLibAPI.IsLoaded)
        {
            api.Logger.Warning("[MyMod] WorldgenLib not found. Hooks will not be registered.");
            return;
        }

        // Step 5: Override water type (per-column)
        WorldgenLibAPI.RegisterStep5("my-mod", OrderBands.AfterVanillaMin + 50, OverrideWater);

        // Step 7: Modify terrain threshold (per-column, per-Y)
        WorldgenLibAPI.RegisterStep7("my-mod", OrderBands.AfterVanillaMin + 50, CarveTerrain);

        // Step 10: Post-placement block edits (per-column)
        WorldgenLibAPI.RegisterStep10("my-mod", OrderBands.AfterVanillaMin + 50, PlaceBlocks);
    }

    private void OverrideWater(ChunkContext chunk, ref ColumnContext col)
    {
        // col.WaterBlockId = chunk.FreshWaterBlockId;
    }

    private double CarveTerrain(ChunkContext chunk, ref ColumnContext col, int posY, double threshold)
    {
        return threshold; // modify threshold here
    }

    private void PlaceBlocks(ChunkContext chunk, ref ColumnCarvingContext col)
    {
        // Use col.GetBlockDataAtY(y) for the vertical chunk containing y.
        // col.BlockData is the bottom-chunk compatibility accessor.
    }
}
```

## API Reference

### Hook Registration (GenTerraHost)

| Method | Step | Signature | Description |
|--------|------|-----------|-------------|
| `RegisterStep0` | BorderTaperPrepare | `(ChunkContext)` | Per-chunk. Mutable: `TaperMap`. |
| `RegisterStep2` | BuildCornerOctaves | `(ChunkContext, ColumnContext)` | Per-column. Modify landform weights/octaves. |
| `RegisterStep4` | VerticalDistortion | `(ChunkContext, ColumnContext)` | Per-column. Modify `DistY`. |
| `RegisterStep5` | WaterColumnSelect | `(ChunkContext, ColumnContext)` | Per-column. Modify `WaterBlockId`. |
| `RegisterStep7` | PerVoxelThreshold | `(ChunkContext, ColumnContext, int, double) → double` | Per-column, per-Y. Return modified threshold. |
| `RegisterStep10` | PostPlacementColumn | `(ChunkContext, ColumnCarvingContext)` | Per-column. Full block accessor. |
| `RegisterTerrainFinalize` | TerrainFinalize | `(ChunkContext)` | Once after all columns and Step 10 hooks. |
| `RegisterFullTerrainGeneration` | Terminal | `(IChunkColumnGenerateRequest) → bool` | Full terrain adapter for a non-decomposable consumer. |

### Hook Registration (GenMapsHost)

| Method | Step | Description |
|--------|------|-------------|
| `RegisterMapsGeoprovince` | Step 1 | Per-region. Modify geoprovince map. |
| `RegisterMapsClimate` | Step 2 | Per-region. Modify climate map. |
| `RegisterMapsForest` | Step 3 | Per-region. Modify forest map. |
| `RegisterMapsUpheavel` | Step 4 | Per-region. Modify upheaval map. |
| `RegisterMapsOcean` | Step 5 | Per-region. Modify ocean map. |
| `RegisterMapsBeach` | Step 6 | Per-region. Modify beach map. |
| `RegisterMapsShrub` | Step 7 | Per-region. Modify shrub map. |
| `RegisterMapsBiome` | Step 8 | Per-region. Modify biome map. |
| `RegisterMapsLandform` | Step 9 | Per-region. Modify landform map. |
| `RegisterMapsRegionFinalize` | Finalize | After all maps, before save. |
| `RegisterMapGenerator` | Factory seam | Wrap or replace one assembled map-layer generator. |
| `RegisterMapPadding` | Padding seam | Replace a stage's vanilla padding value. |
| `RegisterFullMapRegionGeneration` | Terminal | Own a complete region pass when atomic steps are insufficient. |

### Hook Registration (GenTerraPostProcess)

| Method | Description |
|--------|-------------|
| `RegisterPostProcessOptOut` | Return `true` to skip post-processing for a chunk. |
| `RegisterCleanupRule` | Return `false` to prevent deletion of a floating node. |

### Hook Registration (GenBlockLayers)

| Method | Description |
|--------|-------------|
| `RegisterBlockLayersRaiseModifier` | `(localX, localZ, raise, mapChunk) → modifiedRaise` |
| `RegisterBlockLayersSeaLevelFilter` | `(localX, localZ, mapChunk) → true to keep raise` |
| `RegisterFullBlockLayersGeneration` | `(IChunkColumnGenerateRequest) → bool` |

### Force Mechanisms

| Method | Description |
|--------|-------------|
| `ForceLandformAt(ForceLandform)` | Force a landform at a world position with radius. |
| `ForceClimateAt(ForceClimate)` | Force a climate override at a position with radius. |
| `ForceRandomLandArea(int, int, int)` | Require land over a generated area. |

### Landform Registry

```csharp
// Register a custom landform
int index = LandformRegistry.Register("mymod:mylandform", variant);

// Query existing landforms
int vanillaRiver = LandformRegistry.GetIndex("game:riverlandform");
LandformVariant? variant = LandformRegistry.GetByCode("game:riverlandform");

// Access octave data
double[] octaves = LandformRegistry.GetTerrainOctaves(index);
double[] thresholds = LandformRegistry.GetTerrainOctaveThresholds(index);
```

### Order Bands

Pick a band and an offset for your hook's execution order:

```csharp
// Before vanilla logic
OrderBands.BeforeVanillaMin  // -100
OrderBands.BeforeVanillaMax  // -1

// Vanilla logic (reserved)
OrderBands.Vanilla           // 0

// After vanilla (most mods go here)
OrderBands.AfterVanillaMin   // 1
OrderBands.AfterVanillaMax   // 100

// Final override
OrderBands.FinalOverrideMin  // 1000
```

Pre-defined offsets for known mods:

```csharp
OrderBands.RiversOffset        // 50
OrderBands.VSRiverGenOffset    // 50
OrderBands.WatershedsOffset    // 60
OrderBands.TerraPretyOffset    // 50
OrderBands.TerraPretyCarveOffset // 70
```

### Step IDs

Use `StepId` constants for forward-compatible step references:

```csharp
StepId.BorderTaper      // "terra:step0-border-taper"
StepId.BuildOctaves     // "terra:step2-build-octaves"
StepId.VerticalDistortion // "terra:step4-v-distortion"
StepId.WaterSelect      // "terra:step5-water-select"
StepId.Threshold        // "terra:step7-threshold"
StepId.PostPlacement    // "terra:step10-post-place"
```

### Query API

```csharp
// Check if WorldgenLib is loaded
bool loaded = WorldgenLibAPI.IsLoaded;

// Get all registered hooks (for startup logging)
var hooks = WorldgenLibAPI.GetHookReport();
foreach (var (step, order, modId) in hooks)
    api.Logger.Notification($"  {step}: {modId} (order {order})");

// Get step registry
var registry = WorldgenLibAPI.GetStepRegistry();
```

## Context Types

### ChunkContext (per-chunk)

Available at all steps. Contains region map samples, config values, and mutable taper map.

| Property | Type | Mutable | Description |
|----------|------|---------|-------------|
| `ChunkX`, `ChunkZ` | `int` | No | Chunk coordinates |
| `SeaLevel` | `int` | No | Sea level Y position |
| `MapSizeY` | `int` | No | World height |
| `RockBlockId` | `int` | No | Resolved rock block ID |
| `FreshWaterBlockId` | `int` | No | Resolved fresh-water block ID |
| `SaltWaterBlockId` | `int` | No | Resolved salt-water block ID |
| `LakeIceBlockId` | `int` | No | Resolved lake-ice block ID |
| `TaperMap` | `WeightedTaper[]` | Yes (step 0) | Border taper data |

### ColumnContext (per-column, inside Parallel.For)

| Property | Type | Mutable | Description |
|----------|------|---------|-------------|
| `WorldX`, `WorldZ` | `int` | No | World position |
| `LandformWeights` | `float[]` | Yes (step 2) | Per-landform weights |
| `OctaveAmplitudes` | `double[]` | Yes (step 2) | Noise amplitudes |
| `OctaveThresholds` | `double[]` | Yes (step 2) | Noise thresholds |
| `DistY` | `float` | Yes (step 4) | Vertical distortion |
| `WaterBlockId` | `int` | Yes (step 5) | Water block type |
| `NoiseBoundMin/Max` | `double` | No | Noise bounds |
| `ColumnBlockSolidities` | `BitArray` | No (read-only) | Block solidity results |

### ColumnCarvingContext (step 10, post-placement)

| Property | Type | Description |
|----------|------|-------------|
| `WorldX`, `WorldZ` | `int` | World position |
| `SeaLevel` | `int` | Sea level |
| `BlockData` | `IChunkBlocks` | Bottom-chunk compatibility access; use `GetBlockDataAtY(y)` above it |
| `GetBlockDataAtY(y)` | `IChunkBlocks` | Select the vertical chunk containing global Y |
| `ColumnBlockSolidities` | `BitArray` | Solidity from steps 7 to 9 |
| `WaterBlockId` | `int` | Water type selected at step 5 |
| `ChunkIndex3d(x, y, z)` | `int` | Helper for block indexing |

### RegionContext (per-region, GenMaps hooks)

| Property | Type | Description |
|----------|------|-------------|
| `RegionX`, `RegionZ` | `int` | Region coordinates |
| `MapRegion` | `IMapRegion` | The vanilla region object |
| `NoiseSize*` | `int` | Noise sizes for each map |

## Execution order

When multiple mods register hooks at the same step, they execute in **(order, registration order)**, with stable deterministic behavior that matches the ordered-delegate model.

```
Step 0:  vanilla taper → mod hooks (sorted by order)
Step 2:  vanilla BiLerp → mod hooks (modify weights/octaves)
Step 5:  vanilla salt/fresh → mod hooks (override water type)
Step 7:  vanilla threshold → mod hooks (modify per-Y threshold)
Step 10: vanilla block placement → mod hooks (carve, fill, replace)
```

## Compatibility

- **Vanilla parity target**: Empty-hook behavior follows the vanilla algorithm; matched output still requires the versioned parity harness.
- **Multi-mod coexistence**: Mods operate at different order values within the same step.
- **Thread safety**: Hooks inside `Parallel.For` (steps 2 to 7) must be pure functions with no cross-column state.
- **Freeze after init**: Hook lists are locked after `StartServerSide`, so late registrations are rejected.
- **Conflict safety**: A detected full replacement blocks WorldgenLib's duplicate pass; advisory factory/IL findings are reported separately.
- **Four-target coverage**: Rivers-Mod, VSRiverGen, Watersheds, and Terra Prety each have a library seam for every observed server-worldgen mechanism. See the [compatibility and updates](https://github.com/Zaldaryon/WorldgenLib/wiki/Compatibility-and-Updates) page.

## Building

```powershell
$env:VINTAGE_STORY = 'C:\Path\To\VintageStory'

dotnet build WorldgenLib/WorldgenLib.VintageStory.csproj -c Release
dotnet test WorldgenLib.Tests/WorldgenLib.Tests.csproj -c Release
```

See the repository [building guide](../docs/building.md) for the bootstrap,
DocFX, and packaging commands.

## License

See repository license.
