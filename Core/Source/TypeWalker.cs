using System.Reflection;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

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
    /// The methods the file holds for <paramref name="type"/>. ILSpy's <c>Methods</c> adds a
    /// parameterless constructor to every struct and enum that declares none, since C# allows
    /// <c>new T()</c> on one; it has no metadata row, so the decompiler and the analyzers fail
    /// on it, and the compiler emits no call to it. Every reader of a type's methods takes
    /// them from here, so that no list shows a constructor no question can be answered about.
    /// </summary>
    public static IEnumerable<IMethod> DeclaredMethods(this ITypeDefinition type) =>
        type.Methods.Where(method => !method.MetadataToken.IsNil);

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
    /// <see cref="ExternalBases.Unread"/>: an unknown type, one of an assembly the resolver could
    /// not load or that the assembly it loaded does not hold, or a definition from the minimal
    /// core library, which the type system falls back to when it finds no core library.
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
    /// Whether <paramref name="counterpart"/> is <c>System.Attribute</c> or derives from it, read
    /// in the type system <paramref name="counterpart"/> belongs to. A base class that type
    /// system does not hold ends the walk without an answer and is returned as <c>Unread</c>,
    /// with <c>Derives</c> false. A class of the minimal core library, which ends
    /// <see cref="WalkExternalBaseTypes"/>, does not end this walk: it has no members, but it
    /// names its base class.
    /// </summary>
    /// <param name="counterpart">The type as a type system holding the assembly's references
    /// sees it, such as the decompiler's.</param>
    public static (bool Derives, UnreadBase Unread) DerivesFromAttribute(ITypeDefinition counterpart)
    {
        IType derived = counterpart;
        IType current = counterpart;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            // By name, so a reference to System.Attribute counts where its assembly is not held.
            if (current.ReflectionName == "System.Attribute")
                return (true, null);
            if (current.GetDefinition() is null)
                return (false, UnreadBase.Of(derived, current));

            var baseType = BaseClassOf(current);
            if (baseType is null)
                break;
            derived = current;
            current = baseType;
        }
        return (false, null);
    }

    /// <summary>
    /// The base types of <paramref name="counterpart"/>, classes and interfaces alike, that the
    /// type system it belongs to cannot read, each the nearest on its path: an unknown type, one
    /// of an assembly the resolver could not load or that the assembly it loaded does not hold,
    /// or a definition from the minimal core library. What such a type derives from or
    /// implements is not known either.
    /// <c>System.Object</c> is left out, since it has no base type and implements nothing.
    /// </summary>
    /// <param name="counterpart">The type as a type system holding the assembly's references
    /// sees it, such as the decompiler's.</param>
    public static IReadOnlyList<UnreadBase> UnreadBaseTypes(ITypeDefinition counterpart)
    {
        var unread = new List<UnreadBase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<IType>();
        pending.Enqueue(counterpart);
        while (pending.TryDequeue(out var current))
        {
            foreach (var baseType in current.DirectBaseTypes)
            {
                if (!seen.Add(baseType.ReflectionName))
                    continue;

                var definition = baseType.GetDefinition();
                if (definition?.ParentModule is MetadataModule)
                    pending.Enqueue(baseType);
                else if (definition is null || !IsObject(definition))
                    unread.Add(UnreadBase.Of(current, baseType));
            }
        }
        return unread;
    }

    /// <summary>
    /// The base interfaces of <paramref name="type"/>, an interface, read in the type system it
    /// belongs to, nearest first and each once: the interfaces it lists, and theirs in turn. C#
    /// looks a name up among their members as among the interface's own. A base interface that
    /// type system cannot read is among the unread ones instead, as
    /// <see cref="UnreadBaseTypes"/> gives it. None for a type that is no interface.
    /// </summary>
    public static (IReadOnlyList<ITypeDefinition> Read, IReadOnlyList<UnreadBase> Unread) WalkBaseInterfaces(
        ITypeDefinition type)
    {
        var read = new List<ITypeDefinition>();
        var unread = new List<UnreadBase>();
        if (type.Kind != TypeKind.Interface)
            return (read, unread);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<IType>();
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            foreach (var baseType in current.DirectBaseTypes)
            {
                if (!seen.Add(baseType.ReflectionName))
                    continue;

                var definition = baseType.GetDefinition();
                if (definition?.ParentModule is MetadataModule)
                {
                    if (definition.Kind != TypeKind.Interface)
                        continue;
                    read.Add(definition);
                    pending.Enqueue(baseType);
                }
                else if (definition is null || !IsObject(definition))
                    unread.Add(UnreadBase.Of(current, baseType));
            }
        }
        return (read, unread);
    }

    /// <summary>
    /// Where a name set in an attribute, as <c>Text</c> in <c>[Label(Text = "x")]</c>, leads from
    /// <paramref name="attributeClass"/>, read in the type system that class belongs to. C#
    /// takes the first class, from the attribute's own upward, that declares a public field or
    /// property of the name. One that is not public is out of reach where the attribute is
    /// written and hides nothing there. A property that overrides is passed over, since C#
    /// binds a name to the declaration that introduced it.
    /// </summary>
    /// <param name="stopAt">
    /// True for a class the walk ends at whatever it declares: one the caller asks about and
    /// knows by name alone, so that the name is taken as the caller named it on that class.
    /// </param>
    /// <returns>
    /// The member found, or that the walk ended at such a class, or the class it could not
    /// read, an unknown type of an assembly the resolver could not load or that the assembly
    /// it loaded does not hold, with the class below it on the walk, null when that is the
    /// attribute's class itself. Nothing when no class up to <c>System.Object</c> declares the
    /// name.
    /// </returns>
    public static (IMember Declared, bool Stopped, IType Unread, IType BelowUnread) FindSetByName(
        IType attributeClass, string name, Func<IType, bool> stopAt)
    {
        IType below = null;
        var current = attributeClass;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (stopAt(current))
                return (null, true, null, null);

            var definition = current.GetDefinition();
            if (definition is null)
                return (null, false, current, below);
            // A class of the minimal core library has no members to read. The only ones an
            // attribute class derives from are System.Attribute and System.Object, which
            // declare nothing an attribute can set.
            if (definition.ParentModule is MetadataModule)
            {
                var declared = (IMember)definition.Fields.FirstOrDefault(field =>
                        field.Name == name && field.Accessibility == Accessibility.Public)
                    ?? definition.Properties.FirstOrDefault(property =>
                        property.Name == name && property.Accessibility == Accessibility.Public && !Overriding.Of(property));
                if (declared is not null)
                    return (declared, false, null, null);
            }

            var baseType = BaseClassOf(current);
            if (baseType is null)
                break;
            below = current;
            current = baseType;
        }
        return default;
    }

    /// <summary>
    /// The class <paramref name="type"/> derives from, or null for an interface and for a type
    /// without one. A type's direct base types name its base class first and its interfaces
    /// after it, so the first is taken: an unknown type, which stands for a class of an assembly
    /// the type system does not hold, carries nothing else that marks it as a class.
    /// </summary>
    internal static IType BaseClassOf(IType type)
    {
        if (type.Kind == TypeKind.Interface)
            return null;
        var first = type.DirectBaseTypes.FirstOrDefault();
        return first?.Kind == TypeKind.Interface ? null : first;
    }

    private static bool IsObject(ITypeDefinition type) => type.FullName == "System.Object";

    /// <summary>
    /// The type an extension method extends: its first parameter's type, without the reference a
    /// receiver passed by <c>ref</c>, <c>in</c> or <c>ref readonly</c> is. ILSpy reads
    /// <c>System.IntPtr</c> and <c>System.UIntPtr</c> in a signature as <c>nint</c> and
    /// <c>nuint</c>, special types without a definition, which C# takes for the same types, so
    /// those come back as the types they name.
    /// </summary>
    public static IType ExtendedType(IMethod extensionMethod)
    {
        var type = extensionMethod.Parameters[0].Type;
        while (type is ModifiedType modified)
            type = modified.ElementType;
        if (type is ByReferenceType reference)
            type = reference.ElementType;
        return type.Kind switch
        {
            TypeKind.NInt => extensionMethod.Compilation.FindType(KnownTypeCode.IntPtr),
            TypeKind.NUInt => extensionMethod.Compilation.FindType(KnownTypeCode.UIntPtr),
            _ => type,
        };
    }

    /// <summary>
    /// The types under which <c>analyze</c> with <c>extension_methods</c> lists
    /// <paramref name="extensionMethod"/>: the type it extends (<see cref="ExtendedType"/>), or
    /// for a receiver that is a type parameter each type its constraints name, as if that type
    /// were the receiver, and for one whose constraints name no type <c>System.ValueType</c>
    /// where it has struct or unmanaged and <c>System.Object</c> otherwise, the types a receiver
    /// of that kind would name.
    /// </summary>
    public static IEnumerable<IType> ListedUnder(IMethod extensionMethod)
    {
        var extended = ExtendedType(extensionMethod);
        if (extended is not ITypeParameter parameter)
            return [extended];
        var named = WrittenConstraints(parameter).Where(type => type.Kind != TypeKind.TypeParameter).ToList();
        if (named.Count > 0)
            return named;
        return [extensionMethod.Compilation.FindType(
            parameter.HasValueTypeConstraint ? KnownTypeCode.ValueType : KnownTypeCode.Object)];
    }

    /// <summary>
    /// The type constraints of <paramref name="parameter"/> as C# writes them in its where
    /// clause, in their order: <c>System.Object</c> and <c>System.ValueType</c> left out, which a
    /// constraint names only for struct or unmanaged, written there as that word.
    /// </summary>
    /// <remarks>
    /// Compared by name, since ILens's own type system can know the core library's types by
    /// name alone.
    /// </remarks>
    public static IEnumerable<IType> WrittenConstraints(ITypeParameter parameter) =>
        parameter.TypeConstraints.Select(constraint => constraint.Type)
            .Where(type => ReferenceFormatter.FullTypeNameOf(type)?.ReflectionName is not ("System.Object" or "System.ValueType"));

    /// <summary>
    /// True for a type parameter every type meets: one without class, struct, unmanaged, new()
    /// or a type constraint. A notnull constraint is left aside, since C# only warns where it is
    /// not met.
    /// </summary>
    public static bool ConstrainsNothing(ITypeParameter parameter) =>
        !parameter.HasReferenceTypeConstraint && !parameter.HasValueTypeConstraint
        && !parameter.HasDefaultConstructorConstraint && !WrittenConstraints(parameter).Any();

    /// <summary>
    /// The type parameters <paramref name="type"/> declares itself: ILSpy gives a nested type
    /// those of the types enclosing it as well, first.
    /// </summary>
    public static IEnumerable<ITypeParameter> OwnTypeParameters(ITypeDefinition type) =>
        type.TypeParameters.Skip(type.DeclaringTypeDefinition?.TypeParameterCount ?? 0);

    /// <summary>
    /// True where <paramref name="parameter"/> may take a ref struct as its type argument, which
    /// C# 13 writes as <c>allows ref struct</c> and the compiler records as a flag of the type
    /// parameter's metadata row. ICSharpCode.Decompiler 9.1 reads no such flag, so the row is read
    /// here. False for a type parameter no definition was read for.
    /// </summary>
    public static bool AllowsRefStruct(ITypeParameter parameter)
    {
        IEntity owner = parameter.Owner switch
        {
            IMethod method => method.MemberDefinition,
            ITypeDefinition type => type,
            _ => null,
        };
        if (owner is not { ParentModule: MetadataModule module } || owner.MetadataToken.IsNil)
            return false;

        var metadata = module.MetadataFile.Metadata;
        GenericParameterHandleCollection rows;
        if (owner.MetadataToken.Kind == HandleKind.MethodDefinition)
            rows = metadata.GetMethodDefinition((MethodDefinitionHandle)owner.MetadataToken).GetGenericParameters();
        else if (owner.MetadataToken.Kind == HandleKind.TypeDefinition)
            rows = metadata.GetTypeDefinition((TypeDefinitionHandle)owner.MetadataToken).GetGenericParameters();
        else
            return false;

        // A nested type's rows number the type parameters of the types enclosing it as well, as
        // ILSpy's indices do.
        foreach (var handle in rows)
        {
            var row = metadata.GetGenericParameter(handle);
            if (row.Index == parameter.Index)
                return (row.Attributes & GenericParameterAttributes.AllowByRefLike) != 0;
        }
        return false;
    }

    /// <summary>
    /// True for a type that names a type parameter: one itself, or an array, a pointer, a
    /// reference, a construction or a tuple with one in it.
    /// </summary>
    public static bool NamesTypeParameter(IType type) => type switch
    {
        ITypeParameter => true,
        TypeWithElementType withElement => NamesTypeParameter(withElement.ElementType),
        ParameterizedType constructed => constructed.TypeArguments.Any(NamesTypeParameter),
        TupleType tuple => tuple.ElementTypes.Any(NamesTypeParameter),
        _ => false,
    };
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
/// A base class no type system ILens holds could read: its name, and the libraries the
/// references to it name, each as the reference search left it (<see cref="UnreadLibrary"/>),
/// none when the references name no assembly.
/// </summary>
public sealed record UnreadBase(string TypeName, IReadOnlyList<UnreadLibrary> Libraries)
{
    /// <summary>Why the class was not read, as a message says it.</summary>
    public string Reason => UnreadLibrary.Reason([(TypeName, Libraries)]);

    /// <summary>
    /// <paramref name="baseType"/>, the base class of <paramref name="derived"/>, with the
    /// libraries that <paramref name="derived"/>'s own assembly names for it, as the type
    /// system <paramref name="derived"/> belongs to holds them.
    /// </summary>
    internal static UnreadBase Of(IType derived, IType baseType)
    {
        IReadOnlyList<UnreadLibrary> libraries = baseType.GetDefinitionOrUnknown() is { } named
            && derived.GetDefinition()?.ParentModule is MetadataModule module
            ? UnreadLibrary.Of(module.Compilation, module.MetadataFile, named.FullTypeName)
            : [];
        return new UnreadBase(ReferenceFormatter.FormatTypeName(baseType), libraries);
    }
}
