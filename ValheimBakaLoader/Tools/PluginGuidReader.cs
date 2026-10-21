using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The GUIDs a mod's DLL gives its plugins in <c>[BepInPlugin("guid", "name", "version")]</c>.
    /// BepInEx names a plugin's config file after that GUID, and the GUID often looks nothing
    /// like the package's name ("MySix_ForgeOfPotential" writes "com.mysix.forgeofpotential.cfg"),
    /// so this is the only reliable way to tie a config file back to its mod.
    /// <para>
    /// The DLL is read as metadata and never loaded: loading a mod into the host would run its
    /// static constructors and pin the file so it could not be updated or removed.
    /// </para>
    /// </summary>
    public static class PluginGuidReader
    {
        private const string AttributeNamespace = "BepInEx";
        private const string AttributeName = "BepInPlugin";

        // The scan runs on almost every mods call, and a big mod's DLL is tens of megabytes,
        // so a file is read again only when it has changed on disk.
        private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, string[] Guids)> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every plugin GUID declared by the DLLs in the given folders and below, switched off
        /// ones (<c>*.dll.old</c>) included so a mod that is off still owns its configs. Missing
        /// or null folders count for nothing. Never throws.
        /// </summary>
        public static string[] ReadFolders(params string[] folders)
        {
            var guids = new List<string>();
            foreach (var folder in folders ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            && !file.EndsWith(".dll" + ModSwitch.OffSuffix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (var guid in ReadFileCached(file))
                            if (!guids.Contains(guid, StringComparer.OrdinalIgnoreCase)) guids.Add(guid);
                    }
                }
                catch
                {
                    // A folder that cannot be walked just gives no GUIDs; the name matching
                    // still pairs what it can.
                }
            }
            return guids.ToArray();
        }

        /// <summary>The GUIDs in one DLL on disk, from the cache when the file is unchanged. Never throws.</summary>
        public static string[] ReadFileCached(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return Array.Empty<string>();
                var written = info.LastWriteTimeUtc;
                var length = info.Length;
                if (Cache.TryGetValue(info.FullName, out var hit) && hit.Written == written && hit.Length == length)
                    return hit.Guids;

                string[] guids;
                using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                    guids = Read(stream);
                Cache[info.FullName] = (written, length, guids);
                return guids;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>The GUIDs in a DLL held in memory. Never throws.</summary>
        public static string[] Read(byte[] image)
        {
            if (image == null || image.Length == 0) return Array.Empty<string>();
            using var stream = new MemoryStream(image, writable: false);
            return Read(stream);
        }

        /// <summary>
        /// The GUIDs in a DLL read from <paramref name="stream"/>, in the order its types are
        /// declared. Empty for anything that is not a .NET assembly or holds no plugin. Never throws.
        /// </summary>
        public static string[] Read(Stream stream)
        {
            var guids = new List<string>();
            try
            {
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata) return Array.Empty<string>();
                var md = pe.GetMetadataReader();

                foreach (var typeHandle in md.TypeDefinitions)
                {
                    foreach (var attrHandle in md.GetTypeDefinition(typeHandle).GetCustomAttributes())
                    {
                        try
                        {
                            var attr = md.GetCustomAttribute(attrHandle);
                            if (!IsBepInPlugin(md, attr.Constructor)) continue;
                            var guid = FirstStringArgument(md, attr.Value);
                            if (!string.IsNullOrWhiteSpace(guid) && !guids.Contains(guid)) guids.Add(guid);
                        }
                        catch
                        {
                            // One odd attribute should not cost the rest of the file.
                        }
                    }
                }
            }
            catch
            {
                // Not a PE file, or metadata too broken to walk: no plugins we can name.
            }
            return guids.ToArray();
        }

        // The constructor is a MemberReference into BepInEx.dll for every real mod, and a
        // MethodDefinition only when the attribute class lives in the same assembly (BepInEx
        // itself, or a test). Both are honoured so neither case surprises.
        private static bool IsBepInPlugin(MetadataReader md, EntityHandle ctor)
        {
            switch (ctor.Kind)
            {
                case HandleKind.MemberReference:
                    var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                    if (parent.Kind != HandleKind.TypeReference) return false;
                    var typeRef = md.GetTypeReference((TypeReferenceHandle)parent);
                    return md.StringComparer.Equals(typeRef.Name, AttributeName)
                        && md.StringComparer.Equals(typeRef.Namespace, AttributeNamespace);
                case HandleKind.MethodDefinition:
                    var typeDef = md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType());
                    return md.StringComparer.Equals(typeDef.Name, AttributeName)
                        && md.StringComparer.Equals(typeDef.Namespace, AttributeNamespace);
                default:
                    return false;
            }
        }

        // A custom attribute blob is a 0x0001 prolog and then the fixed arguments in order;
        // the GUID is the first, a SerString, so there is no need to decode the signature.
        private static string FirstStringArgument(MetadataReader md, BlobHandle value)
        {
            if (value.IsNil) return null;
            var blob = md.GetBlobReader(value);
            if (blob.Length < 2 || blob.ReadUInt16() != 0x0001) return null;
            return blob.ReadSerializedString();
        }
    }
}
