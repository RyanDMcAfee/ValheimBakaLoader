using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The bundled BepInEx plugins are compiled against the game's own assemblies and shipped as
    /// binaries, so a game update can leave them naming members that no longer exist. Nothing in
    /// the plugin's own source changes, the DLL still loads, and the failure only arrives when
    /// Mono JITs the method that holds the stale reference - which is the CALLER, not the method
    /// with the try/catch, so the whole command dies with it.
    ///
    /// That is exactly what Valheim 1.0.12 did: PlayerProfile.s_bypassCheatChecks went from a
    /// static FIELD to a static PROPERTY of the same name and the same type. Every check that
    /// asks "does this name still exist on this type" waves that through. This one does not: a
    /// field reference has to resolve to a FIELD, and a property wearing the same name is a
    /// failure, because that is the bug.
    ///
    /// It reads the shipped DLLs with System.Reflection.Metadata, which is in the framework, so
    /// the gate needs no package and no copy of Mono.Cecil. A machine without the dedicated
    /// server installed has nothing to resolve against, and the test says so and passes rather
    /// than failing somebody's checkout.
    /// </summary>
    public class BundledPluginCompatibilityTests
    {
        private readonly ITestOutputHelper _out;

        public BundledPluginCompatibilityTests(ITestOutputHelper output) => _out = output;

        /// <summary>The assemblies whose members the plugins are compiled against and pinned to.</summary>
        private static readonly string[] GameAssemblies = { "assembly_valheim", "assembly_utils" };

        private static readonly string[] ManagedProbePaths =
        {
            @"D:\steamlibrary\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed",
            @"C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed",
        };

        [Fact]
        public void EveryBundledPluginStillFitsTheInstalledGame()
        {
            var managed = FindManagedDir();
            if (managed == null)
            {
                // No game on this machine, so there is nothing to resolve against. Say so out
                // loud: a gate that checked nothing must never look like a gate that passed.
                _out.WriteLine(
                    "SKIPPED: no Valheim dedicated server Managed folder found. Set " +
                    "BAKALOADER_VALHEIM_MANAGED to one, or install the dedicated server, to run " +
                    "this check.");
                return;
            }

            _out.WriteLine("Managed folder: " + managed);

            var game = new GameIndex(managed, GameAssemblies);
            Assert.True(game.TypeCount > 0,
                "read no types out of " + managed + ", so nothing could be resolved");

            var plugins = BundledPluginDlls().ToList();
            Assert.True(plugins.Count > 0, "found no bundled plugin DLLs to check");

            var unresolved = new List<string>();

            foreach (var dll in plugins)
            {
                var problems = UnresolvedReferences(dll, game, out var checkedRefs);
                _out.WriteLine(string.Format(
                    "{0}: {1} reference(s) into {2}, {3} unresolved",
                    Path.GetFileName(dll), checkedRefs, string.Join(" / ", GameAssemblies), problems.Count));

                foreach (var p in problems)
                {
                    _out.WriteLine("    UNRESOLVED  " + p);
                    unresolved.Add(Path.GetFileName(dll) + ": " + p);
                }
            }

            Assert.True(unresolved.Count == 0,
                "the bundled plugins name members the installed game no longer has:" +
                Environment.NewLine + string.Join(Environment.NewLine, unresolved));
        }

        // ------------------------------------------------------------------
        //  Where things are
        // ------------------------------------------------------------------

        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? ".");
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ValheimBakaLoader.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find ValheimBakaLoader.sln above " + here);
            return dir.FullName;
        }

        /// <summary>
        /// Every DLL bundled under Resources, which is all five: Commander, KillAll, MaxPlayers,
        /// SpawnHelper and the item indexer (whose source lives in its own folder but whose
        /// shipped binary lands in Resources\ItemIndexer with the rest).
        /// </summary>
        private static IEnumerable<string> BundledPluginDlls()
        {
            var resources = Path.Combine(RepoRoot(), "ValheimBakaLoader", "Resources");
            if (!Directory.Exists(resources)) yield break;

            foreach (var dll in Directory.EnumerateFiles(resources, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                yield return dll;
        }

        private static string FindManagedDir()
        {
            var fromEnv = Environment.GetEnvironmentVariable("BAKALOADER_VALHEIM_MANAGED");
            if (!string.IsNullOrWhiteSpace(fromEnv) && IsManagedDir(fromEnv)) return fromEnv;

            return ManagedProbePaths.FirstOrDefault(IsManagedDir);
        }

        private static bool IsManagedDir(string dir)
        {
            try { return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "assembly_valheim.dll")); }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  The plugin side: every member reference into the game
        // ------------------------------------------------------------------

        private static List<string> UnresolvedReferences(string dll, GameIndex game, out int checkedRefs)
        {
            checkedRefs = 0;
            var problems = new List<string>();

            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();

            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;

                var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                var assembly = ReferencedAssemblyName(reader, parent);
                if (assembly == null || !GameAssemblies.Contains(assembly, StringComparer.OrdinalIgnoreCase))
                    continue;

                checkedRefs++;

                var typeName = TypeReferenceName(reader, parent);
                var memberName = reader.GetString(member.Name);
                var signature = reader.GetBlobBytes(member.Signature);
                var kind = SignatureKind(signature);

                var found = kind == MemberKind.Field
                    ? game.HasField(typeName, memberName)
                    : game.HasMethod(typeName, memberName, ParameterCount(signature));

                if (!found)
                {
                    problems.Add(string.Format("{0} -> {1}::{2} ({3})",
                        assembly, typeName, memberName, kind == MemberKind.Field ? "field" : "method"));
                }
            }

            return problems;
        }

        private enum MemberKind { Field, Method }

        /// <summary>
        /// The first byte of a signature blob carries the calling convention, and 0x06 in its low
        /// nibble is the one that means FIELD. Everything else is a method of some shape.
        /// </summary>
        private static MemberKind SignatureKind(byte[] signature)
        {
            if (signature.Length == 0) return MemberKind.Method;
            return (signature[0] & 0x0F) == 0x06 ? MemberKind.Field : MemberKind.Method;
        }

        /// <summary>
        /// Parameter count out of a method signature blob: the calling-convention byte, then a
        /// compressed generic-parameter count when the generic bit (0x10) is set, then the
        /// compressed parameter count. Returns -1 when it cannot be read, which makes the match
        /// fall back to the name alone rather than inventing a failure.
        /// </summary>
        private static int ParameterCount(byte[] signature)
        {
            if (signature.Length < 2) return -1;

            var offset = 0;
            var convention = signature[offset++];
            if ((convention & 0x10) != 0 && !TryReadCompressed(signature, ref offset, out _)) return -1;
            return TryReadCompressed(signature, ref offset, out var count) ? count : -1;
        }

        /// <summary>ECMA-335 compressed unsigned integer: 1, 2 or 4 bytes, flagged by the top bits.</summary>
        private static bool TryReadCompressed(byte[] bytes, ref int offset, out int value)
        {
            value = 0;
            if (offset >= bytes.Length) return false;

            var first = bytes[offset];
            if ((first & 0x80) == 0)
            {
                value = first;
                offset += 1;
                return true;
            }
            if ((first & 0xC0) == 0x80)
            {
                if (offset + 1 >= bytes.Length) return false;
                value = ((first & 0x3F) << 8) | bytes[offset + 1];
                offset += 2;
                return true;
            }
            if ((first & 0xE0) == 0xC0)
            {
                if (offset + 3 >= bytes.Length) return false;
                value = ((first & 0x1F) << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                offset += 4;
                return true;
            }
            return false;
        }

        /// <summary>The assembly a type reference lives in, walking out through nested types.</summary>
        private static string ReferencedAssemblyName(MetadataReader reader, TypeReference type)
        {
            for (var depth = 0; depth < 16; depth++)
            {
                switch (type.ResolutionScope.Kind)
                {
                    case HandleKind.AssemblyReference:
                        var asm = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
                        return reader.GetString(asm.Name);
                    case HandleKind.TypeReference:
                        type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
                        continue;
                    default:
                        return null;
                }
            }
            return null;
        }

        /// <summary>
        /// A type reference's name, nested types joined with "/" and namespaced where it has one,
        /// spelled the same way <see cref="GameIndex"/> spells the definitions it indexes.
        /// </summary>
        private static string TypeReferenceName(MetadataReader reader, TypeReference type)
        {
            var parts = new List<string>();
            for (var depth = 0; depth < 16; depth++)
            {
                var name = reader.GetString(type.Name);
                var ns = reader.GetString(type.Namespace);
                parts.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);

                if (type.ResolutionScope.Kind != HandleKind.TypeReference) break;
                type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------
        //  The game side: what the installed assemblies actually declare
        // ------------------------------------------------------------------

        /// <summary>
        /// Every type the given game assemblies declare, with its fields and its methods by
        /// parameter count, and the name of its base type so an inherited member resolves the way
        /// the runtime resolves it. Properties are deliberately NOT indexed as fields: a field
        /// reference that now names a property is the failure this whole file exists for.
        /// </summary>
        private sealed class GameIndex
        {
            private sealed class TypeInfo
            {
                public readonly HashSet<string> Fields = new(StringComparer.Ordinal);
                public readonly Dictionary<string, HashSet<int>> Methods = new(StringComparer.Ordinal);
                public string BaseType;
            }

            private readonly Dictionary<string, TypeInfo> _types = new(StringComparer.Ordinal);

            public int TypeCount => _types.Count;

            public GameIndex(string managedDir, IEnumerable<string> assemblyNames)
            {
                foreach (var name in assemblyNames)
                {
                    var path = Path.Combine(managedDir, name + ".dll");
                    if (File.Exists(path)) Read(path);
                }
            }

            private void Read(string path)
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return;
                var reader = pe.GetMetadataReader();

                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    var full = DefinitionName(reader, type);
                    if (full == null) continue;

                    if (!_types.TryGetValue(full, out var info))
                        _types[full] = info = new TypeInfo();

                    foreach (var f in type.GetFields())
                        info.Fields.Add(reader.GetString(reader.GetFieldDefinition(f).Name));

                    foreach (var m in type.GetMethods())
                    {
                        var method = reader.GetMethodDefinition(m);
                        var name = reader.GetString(method.Name);
                        var count = ParameterCount(reader.GetBlobBytes(method.Signature));
                        if (!info.Methods.TryGetValue(name, out var counts))
                            info.Methods[name] = counts = new HashSet<int>();
                        counts.Add(count);
                    }

                    info.BaseType ??= BaseTypeName(reader, type);
                }
            }

            private static string DefinitionName(MetadataReader reader, TypeDefinition type)
            {
                var parts = new List<string>();
                for (var depth = 0; depth < 16; depth++)
                {
                    var name = reader.GetString(type.Name);
                    var ns = reader.GetString(type.Namespace);
                    parts.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);

                    if (!type.IsNested) break;
                    var declaring = type.GetDeclaringType();
                    if (declaring.IsNil) break;
                    type = reader.GetTypeDefinition(declaring);
                }
                parts.Reverse();
                return string.Join("/", parts);
            }

            private static string BaseTypeName(MetadataReader reader, TypeDefinition type)
            {
                if (type.BaseType.IsNil) return null;

                switch (type.BaseType.Kind)
                {
                    case HandleKind.TypeDefinition:
                        return DefinitionName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type.BaseType));
                    case HandleKind.TypeReference:
                        return TypeReferenceName(reader, reader.GetTypeReference((TypeReferenceHandle)type.BaseType));
                    default:
                        return null; // a generic instantiation; the walk stops rather than guessing
                }
            }

            /// <summary>A FIELD of that name on that type or one it inherits from. Properties do not count.</summary>
            public bool HasField(string typeName, string fieldName) =>
                Walk(typeName, info => info.Fields.Contains(fieldName));

            /// <summary>A method of that name and arity on that type or one it inherits from.</summary>
            public bool HasMethod(string typeName, string methodName, int parameterCount) =>
                Walk(typeName, info =>
                    info.Methods.TryGetValue(methodName, out var counts) &&
                    (parameterCount < 0 || counts.Contains(parameterCount)));

            private bool Walk(string typeName, Func<TypeInfo, bool> match)
            {
                var name = typeName;
                for (var depth = 0; depth < 16 && name != null; depth++)
                {
                    if (!_types.TryGetValue(name, out var info)) return false;
                    if (match(info)) return true;
                    name = info.BaseType;
                }
                return false;
            }
        }
    }
}
