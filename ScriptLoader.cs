using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    /// <summary>
    /// Compiles all *.cs files in autochisel/scripts/ into a SINGLE assembly using
    /// Roslyn. Multiple files can reference each other (helper classes, embedded
    /// libraries, etc.). Each public class implementing IShapeGenerator becomes
    /// an available generator.
    /// </summary>
    public static class ScriptLoader
    {
        public static List<IShapeGenerator> LoadUserGenerators(ICoreClientAPI capi)
        {
            var generators = new List<IShapeGenerator>();
            if (!Directory.Exists(ModPaths.Scripts)) return generators;

            var csFiles = Directory.GetFiles(ModPaths.Scripts, "*.cs");
            if (csFiles.Length == 0) return generators;

            // Collect metadata references from currently loaded assemblies
            var references = new List<MetadataReference>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    if (string.IsNullOrEmpty(asm.Location)) continue;
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch { }
            }

            // Parse all files as syntax trees
            var trees = new List<SyntaxTree>();
            var fileNames = new List<string>();
            foreach (var file in csFiles)
            {
                try
                {
                    string code = File.ReadAllText(file);
                    trees.Add(CSharpSyntaxTree.ParseText(code, path: file));
                    fileNames.Add(Path.GetFileName(file));
                }
                catch (Exception e)
                {
                    capi.Logger.Warning($"[AutoChisel] Failed to read script '{Path.GetFileName(file)}': {e.Message}");
                }
            }

            if (trees.Count == 0) return generators;

            // Strategy 1: compile all files as ONE assembly (allows cross-file references).
            if (TryCompileAndLoad(capi, trees, references, generators, out _))
                return generators;

            // Strategy 2: fall back to per-file compilation — one broken file doesn't kill
            // the others. This matters when user has a mix of old-API scripts and new ones.
            capi.Logger.Warning("[AutoChisel] Bulk compile failed — falling back to per-file mode.");
            for (int i = 0; i < trees.Count; i++)
            {
                var singleList = new List<SyntaxTree> { trees[i] };
                if (!TryCompileAndLoad(capi, singleList, references, generators, out string err))
                {
                    capi.Logger.Warning($"[AutoChisel] Skipping '{fileNames[i]}': {err}");
                }
            }

            return generators;
        }

        /// <summary>
        /// Compiles a set of syntax trees into a single assembly and loads any
        /// IShapeGenerator-implementing public types from it.
        /// Returns false with a short error on compile failure.
        /// </summary>
        private static bool TryCompileAndLoad(ICoreClientAPI capi, List<SyntaxTree> trees,
            List<MetadataReference> references, List<IShapeGenerator> generators, out string error)
        {
            error = null;
            string assemblyName = "UserGen_" + Guid.NewGuid().ToString("N");
            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: trees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                if (!result.Success)
                {
                    var errors = result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(5)
                        .Select(d => d.ToString())
                        .ToList();
                    error = string.Join(" | ", errors);
                    // Only log top-level errors on bulk attempt — per-file attempts log elsewhere.
                    if (trees.Count > 1)
                    {
                        capi.Logger.Warning($"[AutoChisel] Bulk compile errors ({errors.Count}):");
                        foreach (var e in errors) capi.Logger.Warning("  " + e);
                    }
                    return false;
                }

                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                foreach (var type in assembly.GetTypes())
                {
                    if (!typeof(IShapeGenerator).IsAssignableFrom(type)) continue;
                    if (type.IsAbstract || type.IsInterface) continue;

                    try
                    {
                        var gen = (IShapeGenerator)Activator.CreateInstance(type);
                        generators.Add(gen);
                        capi.Logger.Notification($"[AutoChisel] Loaded user generator: {gen.Name}");
                    }
                    catch (Exception e)
                    {
                        capi.Logger.Warning($"[AutoChisel] Cannot instantiate {type.Name}: {e.Message}");
                    }
                }
            }
            return true;
        }
    }
}
