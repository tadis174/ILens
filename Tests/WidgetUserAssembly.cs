using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace Tests;

/// <summary>
/// Writes an assembly built for .NET 10, or for the target given, whose public class WidgetUser.Gadget derives from
/// ExternalLib's Widget, so that list_members with inherited members lists Widget's members where
/// ILens reads ExternalLib and names the library it could not load where it does not. Its
/// reference to ExternalLib can be named by a path, such as <c>..\Outside\ExternalLib</c> or a
/// share on another computer, which no compiler writes: the reference row is bent after the
/// assembly is saved to a name the assembly holds as the name of a parameter of the internal
/// class WidgetUser.Names.
/// </summary>
internal static class WidgetUserAssembly
{
    // ExternalLib from the test output, read from a stream so that the file stays free to replace.
    private static readonly Lazy<Type> Widget = new(() =>
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "References", "ExternalLib.dll"));
        return new AssemblyLoadContext("WidgetUser-ExternalLib")
            .LoadFromStream(stream)
            .GetType("ExternalLib.Widget", throwOnError: true)!;
    });

    /// <summary>
    /// Write the assembly as <paramref name="fileName"/> into <paramref name="folder"/>, with its
    /// reference to ExternalLib named <paramref name="referenceName"/> where one is given and its
    /// TargetFrameworkAttribute naming <paramref name="targetFramework"/>, and return its path.
    /// </summary>
    public static string Write(string folder, string fileName, string? referenceName = null,
        string targetFramework = ".NETCoreApp,Version=v10.0")
    {
        var path = Path.Combine(folder, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName(name) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(TargetFrameworkAttribute).GetConstructor([typeof(string)])!, [targetFramework]));
        var module = assembly.DefineDynamicModule(name);

        var gadget = module.DefineType("WidgetUser.Gadget", TypeAttributes.Public | TypeAttributes.Class, Widget.Value);
        gadget.DefineDefaultConstructor(MethodAttributes.Public);
        gadget.CreateType();

        if (referenceName is not null)
        {
            var names = module.DefineType("WidgetUser.Names",
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
            var holder = names.DefineMethod("Hold",
                MethodAttributes.Assembly | MethodAttributes.Static, typeof(void), [typeof(int)]);
            holder.DefineParameter(1, ParameterAttributes.None, referenceName);
            holder.GetILGenerator().Emit(OpCodes.Ret);
            names.CreateType();
        }

        assembly.Save(path);
        if (referenceName is not null)
            NameTheReference(path, referenceName);
        return path;
    }

    /// <summary>
    /// Point the name of the assembly's reference to ExternalLib at <paramref name="referenceName"/>,
    /// which the assembly holds as a parameter's name.
    /// </summary>
    private static void NameTheReference(string path, string referenceName)
    {
        var image = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var reference = metadata.AssemblyReferences.Single(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "ExternalLib");
            var name = metadata.MethodDefinitions
                .SelectMany(method => metadata.GetMethodDefinition(method).GetParameters())
                .Select(metadata.GetParameter)
                .Single(parameter => metadata.GetString(parameter.Name) == referenceName)
                .Name;

            // A reference row holds four two-byte version numbers, four bytes of flags and the
            // blob index of its public key or token before the string index of its name. In an
            // assembly this small every heap index takes two bytes.
            if (metadata.GetHeapSize(HeapIndex.String) >= 1 << 16 || metadata.GetHeapSize(HeapIndex.Blob) >= 1 << 16)
                throw new InvalidOperationException("The heaps of the written assembly need four-byte indexes.");
            var offset = pe.PEHeaders.MetadataStartOffset
                + metadata.GetTableMetadataOffset(TableIndex.AssemblyRef)
                + (MetadataTokens.GetRowNumber(reference) - 1) * metadata.GetTableRowSize(TableIndex.AssemblyRef)
                + 12 + 2;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), (ushort)MetadataTokens.GetHeapOffset(name));
        }
        File.WriteAllBytes(path, image);

        using var check = new PEReader(File.OpenRead(path));
        var written = check.GetMetadataReader();
        if (!written.AssemblyReferences.Any(handle =>
                written.GetString(written.GetAssemblyReference(handle).Name) == referenceName))
            throw new InvalidOperationException($"The reference of {path} was not renamed to {referenceName}.");
    }
}
