using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tests;

/// <summary>
/// Writes an assembly whose class BrokenBase.Item derives from List&lt;int&gt;, a base class the
/// metadata names through a type specification. With <c>broken</c>, the specification's signature
/// is damaged after the assembly is saved, as a tool that guards an assembly against decompiling
/// damages one, so that the base class cannot be decoded. No compiler writes it.
/// </summary>
internal static class BrokenBaseAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write(bool broken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"BrokenBase_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("BrokenBase") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("BrokenBase");
        // Abstract and sealed, so that no constructor is added whose call to the base class's
        // constructor would name the specification as well.
        module.DefineType("BrokenBase.Item",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(List<int>)).CreateType();
        assembly.Save(path);
        if (!broken)
            return path;

        var image = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var specification = metadata.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(1));
            // A signature this short has a length of one byte, and its first byte says what kind of
            // type follows: a generic type's instance. No signature starts with Invalid.
            var first = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob)
                + MetadataTokens.GetHeapOffset(specification.Signature) + 1;
            if (metadata.GetTableRowCount(TableIndex.TypeSpec) != 1
                || image[first] != (byte)SignatureTypeCode.GenericTypeInstance)
                throw new InvalidOperationException(
                    $"The assembly has {metadata.GetTableRowCount(TableIndex.TypeSpec)} type specifications, " +
                    $"and the first starts with {image[first]:x2}, not with a generic type's instance.");
            image[first] = (byte)SignatureTypeCode.Invalid;
        }
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(params string[] paths) => BurstShapedAssembly.TryDelete(paths);
}
