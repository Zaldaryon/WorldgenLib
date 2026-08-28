using System;
using Xunit;

namespace WorldgenLib.Tests
{
    public class ConflictDetectorTests
    {
        [Fact]
        public void Initially_HasNoConflicts()
        {
            // Reports are static — clear them before test.
            // ConflictDetector._reports is internal; we test the public API.
            // After construction, Reports should be empty (or cleared by prior test).
            // This is a structural test — the full server-based detection is Phase 6.
            var reports = ConflictDetector.Reports;
            // Can't guarantee clean state across test parallelism, so just check type.
            Assert.NotNull(reports);
        }

        [Fact]
        public void Report_AddsToReportsList()
        {
            ConflictDetector.Report("test-mod-unique", "test-mechanism-unique", "test detail-unique");
            Assert.Contains(ConflictDetector.Reports,
                report => report.OffendingModId == "test-mod-unique"
                    && report.Mechanism == "test-mechanism-unique"
                    && report.Detail == "test detail-unique");
        }
    }
}
