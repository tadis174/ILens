using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// The assembly or the module as an analyze result: what carries an attribute written
/// <c>[assembly: …]</c> or <c>[module: …]</c>, which belongs to no type or member. Printed
/// with the word C# writes that target with and the name its metadata row holds, as in
/// <c>assembly Tools</c> and <c>module Tools.dll</c>.
/// </summary>
/// <remarks>
/// ILSpy's type system has one symbol for both, the module, and the ILSpyX analyzers answer an
/// attribute on either with it, so their answer does not say which of the two carries the
/// attribute. The two places are read instead: by <see cref="AttributeCarriers.Of"/> for who
/// uses a type, as every other attribute is, and for where an attribute of a type the assembly
/// defines is applied by <see cref="Carrying"/>, with that symbol dropped from the analyzer's
/// answer.
/// </remarks>
public sealed class GlobalAttributeTarget : ISymbol
{
    private GlobalAttributeTarget(string keyword, string name)
    {
        Keyword = keyword;
        Name = name;
    }

    /// <summary>The word C# writes the target with: <c>assembly</c> or <c>module</c>.</summary>
    public string Keyword { get; }

    /// <summary>The assembly's or the module's name, as its metadata row holds it.</summary>
    public string Name { get; }

    public SymbolKind SymbolKind => SymbolKind.Module;

    /// <summary>The target an attribute's parent row stands for, or null for any other row.</summary>
    public static GlobalAttributeTarget Of(MetadataReader metadata, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.AssemblyDefinition => new("assembly", metadata.GetString(metadata.GetAssemblyDefinition().Name)),
        HandleKind.ModuleDefinition => new("module", metadata.GetString(metadata.GetModuleDefinition().Name)),
        _ => null,
    };

    /// <summary>
    /// The assembly and the module, each when it carries an attribute of <paramref name="type"/>,
    /// a type <paramref name="module"/> defines, or of a construction of it.
    /// </summary>
    public static IReadOnlyList<ISymbol> Carrying(MetadataModule module, ITypeDefinition type)
    {
        if (type.ParentModule != module)
            return [];

        var metadata = module.MetadataFile.Metadata;
        // A module that is no assembly has no assembly row.
        EntityHandle[] parents = metadata.IsAssembly
            ? [EntityHandle.AssemblyDefinition, EntityHandle.ModuleDefinition]
            : [EntityHandle.ModuleDefinition];
        return parents
            .Where(parent => metadata.GetCustomAttributes(parent).Any(IsOfTheType))
            .Select(parent => (ISymbol)Of(metadata, parent))
            .ToList();

        // False for an attribute whose constructor the type system cannot resolve.
        bool IsOfTheType(CustomAttributeHandle handle)
        {
            try
            {
                var constructor = metadata.GetCustomAttribute(handle).Constructor;
                return type.Equals(module.ResolveMethod(constructor, default)?.DeclaringType.GetDefinition());
            }
            catch (Exception e) when (e is BadImageFormatException or ArgumentException)
            {
                return false;
            }
        }
    }
}
