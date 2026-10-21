using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// <see cref="PluginGuidReader"/> and the GUID half of <see cref="ConfigFiles.MatchForMod"/>.
    /// The Mods page paired a mod with its configs by name alone, and missed every mod whose
    /// BepInPlugin GUID reads nothing like its package (MySix_ForgeOfPotential writes
    /// com.mysix.forgeofpotential.cfg). The DLLs here are real ones, built in memory, so the
    /// reader is proved against the same metadata a compiler writes.
    /// </summary>
    public class PluginGuidReaderTests
    {
        private const string Guid = "com.mysix.forgeofpotential";

        // ------------------------------------------------------------------ the reader

        [Fact]
        public void A_plugin_class_carrying_BepInPlugin_gives_its_guid()
        {
            var dll = BuildAssembly(attributeInSameAssembly: false, (Guid, "ForgePlugin"));
            Assert.Equal(new[] { Guid }, PluginGuidReader.Read(dll));
        }

        [Fact]
        public void Every_plugin_in_one_dll_is_read()
        {
            var dll = BuildAssembly(attributeInSameAssembly: false, (Guid, "ForgePlugin"), ("com.mysix.other", "OtherPlugin"));
            Assert.Equal(new[] { Guid, "com.mysix.other" }, PluginGuidReader.Read(dll));
        }

        [Fact]
        public void An_attribute_class_defined_in_the_same_dll_is_read_too()
        {
            var dll = BuildAssembly(attributeInSameAssembly: true, (Guid, "ForgePlugin"));
            Assert.Equal(new[] { Guid }, PluginGuidReader.Read(dll));
        }

        [Fact]
        public void A_dll_with_no_plugin_gives_nothing()
        {
            Assert.Empty(PluginGuidReader.Read(BuildAssembly(attributeInSameAssembly: false)));
        }

        [Fact]
        public void Garbage_bytes_give_nothing_and_do_not_throw()
        {
            var junk = new byte[4096];
            new Random(7).NextBytes(junk);
            Assert.Empty(PluginGuidReader.Read(junk));
            Assert.Empty(PluginGuidReader.Read(new byte[] { (byte)'M', (byte)'Z', 0, 0 }));
            Assert.Empty(PluginGuidReader.Read(Array.Empty<byte>()));
            Assert.Empty(PluginGuidReader.Read((byte[])null));
        }

        [Fact]
        public void A_folder_is_read_with_switched_off_dlls_and_a_rewrite_is_seen()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vbl-guid-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllBytes(Path.Combine(dir, "sub", "Forge.dll.old"), BuildAssembly(false, (Guid, "ForgePlugin")));
                File.WriteAllBytes(Path.Combine(dir, "readme.txt"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(dir, "Broken.dll"), new byte[] { 1, 2, 3 });
                Assert.Equal(new[] { Guid }, PluginGuidReader.ReadFolders(dir, null, Path.Combine(dir, "missing")));

                var path = Path.Combine(dir, "sub", "Forge.dll.old");
                File.WriteAllBytes(path, BuildAssembly(false, ("com.mysix.renamed", "ForgePlugin"), ("com.mysix.second", "Second")));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                Assert.Equal(new[] { "com.mysix.renamed", "com.mysix.second" }, PluginGuidReader.ReadFolders(dir));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ the matcher

        private static readonly string[] Files =
        {
            "com.mysix.forgeofpotential.cfg",
            "COM.MySix.ForgeOfPotential.extra.cfg",
            "com.mysix.forgeofpotential/recipes.yaml",
            "com.mysix.forgeofpotentialplus.cfg",
            "com.mysix.forgeofpotential.yaml",
            "unrelated.cfg",
            "elsewhere/com.mysix.forgeofpotential.json",
        };

        [Fact]
        public void A_guid_pairs_its_cfg_its_extra_cfgs_and_its_folder()
        {
            var files = ConfigFiles.MatchForMod(Files, "MySix_ForgeOfPotential", "MysixValheimMods", null,
                new[] { "MysixValheimMods-MySix_ForgeOfPotential" }, new[] { Guid });
            Assert.Equal(new[]
            {
                "com.mysix.forgeofpotential/recipes.yaml",
                "com.mysix.forgeofpotential.cfg",
                "COM.MySix.ForgeOfPotential.extra.cfg",
            }, files);
        }

        [Fact]
        public void Without_the_guid_the_names_alone_miss_that_mod()
        {
            // The bug itself: the dots in the GUID keep the squashed name from ever matching.
            Assert.Empty(ConfigFiles.MatchForMod(Files, "MySix_ForgeOfPotential", "MysixValheimMods", null,
                new[] { "MysixValheimMods-MySix_ForgeOfPotential" }));
        }

        [Fact]
        public void The_name_fallback_still_works_beside_a_guid()
        {
            var files = new[] { "shudnal.ExtraSlots.cfg", "extra_slots.yaml", "Other.cfg", "com.x.slots.cfg" };
            Assert.Equal(new[] { "com.x.slots.cfg", "extra_slots.yaml", "shudnal.ExtraSlots.cfg" },
                ConfigFiles.MatchForMod(files, "Extra Slots", "shudnal", null, null, new[] { "com.x.slots" }));
        }

        // ------------------------------------------------------------------ building a dll

        /// <summary>
        /// A tiny class library with one class per plugin, each carrying
        /// [BepInEx.BepInPlugin(guid, name, "1.0.0")]. The attribute's class is a reference into
        /// BepInEx.dll the way a real mod has it, or, with <paramref name="attributeInSameAssembly"/>,
        /// defined in the library itself.
        /// </summary>
        private static byte[] BuildAssembly(bool attributeInSameAssembly, params (string Guid, string TypeName)[] plugins)
        {
            var md = new MetadataBuilder();
            md.AddModule(0, md.GetOrAddString("Forge.dll"), md.GetOrAddGuid(System.Guid.NewGuid()), default, default);
            md.AddAssembly(md.GetOrAddString("Forge"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);

            var corlib = md.AddAssemblyReference(md.GetOrAddString("System.Runtime"), new Version(6, 0, 0, 0),
                default, default, 0, default);
            var objectRef = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Object"));
            var attributeRef = md.AddTypeReference(corlib, md.GetOrAddString("System"), md.GetOrAddString("Attribute"));

            var ctorSig = new BlobBuilder();
            new BlobEncoder(ctorSig).MethodSignature(isInstanceMethod: true).Parameters(3,
                r => r.Void(),
                p => { p.AddParameter().Type().String(); p.AddParameter().Type().String(); p.AddParameter().Type().String(); });
            var ctorSigBlob = md.GetOrAddBlob(ctorSig);

            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

            EntityHandle ctor;
            if (attributeInSameAssembly)
            {
                ctor = md.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, md.GetOrAddString(".ctor"), ctorSigBlob, -1, default);
                md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, md.GetOrAddString("BepInEx"),
                    md.GetOrAddString("BepInPlugin"), attributeRef,
                    MetadataTokens.FieldDefinitionHandle(1), (MethodDefinitionHandle)ctor);
            }
            else
            {
                var bepinex = md.AddAssemblyReference(md.GetOrAddString("BepInEx"), new Version(5, 4, 0, 0),
                    default, default, 0, default);
                var pluginAttr = md.AddTypeReference(bepinex, md.GetOrAddString("BepInEx"), md.GetOrAddString("BepInPlugin"));
                ctor = md.AddMemberReference(pluginAttr, md.GetOrAddString(".ctor"), ctorSigBlob);
            }

            foreach (var (guid, typeName) in plugins)
            {
                var type = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, md.GetOrAddString("Forge"),
                    md.GetOrAddString(typeName), objectRef,
                    MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(md.GetRowCount(TableIndex.MethodDef) + 1));

                var value = new BlobBuilder();
                new BlobEncoder(value).CustomAttributeSignature(
                    fixedArgs =>
                    {
                        fixedArgs.AddArgument().Scalar().Constant(guid);
                        fixedArgs.AddArgument().Scalar().Constant(typeName);
                        fixedArgs.AddArgument().Scalar().Constant("1.0.0");
                    },
                    named => named.Count(0));
                md.AddCustomAttribute(type, ctor, md.GetOrAddBlob(value));
            }

            var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
                new MetadataRootBuilder(md), new BlobBuilder());
            var image = new BlobBuilder();
            pe.Serialize(image);
            return image.ToArray();
        }
    }
}
