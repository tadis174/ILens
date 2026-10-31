using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tests;

/// <summary>
/// Writes an assembly that defines no class and forwards the classes named to another assembly,
/// or forwards none. Without forwards it stands for a build of a library that no longer holds
/// the classes an assembly was compiled against, with them for a library that passes a class
/// on. A fixture project cannot stand in for it: it would have to build a second DLL of a
/// fixture's name, and the compiler writes a forward only for a class it finds in the target.
/// It can also reference classes of the other assembly and define classes of its own, for a
/// library whose forwards share their names with classes it references or defines.
/// </summary>
internal static class ForwardingAssembly
{
    // TypeAttributes has no name for the flag that marks an exported-type row as a forward.
    private const TypeAttributes Forward = (TypeAttributes)0x00200000;

    /// <summary>
    /// Write the assembly <paramref name="name"/> in <paramref name="version"/>, which defines
    /// and forwards nothing, as <c>name.dll</c> into <paramref name="folder"/>, and return its
    /// path.
    /// </summary>
    public static string Write(string folder, string name, Version version) =>
        Write(folder, name, version, forwardTo: null, [], [], []);

    /// <summary>
    /// Write the assembly <paramref name="name"/> in <paramref name="version"/> as
    /// <c>name.dll</c> into <paramref name="folder"/>, and return its path. It forwards each of
    /// <paramref name="forwarded"/>, a class by namespace and name, to
    /// <paramref name="target"/>, asking for <paramref name="targetVersion"/>. A nested class is
    /// named after the classes enclosing it with '+', <c>Outer+Inner</c>, and forwarded as the
    /// compiler forwards one: its row names the row of the class enclosing it.
    /// </summary>
    public static string Write(string folder, string name, Version version,
        string target, Version targetVersion, params (string Namespace, string Name)[] forwarded) =>
        Write(folder, name, version, (target, targetVersion), forwarded, [], []);

    /// <summary>
    /// Write the assembly as the overload above does, where it also references each of
    /// <paramref name="referenced"/> in <paramref name="target"/>, a class it forwards included,
    /// and defines each of <paramref name="defined"/> as a public class that derives from nothing
    /// and declares nothing. A generic class is named as metadata names it, with its count of type
    /// parameters after a backtick, <c>Pair`2</c>, and a defined one has a row for each type
    /// parameter.
    /// </summary>
    public static string Write(string folder, string name, Version version, string target, Version targetVersion,
        (string Namespace, string Name)[] forwarded, (string Namespace, string Name)[] referenced,
        (string Namespace, string Name)[] defined) =>
        Write(folder, name, version, (target, targetVersion), forwarded, referenced, defined);

    private static string Write(string folder, string name, Version version,
        (string Name, Version Version)? forwardTo, (string Namespace, string Name)[] forwarded,
        (string Namespace, string Name)[] referenced, (string Namespace, string Name)[] defined)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()),
            default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), version, default, default, 0, AssemblyHashAlgorithm.None);
        // The row every module's type table starts with.
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        foreach (var (typeNamespace, typeName) in defined)
        {
            var type = metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class,
                metadata.GetOrAddString(typeNamespace), metadata.GetOrAddString(typeName), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            int tick = typeName.IndexOf('`');
            int count = tick < 0 ? 0 : int.Parse(typeName[(tick + 1)..]);
            for (int index = 0; index < count; index++)
                metadata.AddGenericParameter(type, GenericParameterAttributes.None,
                    metadata.GetOrAddString($"T{index + 1}"), index);
        }
        if (forwardTo is var (target, targetVersion))
        {
            var reference = metadata.AddAssemblyReference(
                metadata.GetOrAddString(target), targetVersion, default, default, 0, default);
            foreach (var (typeNamespace, typeName) in referenced)
                metadata.AddTypeReference(reference, metadata.GetOrAddString(typeNamespace), metadata.GetOrAddString(typeName));
            var rows = new Dictionary<string, ExportedTypeHandle>();
            foreach (var (typeNamespace, typeName) in forwarded)
            {
                // A row for each class enclosing a nested class, written once.
                EntityHandle implementation = reference;
                string[] segments = typeName.Split('+');
                for (int depth = 0; depth < segments.Length; depth++)
                {
                    string key = $"{typeNamespace}.{string.Join("+", segments.Take(depth + 1))}";
                    if (!rows.TryGetValue(key, out var row))
                        rows[key] = row = depth == 0
                            ? metadata.AddExportedType(Forward, metadata.GetOrAddString(typeNamespace),
                                metadata.GetOrAddString(segments[0]), implementation, 0)
                            : metadata.AddExportedType(TypeAttributes.NestedPublic, default,
                                metadata.GetOrAddString(segments[depth]), implementation, 0);
                    implementation = row;
                }
            }
        }

        return Save(metadata, folder, name);
    }

    /// <summary>
    /// Write <paramref name="metadata"/>, which holds no method body, as <c>name.dll</c> into
    /// <paramref name="folder"/>, and return its path.
    /// </summary>
    internal static string Save(MetadataBuilder metadata, string folder, string name)
    {
        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder())
            .Serialize(image);
        string path = Path.Combine(folder, name + ".dll");
        using var file = File.Create(path);
        image.WriteContentTo(file);
        return path;
    }
}
