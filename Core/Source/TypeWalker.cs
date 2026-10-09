using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Type-system traversal helpers. Centralized so the inheritance-walk boundary
/// (stop at <c>System.Object</c>, follow class chain only) lives in one place.
/// </summary>
public static class TypeWalker
{
    /// <summary>
    /// How deep a base class chain may run before it is taken for a cycle in malformed
    /// metadata. Real hierarchies stay far below it.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Walk the base class chain of <paramref name="type"/> that its own assembly defines,
    /// excluding interfaces and <c>System.Object</c>. Yields base types closest-first.
    /// </summary>
    public static IEnumerable<ITypeDefinition> WalkBaseTypes(ITypeDefinition type) =>
        WalkConstructedBaseTypes(type).Select(baseType => baseType.GetDefinition()!);

    /// <summary>
    /// The chain <see cref="WalkBaseTypes"/> yields, each base type as <paramref name="type"/>
    /// sees it: a generic base comes with the type arguments the chain gives it, so its
    /// <see cref="IType.GetSubstitution"/> turns a member's signature into the one the member
    /// has on <paramref name="type"/>.
    /// </summary>
    /// <remarks>
    /// The walk stops at the first base class another assembly defines. ILens's type system
    /// holds only the inspected assembly and a minimal core library, so such a class is an
    /// unknown type there, or, for a core type such as <c>System.Exception</c>, a definition
    /// without members. <see cref="WalkExternalBaseTypes"/> goes on from there in a type system
    /// that holds the assembly's references.
    /// </remarks>
    public static IEnumerable<IType> WalkConstructedBaseTypes(ITypeDefinition type)
    {
        IType current = type;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var baseType = BaseClassOf(current);
            if (baseType?.GetDefinition() is not { } definition
                || IsObject(definition)
                || definition.ParentModule != type.ParentModule)
                yield break;

            yield return baseType;
            current = baseType;
        }
    }

    /// <summary>
    /// The base classes of <paramref name="counterpart"/> that another assembly defines, read in
    /// the type system <paramref name="counterpart"/> belongs to, closest first and each as
    /// <paramref name="counterpart"/> sees it, up to but excluding <c>System.Object</c>. A base
    /// class that type system cannot read either ends the walk and is returned as
    /// <see cref="ExternalBases.Unread"/>: an unknown type, whose assembly the resolver could not
    /// load, or a definition from the minimal core library, which the type system falls back to
    /// when it finds no core library.
    /// </summary>
    /// <param name="counterpart">The type as a type system holding the assembly's references
    /// sees it, such as the decompiler's.</param>
    public static ExternalBases WalkExternalBaseTypes(ITypeDefinition counterpart)
    {
        var levels = new List<IType>();
        IType current = counterpart;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var baseType = BaseClassOf(current);
            if (baseType is null)
                break;

            var definition = baseType.GetDefinition();
            if (definition is not null && IsObject(definition))
                break;
            if (definition is not null && definition.ParentModule == counterpart.ParentModule)
            {
                // The assembly's own, which WalkConstructedBaseTypes yields.
                current = baseType;
                continue;
            }
            if (definition?.ParentModule is not MetadataModule)
                return new ExternalBases(levels, UnreadBase.Of(current, baseType));

            levels.Add(baseType);
            current = baseType;
        }
        return new ExternalBases(levels, null);
    }

    /// <summary>
    /// The class <paramref name="type"/> derives from, or null for an interface and for a type
    /// without one. A type's direct base types name its base class first and its interfaces
    /// after it, so the first is taken: an unknown type, which stands for a class of an assembly
    /// the type system does not hold, carries nothing else that marks it as a class.
    /// </summary>
    private static IType BaseClassOf(IType type)
    {
        if (type.Kind == TypeKind.Interface)
            return null;
        var first = type.DirectBaseTypes.FirstOrDefault();
        return first?.Kind == TypeKind.Interface ? null : first;
    }

    private static bool IsObject(ITypeDefinition type) => type.FullName == "System.Object";

    /// <summary>
    /// Is <paramref name="type"/> a C# <c>static class</c>? Such types compile to
    /// IL with both <c>abstract</c> and <c>sealed</c> set — that combination is
    /// unreachable from regular C# class declarations, so the pair uniquely
    /// identifies a static class.
    /// </summary>
    public static bool IsStaticClass(ITypeDefinition type)
    {
        return type.IsAbstract && type.IsSealed;
    }
}

/// <summary>
/// The base classes of a type that another assembly defines, closest first, as a type system
/// holding the assembly's references reads them, and the base class where that walk stopped
/// because it could not read it, or null when the walk reached <c>System.Object</c>.
/// </summary>
public sealed record ExternalBases(IReadOnlyList<IType> Levels, UnreadBase Unread)
{
    public static readonly ExternalBases None = new([], null);
}

/// <summary>
/// A base class no type system ILens holds could read: its name, and the assembly the
/// reference to it names, as <c>Name, Version=…</c>, or null when the reference names none.
/// </summary>
public sealed record UnreadBase(string TypeName, string Origin)
{
    /// <summary>The assembly that defines the class, as a message names it.</summary>
    public string Assembly => Origin ?? "the assembly that defines it";

    /// <summary>
    /// <paramref name="baseType"/>, the base class of <paramref name="derived"/>, with the
    /// assembly that <paramref name="derived"/>'s own assembly names for it.
    /// </summary>
    internal static UnreadBase Of(IType derived, IType baseType)
    {
        var origin = baseType.GetDefinitionOrUnknown() is { } named
            && derived.GetDefinition()?.ParentModule is MetadataModule module
            ? SymbolResolver.OriginOfReference(module.MetadataFile.Metadata, named.FullTypeName)
            : null;
        return new UnreadBase(ReferenceFormatter.FormatTypeName(baseType), origin);
    }
}
