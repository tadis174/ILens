using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Tests;

/// <summary>
/// Writes an assembly whose reference to a type names an assembly row past the end of its
/// assembly table, the shape some builds of Harmony's own DLL have from 2.3 on, 2.4.1's among
/// them, where the libraries merged into it leave such references behind. No compiler writes it,
/// so the reference is bent after the assembly is saved. The class DanglingReference.Holder has two methods: UsesBuilder takes a
/// StringBuilder, whose reference names its assembly, and UsesUri takes a Uri, whose reference
/// is the bent one.
/// </summary>
internal static class DanglingReferenceAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"DanglingReference_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("DanglingReference") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("DanglingReference");
        var holder = module.DefineType("DanglingReference.Holder",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        foreach (var (name, parameter) in new[] { ("UsesBuilder", typeof(StringBuilder)), ("UsesUri", typeof(Uri)) })
        {
            var method = holder.DefineMethod(name,
                MethodAttributes.Public | MethodAttributes.Static, typeof(void), [parameter]);
            method.GetILGenerator().Emit(OpCodes.Ret);
        }
        holder.CreateType();
        assembly.Save(path);

        var image = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var uri = metadata.TypeReferences.Single(handle =>
                metadata.GetString(metadata.GetTypeReference(handle).Name) == nameof(Uri));
            // A type reference row starts with its scope, which in an assembly this small is a
            // two-byte coded index: the row number above two tag bits, tag 2 for an assembly row.
            var offset = pe.PEHeaders.MetadataStartOffset
                + metadata.GetTableMetadataOffset(TableIndex.TypeRef)
                + (MetadataTokens.GetRowNumber(uri) - 1) * metadata.GetTableRowSize(TableIndex.TypeRef);
            var pastTheTable = metadata.GetTableRowCount(TableIndex.AssemblyRef) + 4;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), (ushort)(pastTheTable << 2 | 2));
        }
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
