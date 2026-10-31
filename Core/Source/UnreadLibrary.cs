using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// What the reference search made of the library a class comes from, for a class ILens did not
/// read: the library was not loaded, the file found under its name holds another assembly, or
/// the library was loaded and does not hold the class. An answer that leaves something out for
/// want of the class says which, since each calls for something else: a folder to add, a file
/// that is not what its name says, or another build of the library.
/// </summary>
/// <remarks>
/// <para>
/// A reference finds its library as ILSpy's type system finds it, among the loaded modules by
/// the assembly name inside the file. The reference search finds a file by the file's name, so
/// a file named for the library that holds another assembly is loaded and is not that library.
/// </para>
/// <para>
/// A library that forwards the class is followed to the library it forwards to, as the type
/// system follows it, so the answer names the library the class is missing in and not one that
/// only passes the name on.
/// </para>
/// </remarks>
/// <param name="Name">The library, as the reference to it names it.</param>
/// <param name="Version">The version that reference asks for.</param>
/// <param name="File">The file loaded for the library, or null when none was.</param>
/// <param name="FoundVersion">The version of the assembly in that file, when it is the library.</param>
/// <param name="Holds">The assembly that file holds, when it is another one.</param>
public sealed record UnreadLibrary(string Name, Version Version, string File, Version FoundVersion, string Holds)
{
    // A chain of forwards in valid metadata is one or two steps long; the bound ends a cycle.
    private const int MaxForwards = 8;

    /// <summary>The library as an answer names it, <c>Name, Version=…</c>.</summary>
    public string Reference => SymbolResolver.FormatAssembly(Name, Version);

    /// <summary>
    /// The libraries the reference rows of <paramref name="referencing"/> name for
    /// <paramref name="type"/>, a class that was not read, and the library its exported-type row
    /// forwards the class to, each as <paramref name="loaded"/> holds it: one unless the rows
    /// disagree, none when no row names an assembly. A library the class is found through is
    /// left out, since it is not why the class was not read: an assembly with other libraries
    /// merged into it can name one class through two libraries, and only one of them fails.
    /// </summary>
    /// <param name="loaded">
    /// The type system that holds what the reference search found, the decompiler's.
    /// </param>
    public static IReadOnlyList<UnreadLibrary> Of(ICompilation loaded, MetadataFile referencing, FullTypeName type)
    {
        var assemblies = SymbolResolver.AssembliesReferencedFor(referencing.Metadata, type).ToList();
        // A class the assembly forwards and names nowhere else has no reference row; the forward
        // is the assembly's own record of where the class went.
        if (ForwardOf(referencing, type) is { } forward && !assemblies.Contains(forward))
            assemblies.Add(forward);
        return assemblies
            .Select(assembly => Follow(loaded, assembly.Name, assembly.Version, type))
            .OfType<UnreadLibrary>()
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Why the classes of <paramref name="unread"/> were not read, as an answer says it after
    /// what it left out for want of them. Each class comes with the libraries its references
    /// name; a class without any is one no reference row attributes to an assembly.
    /// </summary>
    public static string Reason(IEnumerable<(string Type, IReadOnlyList<UnreadLibrary> Libraries)> unread)
    {
        var classes = unread.DistinctBy(unreadClass => unreadClass.Type).ToList();
        var clauses = new List<string>();

        // The libraries that were not loaded share one sentence: those the references to one
        // class disagree on are joined by "or", those of several classes by "and".
        var notLoaded = new List<string>();
        foreach (var (_, libraries) in classes)
        {
            var named = string.Join(" or ", libraries.Where(library => library.File is null).Select(library => library.Reference));
            if (named.Length > 0 && !notLoaded.Contains(named))
                notLoaded.Add(named);
        }
        var unattributed = classes.Count(unreadClass => unreadClass.Libraries.Count == 0);
        if (unattributed > 0)
            notLoaded.Add($"the assembly that defines {(unattributed == 1 ? "it" : "them")}");
        if (notLoaded.Count > 0)
            clauses.Add("ILens could not load " + string.Join(" and ", notLoaded));

        var found = classes.SelectMany(unreadClass => unreadClass.Libraries
            .Where(library => library.File is not null)
            .Select(library => (Library: library, unreadClass.Type)));
        foreach (var types in found.GroupBy(pair => pair.Library, pair => pair.Type))
        {
            var library = types.Key;
            clauses.Add(library.Holds is { } other
                ? $"ILens could not load {library.Reference}: the file found, {library.File}, holds the assembly {other}"
                : $"{library.Name} as loaded from {library.File}" +
                    (library.FoundVersion is null ? "" : $", Version={library.FoundVersion},") +
                    $" does not hold {string.Join(" or ", types)}" +
                    (library.FoundVersion is null || library.FoundVersion.Equals(library.Version)
                        ? ""
                        : $"; referenced is Version={library.Version}"));
        }
        return string.Join("; ", clauses);
    }

    /// <summary>
    /// The library named <paramref name="name"/> as <paramref name="loaded"/> holds it, or the
    /// library at the end of the forwards when it forwards <paramref name="type"/>. Null when
    /// the type system finds <paramref name="type"/> through that library.
    /// </summary>
    private static UnreadLibrary Follow(ICompilation loaded, string name, Version version, FullTypeName type)
    {
        var modules = loaded.Modules.OfType<MetadataModule>().ToList();
        if (Named(name)?.GetTypeDefinition(type) is not null)
            return null;

        for (var step = 0; ; step++)
        {
            var library = Named(name);
            if (library is null)
            {
                var namedLikeIt = modules.FirstOrDefault(module =>
                    Is(Path.GetFileNameWithoutExtension(module.MetadataFile.FileName), name));
                return new(name, version, namedLikeIt?.MetadataFile.FileName, null, namedLikeIt?.AssemblyName);
            }

            if (step == MaxForwards || ForwardOf(library.MetadataFile, type) is not { } target)
            {
                var metadata = library.MetadataFile.Metadata;
                return new(name, version, library.MetadataFile.FileName,
                    metadata.IsAssembly ? metadata.GetAssemblyDefinition().Version : null, null);
            }
            (name, version) = target;
        }

        MetadataModule Named(string assembly) => modules.FirstOrDefault(module => Is(module.AssemblyName, assembly));
        static bool Is(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The library that <paramref name="library"/> forwards <paramref name="type"/> to, or null
    /// when it forwards it nowhere. Asked for the outermost type, as the type system asks.
    /// </summary>
    private static (string Name, Version Version)? ForwardOf(MetadataFile library, FullTypeName type)
    {
        var metadata = library.Metadata;
        try
        {
            EntityHandle implementation = library.GetTypeForwarder(new FullTypeName(type.TopLevelTypeName));
            for (var depth = 0; depth <= MaxForwards && implementation.Kind == HandleKind.ExportedType && !implementation.IsNil; depth++)
                implementation = metadata.GetExportedType((ExportedTypeHandle)implementation).Implementation;
            return SymbolResolver.AssemblyOf(metadata, implementation);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
