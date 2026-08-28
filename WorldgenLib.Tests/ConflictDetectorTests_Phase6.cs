using System;
using System.Reflection;
using Xunit;

namespace WorldgenLib.Tests
{
    public class ConflictDetectorPhase6Tests
    {
        [Fact]
        public void HasDetectMethod()
        {
            var method = typeof(ConflictDetector).GetMethod("Detect",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(method);
        }

        [Fact]
        public void HasReportsProperty()
        {
            var prop = typeof(ConflictDetector).GetProperty("Reports",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(prop);
        }

        [Fact]
        public void HasConflictsProperty()
        {
            var prop = typeof(ConflictDetector).GetProperty("HasConflicts",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(prop);
        }

        [Fact]
        public void ConflictReport_HasRequiredProperties()
        {
            var type = typeof(ConflictDetector.ConflictReport);
            Assert.NotNull(type.GetProperty("OffendingModId"));
            Assert.NotNull(type.GetProperty("Mechanism"));
            Assert.NotNull(type.GetProperty("Detail"));
            Assert.NotNull(type.GetProperty("DetectedAt"));
        }

        [Fact]
        public void Report_AddsToReportsList()
        {
            ConflictDetector.Report("phase6-test-mod-unique", "test-mechanism", "test detail");
            Assert.Contains(ConflictDetector.Reports,
                report => report.OffendingModId == "phase6-test-mod-unique"
                    && report.Mechanism == "test-mechanism"
                    && report.Detail == "test detail");
        }

        [Fact]
        public void StartupReport_HasPrintMethod()
        {
            var method = typeof(StartupReport).GetMethod("Print",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(method);
        }

        [Fact]
        public void OrderBands_AllConstantsExist()
        {
            Assert.Equal(-100, OrderBands.BeforeVanillaMin);
            Assert.Equal(-1, OrderBands.BeforeVanillaMax);
            Assert.Equal(0, OrderBands.Vanilla);
            Assert.Equal(1, OrderBands.AfterVanillaMin);
            Assert.Equal(100, OrderBands.AfterVanillaMax);
            Assert.Equal(1000, OrderBands.FinalOverrideMin);
        }
    }
}
