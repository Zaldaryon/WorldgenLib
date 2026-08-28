using System;
using System.Reflection;
using Xunit;

namespace WorldgenLib.Tests
{
    public class GenTerraPostProcessHostTests
    {
        [Fact]
        public void HasBFSFields()
        {
            var type = typeof(GenTerraPostProcessHost);
            Assert.NotNull(type.GetField("_chunkVisitedNodes",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(type.GetField("_solidNodes",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(type.GetField("_bfsQueue",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(type.GetField("_currentVisited",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        [Fact]
        public void HasRegistrationMethods()
        {
            var type = typeof(GenTerraPostProcessHost);
            Assert.NotNull(type.GetMethod("RegisterOptOut"));
            Assert.NotNull(type.GetMethod("RegisterCleanupRule"));
        }

        [Fact]
        public void HasStartServerSideMethod()
        {
            var type = typeof(GenTerraPostProcessHost);
            Assert.NotNull(type.GetMethod("StartServerSide"));
        }

        [Fact]
        public void DelegateTypes_Exist()
        {
            var assembly = typeof(GenTerraPostProcessHost).Assembly;
            Assert.NotNull(assembly.GetType("WorldgenLib.GenTerraPostProcessHost+ChunkPostProcessHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.GenTerraPostProcessHost+CleanupRuleHook"));
        }
    }
}
