using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace WorldgenLib
{
    /// <summary>
    /// Generates a startup report listing every registered effect, owner,
    /// step, and order value. Printed at server startup for diagnostics.
    /// </summary>
    public static class StartupReport
    {
        public static void Print(ICoreServerAPI api,
            GenMapsHost genMapsHost,
            GenTerraHost genTerraHost,
            GenTerraPostProcessHost? genTerraPostProcessHost,
            GenBlockLayersHost? genBlockLayersHost = null)
        {
            var lines = new List<string>
            {
                "═══════════════════════════════════════════════════════════════",
                "  WorldgenLib Startup Report",
                "═══════════════════════════════════════════════════════════════",
                ""
            };

            // Conflict status
            if (ConflictDetector.HasBlockingConflicts)
            {
                lines.Add("⚠  BLOCKING CONFLICT DETECTED — WorldgenLib worldgen callbacks disabled");
                foreach (var report in ConflictDetector.Reports)
                    lines.Add($"   [{report.Mechanism}] {report.OffendingModId}: {report.Detail}");
            }
            else if (ConflictDetector.HasConflicts)
            {
                lines.Add("⚠  Compatibility advisories detected — WorldgenLib hosts remain active");
                foreach (var report in ConflictDetector.Reports)
                    lines.Add($"   [{report.Mechanism}] {report.OffendingModId}: {report.Detail}");
            }
            else
            {
                lines.Add("✓  No conflicts detected — all hosts active");
            }

            lines.Add("");
            lines.Add("Active hosts:");
            lines.Add($"  GenMapsHost:           {(genMapsHost != null ? "active" : "inactive")}");
            lines.Add($"  GenTerraHost:          {(genTerraHost != null ? "active" : "inactive")}");
            lines.Add($"  GenTerraPostProcessHost: {(genTerraPostProcessHost != null ? "active" : "inactive")}");
            lines.Add($"  GenBlockLayersHost:    {(genBlockLayersHost != null ? "active" : "inactive")}");
            lines.Add("Runtime seams:");
            lines.Add($"  GenMaps blocker:        {(GenMapsBlocker.IsPatched ? "active" : "inactive")}");
            lines.Add($"  GenTerra blocker:       {(GenTerraBlocker.IsPatched ? "active" : "inactive")}");
            lines.Add($"  BlockLayers inline:     {(GenBlockLayersPatch.TransformationAvailable ? "active" : "unavailable")}");

            // Order band constants
            lines.Add("");
            lines.Add("Order bands:");
            lines.Add($"  BeforeVanilla:  {OrderBands.BeforeVanillaMin}..{OrderBands.BeforeVanillaMax}");
            lines.Add($"  Vanilla:        {OrderBands.Vanilla}");
            lines.Add($"  AfterVanilla:   {OrderBands.AfterVanillaMin}..{OrderBands.AfterVanillaMax}");
            lines.Add($"  FinalOverride:  {OrderBands.FinalOverrideMin}+");

            // Registered hooks
            var allHooks = new List<(string Step, double Order, string ModId)>();
            if (genTerraHost != null) allHooks.AddRange(genTerraHost.GetHookReport());
            if (genMapsHost != null) allHooks.AddRange(genMapsHost.GetHookReport());
            if (genTerraPostProcessHost != null) allHooks.AddRange(genTerraPostProcessHost.GetHookReport());
            if (genBlockLayersHost != null) allHooks.AddRange(genBlockLayersHost.GetHookReport());

            lines.Add("");
            lines.Add($"Registered hooks ({allHooks.Count} total):");
            if (allHooks.Count == 0)
            {
                lines.Add("  (none — vanilla-equivalent mode)");
            }
            else
            {
                foreach (var (step, order, modId) in allHooks)
                    lines.Add($"  [{step}] order={order:F0} mod={modId}");
            }

            lines.Add("");
            lines.Add("═══════════════════════════════════════════════════════════════");

            foreach (var line in lines)
                api.Logger.Notification(line);
        }
    }
}
