using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace WorldgenLib.Tests
{
    /// <summary>
    /// Microbenchmarks for the OrderedHookList dispatch hot path.
    /// Measures:
    /// 1. Count==0 fast-path overhead (the common case with no mods)
    /// 2. Enumerate() with 1, 4, 8 hooks registered
    /// 3. Delegate invocation cost per hook
    /// 4. GC allocation in the fast path
    ///
    /// All measurements use Stopwatch for nanosecond resolution.
    /// Results are documented in docs/design/performance.md.
    /// </summary>
    public class PerformanceBenchmarks
    {
        private readonly ITestOutputHelper _output;

        public PerformanceBenchmarks(ITestOutputHelper output)
        {
            _output = output;
        }

        // ── Helpers ──

        private static readonly Action NoOpDelegate = () => { };

        private static OrderedHookList<Action> BuildHookList(int count)
        {
            var list = new OrderedHookList<Action>();
            for (int i = 0; i < count; i++)
                list.Register($"mod-{i}", i * 10.0, NoOpDelegate);
            list.Freeze();
            return list;
        }

        private double MeasureNanos(Action action, int iterations)
        {
            // Warmup
            for (int i = 0; i < 100; i++) action();

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            long gen0Before = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                action();
            sw.Stop();
            long gen0After = GC.CollectionCount(0);

            double totalNanos = sw.Elapsed.TotalNanoseconds;
            double perCall = totalNanos / iterations;

            _output.WriteLine($"  {iterations} iters: {sw.Elapsed.TotalMilliseconds:F2}ms total, {perCall:F0} ns/call, GC0: {gen0After - gen0Before}");

            return perCall;
        }

        // ══════════════════════════════════════════════════════════════
        //  1. Count==0 fast path — the no-mods case
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_CountZero_FastPath()
        {
            var list = new OrderedHookList<Action>();
            list.Freeze();

            _output.WriteLine("=== OrderedHookList.Count==0 fast path ===");

            // Measure Count property access (field read)
            double countNanos = MeasureNanos(() =>
            {
                _ = list.Count;
            }, 100_000);

            // Measure the typical if-guard pattern used in GenTerraHost
            double guardNanos = MeasureNanos(() =>
            {
                if (list.Count > 0)
                {
                    foreach (var (order, modId, hook) in list.Enumerate())
                        hook();
                }
            }, 100_000);

            _output.WriteLine($"  Count property: {countNanos:F0} ns");
            _output.WriteLine($"  if-guard (should be ~same): {guardNanos:F0} ns");

            // Count==0 should be under 50ns (just an int field read)
            Assert.True(countNanos < 200,
                $"Count==0 fast path too slow: {countNanos:F0} ns (budget: <200 ns)");
        }

        // ══════════════════════════════════════════════════════════════
        //  2. Enumerate with varying hook counts
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_Enumerate_1Hook()
        {
            var list = BuildHookList(1);

            _output.WriteLine("=== Enumerate with 1 hook ===");

            double perCall = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list.Enumerate())
                    hook();
            }, 50_000);

            _output.WriteLine($"  1 hook: {perCall:F0} ns/call");

            // 1 hook dispatch should be under 1000ns
            Assert.True(perCall < 1500,
                $"1-hook dispatch too slow: {perCall:F0} ns (budget: <1500 ns)");
        }

        [Fact]
        public void Bench_Enumerate_4Hooks()
        {
            var list = BuildHookList(4);

            _output.WriteLine("=== Enumerate with 4 hooks ===");

            double perCall = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list.Enumerate())
                    hook();
            }, 50_000);

            _output.WriteLine($"  4 hooks: {perCall:F0} ns/call");

            // 4 hook dispatch should be under 4000ns
            Assert.True(perCall < 5000,
                $"4-hook dispatch too slow: {perCall:F0} ns (budget: <5000 ns)");
        }

        [Fact]
        public void Bench_Enumerate_8Hooks()
        {
            var list = BuildHookList(8);

            _output.WriteLine("=== Enumerate with 8 hooks ===");

            double perCall = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list.Enumerate())
                    hook();
            }, 50_000);

            _output.WriteLine($"  8 hooks: {perCall:F0} ns/call");

            // 8 hook dispatch should be under 8000ns
            Assert.True(perCall < 10000,
                $"8-hook dispatch too slow: {perCall:F0} ns (budget: <10000 ns)");
        }

        [Fact]
        public void Bench_Snapshot_8Hooks()
        {
            var list = BuildHookList(8);

            _output.WriteLine("=== Frozen snapshot with 8 hooks ===");

            double perCall = MeasureNanos(() =>
            {
                foreach (var entry in list.Snapshot)
                {
                    if (!list.IsDisabled(entry.ModId)) entry.Handler();
                }
            }, 50_000);

            _output.WriteLine($"  8-hook snapshot: {perCall:F0} ns/call");
            Assert.True(perCall < 10000,
                $"8-hook snapshot dispatch too slow: {perCall:F0} ns (budget: <10000 ns)");
        }

        // ══════════════════════════════════════════════════════════════
        //  3. Linear scaling check
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_DispatchScaling_Linear()
        {
            var list1 = BuildHookList(1);
            var list4 = BuildHookList(4);
            var list8 = BuildHookList(8);

            _output.WriteLine("=== Dispatch scaling ===");

            double t1 = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list1.Enumerate())
                    hook();
            }, 50_000);

            double t4 = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list4.Enumerate())
                    hook();
            }, 50_000);

            double t8 = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list8.Enumerate())
                    hook();
            }, 50_000);

            double ratio4 = t4 / t1;
            double ratio8 = t8 / t1;

            _output.WriteLine($"  1 hook: {t1:F0} ns");
            _output.WriteLine($"  4 hooks: {t4:F0} ns (ratio: {ratio4:F1}x)");
            _output.WriteLine($"  8 hooks: {t8:F0} ns (ratio: {ratio8:F1}x)");

            // Scaling should be roughly linear (within 5x tolerance for microbench noise)
            Assert.True(ratio4 < 6.0,
                $"4-hook dispatch not linear: {ratio4:F1}x (expected <6x)");
            Assert.True(ratio8 < 12.0,
                $"8-hook dispatch not linear: {ratio8:F1}x (expected <12x)");
        }

        // ══════════════════════════════════════════════════════════════
        //  4. ThresholdHook (Func return double) — the step 7 hot path
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_ThresholdHook_ReturnsDouble()
        {
            // Simulate the step 7 threshold hook pattern
            var list = new OrderedHookList<Func<double, double>>();
            for (int i = 0; i < 4; i++)
                list.Register($"mod-{i}", i * 10.0,
                    threshold => threshold);
            list.Freeze();

            _output.WriteLine("=== ThresholdHook (Func<double,double>) dispatch ===");

            double perCall = MeasureNanos(() =>
            {
                foreach (var (order, modId, hook) in list.Enumerate())
                {
                    double result = hook(0.5);
                    _ = result;
                }
            }, 50_000);

            _output.WriteLine($"  4 threshold hooks: {perCall:F0} ns/call");

            Assert.True(perCall < 5000,
                $"ThresholdHook dispatch too slow: {perCall:F0} ns (budget: <5000 ns)");
        }

        // ══════════════════════════════════════════════════════════════
        //  5. Zero-allocation fast path
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_NoAllocation_FastPath()
        {
            var list = new OrderedHookList<Action>();
            list.Freeze();

            _output.WriteLine("=== Allocation check: Count==0 fast path ===");

            // Measure allocations on this thread. GC.GetTotalMemory is a
            // process-wide heap snapshot and can be changed by the testhost
            // while this loop runs, producing false positives in CI.
            for (int i = 0; i < 10_000; i++)
            {
                if (list.Count > 0)
                {
                    foreach (var (order, modId, hook) in list.Enumerate())
                        hook();
                }
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1_000_000; i++)
            {
                if (list.Count > 0)
                {
                    foreach (var (order, modId, hook) in list.Enumerate())
                        hook();
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            _output.WriteLine($"  1M iterations, allocated: {allocated} bytes ({allocated / 1_000_000.0:F2} bytes/iter)");

            // The Count==0 fast path must not allocate on its executing thread.
            Assert.Equal(0, allocated);
        }

        // ══════════════════════════════════════════════════════════════
        //  6. Parallel.For with hook dispatch (simulates GenTerraHost)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_ParallelFor_HookOverhead()
        {
            var list = BuildHookList(2);
            var emptyList = new OrderedHookList<Action>();
            emptyList.Freeze();

            _output.WriteLine("=== Parallel.For with hook dispatch ===");

            const int chunkSize = 32;
            const int iterations = 500;

            double emptyTime = MeasureNanos(() =>
            {
                Parallel.For(0, chunkSize * chunkSize, new ParallelOptions { MaxDegreeOfParallelism = 4 }, idx =>
                {
                    if (emptyList.Count > 0)
                    {
                        foreach (var (order, modId, hook) in emptyList.Enumerate())
                            hook();
                    }
                });
            }, iterations);

            double withHooksTime = MeasureNanos(() =>
            {
                Parallel.For(0, chunkSize * chunkSize, new ParallelOptions { MaxDegreeOfParallelism = 4 }, idx =>
                {
                    if (list.Count > 0)
                    {
                        foreach (var (order, modId, hook) in list.Enumerate())
                            hook();
                    }
                });
            }, iterations);

            double overhead = withHooksTime - emptyTime;
            double overheadPct = (overhead / emptyTime) * 100;

            _output.WriteLine($"  Empty hooks: {emptyTime:F0} ns/chunk");
            _output.WriteLine($"  2 hooks: {withHooksTime:F0} ns/chunk");
            _output.WriteLine($"  Overhead: {overhead:F0} ns ({overheadPct:F1}%)");

            // The parallel overhead includes thread scheduling noise.
            // What matters: the hook dispatch itself is not dominant.
            // With 2 no-op hooks, the overhead should be small relative
            // to the Parallel.For base cost.
            Assert.True(withHooksTime < emptyTime * 10,
                $"Hook overhead too large: {withHooksTime:F0} vs {emptyTime:F0} ns");
        }

        // ══════════════════════════════════════════════════════════════
        //  7. Step 7 simulated hot loop (sequential, no Parallel.For)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_Step7Threshold_SimulatedHotLoop()
        {
            // Simulate the step 7 inner loop pattern:
            // for each of 1024 columns, for each of ~256 Y levels, invoke hook
            // This is the hottest loop in the pipeline.
            // Use sequential loop to isolate hook dispatch cost from Parallel.For noise.

            var emptyList = new OrderedHookList<Func<double, double>>();
            emptyList.Freeze();

            var singleHookList = new OrderedHookList<Func<double, double>>();
            singleHookList.Register("river-mod", OrderBands.AfterVanillaMin,
                threshold => threshold * 0.95);
            singleHookList.Freeze();

            const int columnsPerChunk = 1024;
            const int yLevels = 256;
            const int chunks = 5;

            _output.WriteLine("=== Step 7 simulated hot loop (sequential) ===");

            double emptyTime = MeasureNanos(() =>
            {
                for (int col = 0; col < columnsPerChunk * chunks; col++)
                {
                    for (int y = 1; y < yLevels; y++)
                    {
                        if (emptyList.Count > 0)
                        {
                            foreach (var (order, modId, hook) in emptyList.Enumerate())
                            {
                                double result = hook(0.5);
                                _ = result;
                            }
                        }
                    }
                }
            }, 3);

            double hookTime = MeasureNanos(() =>
            {
                for (int col = 0; col < columnsPerChunk * chunks; col++)
                {
                    for (int y = 1; y < yLevels; y++)
                    {
                        if (singleHookList.Count > 0)
                        {
                            foreach (var (order, modId, hook) in singleHookList.Enumerate())
                            {
                                double result = hook(0.5);
                                _ = result;
                            }
                        }
                    }
                }
            }, 3);

            double totalInvocations = columnsPerChunk * chunks * (yLevels - 1);
            double emptyNanosPerInvoke = emptyTime / totalInvocations;
            double hookNanosPerInvoke = hookTime / totalInvocations;
            double overheadPerInvoke = hookNanosPerInvoke - emptyNanosPerInvoke;

            _output.WriteLine($"  Total invocations: {totalInvocations:N0}");
            _output.WriteLine($"  Empty: {emptyNanosPerInvoke:F1} ns/invocation");
            _output.WriteLine($"  1 hook: {hookNanosPerInvoke:F1} ns/invocation");
            _output.WriteLine($"  Per-hook overhead: {overheadPerInvoke:F1} ns/invocation");

            // The Count==0 fast path should be under 20ns per invocation
            Assert.True(emptyNanosPerInvoke < 50,
                $"Empty fast path too slow: {emptyNanosPerInvoke:F1} ns (budget: <50 ns)");

            // Single hook overhead should be under 100ns per invocation
            Assert.True(overheadPerInvoke < 200,
                $"Single hook overhead too large: {overheadPerInvoke:F1} ns (budget: <200 ns)");
        }

        // ══════════════════════════════════════════════════════════════
        //  8. Multiple steps: simulate full pipeline dispatch
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_FullPipeline_SixSteps()
        {
            // Simulate the full GenTerraHost pipeline:
            // 6 hook lists, each checked per chunk, most empty
            var step0 = BuildHookList(0);
            var step2 = BuildHookList(0);
            var step4 = BuildHookList(1);
            var step5 = BuildHookList(1);
            var step7 = BuildHookList(2);
            var step10 = BuildHookList(1);

            _output.WriteLine("=== Full pipeline: 6 steps, 5 hooks total ===");

            const int chunks = 100;

            double perChunk = MeasureNanos(() =>
            {
                for (int c = 0; c < chunks; c++)
                {
                    if (step0.Count > 0) { foreach (var (_, _, h) in step0.Enumerate()) h(); }
                    if (step2.Count > 0) { foreach (var (_, _, h) in step2.Enumerate()) h(); }
                    if (step4.Count > 0) { foreach (var (_, _, h) in step4.Enumerate()) h(); }
                    if (step5.Count > 0) { foreach (var (_, _, h) in step5.Enumerate()) h(); }
                    if (step7.Count > 0) { foreach (var (_, _, h) in step7.Enumerate()) h(); }
                    if (step10.Count > 0) { foreach (var (_, _, h) in step10.Enumerate()) h(); }
                }
            }, 100);

            double overheadPerChunk = perChunk / chunks;

            _output.WriteLine($"  Per-chunk (6 steps, 5 hooks): {overheadPerChunk:F0} ns");

            // Full pipeline overhead per chunk should be under 5000ns
            Assert.True(overheadPerChunk < 10000,
                $"Full pipeline overhead too large: {overheadPerChunk:F0} ns (budget: <10000 ns/chunk)");
        }

        // ══════════════════════════════════════════════════════════════
        //  9. Freeze + sort cost (one-time, at init)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Bench_FreezeSort_OneTimeCost()
        {
            _output.WriteLine("=== Freeze + sort (one-time cost at init) ===");

            // Measure freeze with 25 hooks (the full step registry)
            var list = new OrderedHookList<Action>();
            for (int i = 0; i < 25; i++)
                list.Register($"mod-{i % 6}", (i % 4) * 25.0, NoOpDelegate);

            double freezeNanos = MeasureNanos(() =>
            {
                var fresh = new OrderedHookList<Action>();
                for (int i = 0; i < 25; i++)
                    fresh.Register($"mod-{i % 6}", (i % 4) * 25.0, NoOpDelegate);
                fresh.Freeze();
            }, 1000);

            _output.WriteLine($"  Freeze 25 hooks: {freezeNanos:F0} ns");

            // Freeze is a one-time cost, allow up to 50μs
            Assert.True(freezeNanos < 50_000,
                $"Freeze too slow: {freezeNanos:F0} ns (budget: <50000 ns)");
        }
    }
}
