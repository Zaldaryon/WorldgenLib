using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WorldgenLib.Tests
{
    internal static class TestAssemblyLoader
    {
        [ModuleInitializer]
        internal static void InstallResolver()
        {
            AssemblyLoadContext.Default.Resolving += ResolveReferenceAssembly;
        }

        public static void EnsureVSEssentialsLoaded()
        {
            string? root = FindReferenceRoot();
            if (root == null)
                throw new FileNotFoundException(
                    "The Vintage Story reference assembly directory was not found. Set VINTAGE_STORY to an extracted official Vintage Story installation.");

            string[] requiredAssemblies =
            {
                Path.Combine(root, "VintagestoryAPI.dll"),
                Path.Combine(root, "Lib", "0Harmony.dll"),
                Path.Combine(root, "Lib", "Mono.Cecil.dll"),
                Path.Combine(root, "VintagestoryLib.dll"),
                Path.Combine(root, "VintagestoryServer.dll"),
                Path.Combine(root, "Mods", "VSSurvivalMod.dll"),
                Path.Combine(root, "Mods", "VSEssentials.dll")
            };

            foreach (string path in requiredAssemblies)
            {
                string? assemblyName = Path.GetFileNameWithoutExtension(path);
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(assembly => assembly.GetName().Name == assemblyName))
                    continue;
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"The Vintage Story reference assembly '{assemblyName}' was not found.", path);

                LoadIntoDefaultContext(path);
            }
        }

        private static Assembly? ResolveReferenceAssembly(
            AssemblyLoadContext context,
            AssemblyName assemblyName)
        {
            if (assemblyName.Name == null)
                return null;

            string? root = FindReferenceRoot();
            if (root == null)
                return null;

            string[] candidatePaths =
            {
                Path.Combine(root, assemblyName.Name + ".dll"),
                Path.Combine(root, "Lib", assemblyName.Name + ".dll"),
                Path.Combine(root, "Mods", assemblyName.Name + ".dll")
            };

            string? path = candidatePaths.FirstOrDefault(File.Exists);

            return path == null ? null : LoadIntoDefaultContext(path);
        }

        private static Assembly LoadIntoDefaultContext(string path)
        {
            string assemblyName = Path.GetFileNameWithoutExtension(path);
            Assembly? alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, assemblyName,
                        StringComparison.OrdinalIgnoreCase));
            return alreadyLoaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }

        private static string? FindReferenceRoot()
        {
            string? configuredRoot = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrWhiteSpace(configuredRoot))
                return null;

            string root = Path.GetFullPath(configuredRoot);
            return Directory.Exists(root) ? root : null;
        }
    }
}
