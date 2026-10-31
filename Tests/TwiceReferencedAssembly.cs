using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Tests;

/// <summary>
/// Writes an assembly that names one class through two libraries, as an assembly with other
/// libraries merged into it does: <c>TwiceReferenced.Derived</c> derives from
/// <c>Shared.Shelf</c> through its reference to FirstLib, and a second reference row names the
/// same class in SecondLib. FirstLib, written beside it, holds no class, and SecondLib holds
/// <c>Shared.Shelf</c>. No compiler writes two such rows without the libraries merged.
/// </summary>
internal static class TwiceReferencedAssembly
{
    /// <summary>
    /// Write TwiceReferenced.dll, FirstLib.dll and SecondLib.dll into <paramref name="folder"/>
    /// and return the path of the first.
    /// </summary>
    public static string Write(string folder)
    {
        var version = new Version(1, 0, 0, 0);
        ForwardingAssembly.Write(folder, "FirstLib", version);

        var secondLib = new PersistedAssemblyBuilder(new AssemblyName("SecondLib") { Version = version }, typeof(object).Assembly);
        secondLib.DefineDynamicModule("SecondLib")
            .DefineType("Shared.Shelf", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        secondLib.Save(Path.Combine(folder, "SecondLib.dll"));

        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("TwiceReferenced.dll"), metadata.GetOrAddGuid(Guid.NewGuid()),
            default, default);
        metadata.AddAssembly(metadata.GetOrAddString("TwiceReferenced"), version, default, default, 0, AssemblyHashAlgorithm.None);
        var shelf = new[] { "FirstLib", "SecondLib" }
            .Select(library => metadata.AddTypeReference(
                metadata.AddAssemblyReference(metadata.GetOrAddString(library), version, default, default, 0, default),
                metadata.GetOrAddString("Shared"), metadata.GetOrAddString("Shelf")))
            .ToList();
        // The row every module's type table starts with, then the class, which declares nothing.
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("TwiceReferenced"), metadata.GetOrAddString("Derived"), shelf[0],
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        return ForwardingAssembly.Save(metadata, folder, "TwiceReferenced");
    }
}
