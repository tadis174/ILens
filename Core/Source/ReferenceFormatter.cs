using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Renders symbols and types as call-site references — fully qualified names,
/// no accessibility/modifiers. Used by analyze tool output.
/// For declaration-style signatures see <see cref="SignatureFormatter"/>.
/// </summary>
public static class ReferenceFormatter
{
    /// <summary>
    /// Format a single symbol to its display name.
    /// </summary>
    public static string FormatSymbol(ISymbol symbol)
    {
        return symbol switch
        {
            IMethod method => FormatMethodReference(method),
            IProperty prop => $"{FormatTypeName(prop.DeclaringType)}.{prop.Name}{FormatIndices(prop)}",
            IField field => $"{FormatTypeName(field.DeclaringType)}.{field.Name}",
            IEvent evt => $"{FormatTypeName(evt.DeclaringType)}.{evt.Name}",
            IType type => FormatTypeName(type),
            IEntity entity => entity.FullName,
            _ => symbol.ToString() ?? "(unknown)"
        };
    }

    /// <summary>
    /// An indexer's index types in brackets, <c>[int, int]</c>, and nothing for any other
    /// property. Several indexers share one name, <c>Item</c> unless <c>[IndexerName]</c>
    /// renames it, so the name alone does not say which is meant. The test is
    /// <c>Parameters.Count</c>, not <c>IsIndexer</c>, which ILSpy sets only for the name the
    /// declaring type's default member attribute gives.
    /// </summary>
    public static string FormatIndices(IProperty property) =>
        property.Parameters.Count == 0
            ? ""
            : $"[{string.Join(", ", property.Parameters.Select(FormatParameterType))}]";

    /// <summary>
    /// Format a method as a fully-qualified call-site reference with its parameter
    /// types in parentheses, e.g. <c>System.String.IndexOf(char, int)</c>.
    /// Uses C# keywords for well-known framework types and unfolds generics.
    /// </summary>
    public static string FormatMethodReference(IMethod method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameterType));
        return $"{FormatTypeName(method.DeclaringType)}.{method.Name}({parameters})";
    }

    /// <summary>
    /// A line saying that code reaches <paramref name="member"/> only through the interface
    /// member it implements explicitly, or <c>null</c> for any other member. C# never names an
    /// explicit implementation at a call: a call goes to the interface member and reaches the
    /// implementation at run time. A usage answer about the implementation is therefore empty
    /// however often that happens, and without this line it would read as proof of disuse.
    /// Those calls are not added to the answer, since each could as well reach another
    /// implementation of the interface.
    /// </summary>
    public static string FormatExplicitRoute(IMember member)
    {
        if (!member.IsExplicitInterfaceImplementation)
            return null;
        var implemented = member.ExplicitlyImplementedInterfaceMembers.Select(FormatSymbol).ToList();
        var through = implemented.Count > 0 ? string.Join(", ", implemented) : member.Name;
        return $"Explicit implementation: code reaches it only through {through}; ask about that member for its uses.";
    }

    /// <summary>
    /// A line saying that code calls <paramref name="member"/> through the declaration it
    /// overrides, or <c>null</c> for a member that overrides nothing. C# compiles a call to a
    /// virtual member as a call to the declaration that introduced its slot rather than to an
    /// override (<see cref="SymbolResolver.SlotDeclarationOf"/>), so an answer listing only what
    /// names the override would be empty however often code reaches it, and would read as proof
    /// of disuse. With <paramref name="listsCalls"/>, the answer holds those calls whose
    /// receiver's type is the override's class or derives from it, the calls the decompiled C#
    /// shows as calls to the override; otherwise it holds only what names the override, and the
    /// line points to call_sites for those calls. Those calls can be counted only when the
    /// assembly defines that declaration.
    /// </summary>
    public static string FormatOverrideRoute(IMember member, bool listsCalls)
    {
        if (!member.IsOverride)
            return null;

        var slot = SymbolResolver.SlotDeclarationOf(member);
        if (slot.IsOverride)
        {
            var from = member.DeclaringTypeDefinition is { } declaringType
                && NearestExternalBase(declaringType) is { } external
                ? $"the {FormatTypeName(external)} member"
                : "the member";
            return $"Override: code calls {from} it overrides instead, which another assembly defines, " +
                (listsCalls ? "so those calls are not counted here." : "so only references to this override are listed.");
        }

        var receivers = $"those calls whose receiver's type is {FormatTypeName(member.DeclaringType)} or derives from it";
        if (listsCalls)
            return $"Override: code calls {FormatSymbol(slot)} instead; listed are {receivers}.";

        IMethod[] accessors = member switch
        {
            IProperty property => [property.Getter, property.Setter],
            IEvent @event => [@event.AddAccessor, @event.RemoveAccessor],
            _ => [],
        };
        var declared = accessors.Where(accessor => accessor != null).Select(accessor => accessor.Name).ToList();
        var on = declared.Count > 0 ? " on " + string.Join(" and ", declared) : "";
        return $"Override: code calls {FormatSymbol(slot)} instead, so only references to this override are listed; " +
            $"'{AnalysisKind.CallSites.ToSchemaName()}'{on} lists {receivers}.";
    }

    /// <summary>
    /// The nearest class <paramref name="type"/> derives from that its assembly does not define,
    /// or <c>null</c> when every base class is the assembly's own.
    /// </summary>
    /// <remarks>
    /// A class's direct base types name the class it derives from first and its interfaces
    /// after it, so the walk takes only the first. An interface from an assembly ILens does not
    /// load is an unknown type, which nothing marks as an interface.
    /// </remarks>
    private static IType NearestExternalBase(ITypeDefinition type)
    {
        const int maxDepth = 64;  // a longer chain is taken for a cycle in malformed metadata
        var current = type;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var baseClass = current.DirectBaseTypes.FirstOrDefault();
            if (baseClass is null || baseClass.Kind == TypeKind.Interface)
                return null;
            if (baseClass.GetDefinition() is not { } definition || definition.ParentModule != type.ParentModule)
                return baseClass;
            current = definition;
        }
        return null;
    }

    /// <summary>
    /// A parameter's type with the keyword C# passes it by, <c>ref int</c>, <c>out int</c>,
    /// <c>in int</c> or <c>ref readonly int</c>, and the type alone for one passed by value.
    /// A method of an assembly ILens does not load is known only from the signature a call
    /// names, which says that a parameter is passed by reference but not with which keyword,
    /// so such a parameter is written <c>ref</c>.
    /// </summary>
    public static string FormatParameterType(IParameter parameter) =>
        parameter.ReferenceKind switch
        {
            ReferenceKind.Ref => "ref ",
            ReferenceKind.Out => "out ",
            ReferenceKind.In => "in ",
            ReferenceKind.RefReadOnly => "ref readonly ",
            _ when parameter.Type.Kind == TypeKind.ByReference => "ref ",
            _ => "",
        } + FormatTypeRef(parameter.Type);

    /// <summary>
    /// A method's or property's return type as C# declares it: <c>ref T</c> or
    /// <c>ref readonly T</c> for one that returns by reference, the type alone otherwise.
    /// </summary>
    public static string FormatReturnType(IMember member)
    {
        var type = member.ReturnType;
        while (type is ModifiedType modified)
            type = modified.ElementType;
        if (type is not ByReferenceType)
            return FormatTypeRef(member.ReturnType);
        var readOnly = member switch
        {
            IMethod method => method.ReturnTypeIsRefReadOnly,
            IProperty property => property.ReturnTypeIsRefReadOnly,
            _ => false,
        };
        return (readOnly ? "ref readonly " : "ref ") + FormatTypeRef(member.ReturnType);
    }

    /// <summary>
    /// A type's fully qualified name with each generic segment's type parameters: a definition
    /// prints them unbound, as in <c>typeof</c> — <c>System.Func&lt;,&gt;</c>,
    /// <c>System.Collections.Generic.Dictionary&lt;,&gt;.Enumerator</c> — and a constructed
    /// type with its arguments, <c>System.Collections.Generic.List&lt;System.Int32&gt;</c>.
    /// Every name <see cref="ParsedTypeName"/> reads back.
    /// </summary>
    /// <remarks>
    /// Print type names through this rather than <c>FullName</c>, which drops the arity and so
    /// names <c>Task</c> and <c>Task&lt;TResult&gt;</c> alike: a caller who passes such a name
    /// back gets whichever type the lookup prefers, not necessarily the one printed.
    /// </remarks>
    public static string FormatTypeName(IType type)
    {
        switch (type)
        {
            case ParameterizedType constructed:
                var arguments = constructed.TypeArguments.Select(FormatTypeName).ToList();
                return FullTypeNameOf(constructed.GenericType) is { } generic
                    ? FormatSegments(generic, arguments, qualified: true)
                    : $"{constructed.GenericType.FullName}<{string.Join(", ", arguments)}>";
            case TupleType tuple:
                return FormatTypeName(tuple.UnderlyingType);
            case ArrayType array:
                return $"{FormatTypeName(array.ElementType)}[{new string(',', array.Dimensions - 1)}]";
            case ByReferenceType byRef:
                return FormatTypeName(byRef.ElementType);
            case TypeWithElementType decorated:
                return FormatTypeName(decorated.ElementType) + decorated.NameSuffix;
            case FunctionPointerType functionPointer:
                return FormatFunctionPointer(functionPointer, FormatTypeName);
            case ITypeParameter typeParameter:
                return typeParameter.Name;
        }
        return FullTypeNameOf(type) is { } name
            ? FormatTypeName(name)
            : type.FullName;
    }

    /// <summary>
    /// A named type's name as <see cref="FormatTypeName(IType)"/> prints its definition, for a
    /// type known only by name, such as one the assembly references but does not define.
    /// </summary>
    public static string FormatTypeName(FullTypeName name) =>
        FormatSegments(name, null, qualified: true);

    /// <summary>
    /// A named type's name with its nesting and the arity of each level: from its definition,
    /// or for a type in an assembly ILens does not load, from the reference to it. Null for a
    /// type without such a name, such as a type parameter or an array.
    /// </summary>
    /// <remarks>
    /// Code with nullable annotations wraps a reference type in its annotation, which hides a
    /// type from an unloaded assembly from both lookups, so the annotation is dropped first.
    /// </remarks>
    public static FullTypeName? FullTypeNameOf(IType type)
    {
        type = type.ChangeNullability(Nullability.Oblivious);
        return (type.GetDefinition() ?? type as ITypeDefinitionOrUnknown)?.FullTypeName;
    }

    /// <summary>
    /// <paramref name="name"/> segment by segment, with each segment's own type parameters
    /// taken in order from <paramref name="arguments"/>, or left unbound when there are none.
    /// <paramref name="qualified"/> puts the namespace in front.
    /// </summary>
    private static string FormatSegments(FullTypeName name, IReadOnlyList<string> arguments, bool qualified)
    {
        var top = name.TopLevelTypeName;
        var text = new System.Text.StringBuilder();
        if (qualified && !string.IsNullOrEmpty(top.Namespace))
            text.Append(top.Namespace).Append('.');
        var used = 0;
        AppendSegment(top.Name, top.TypeParameterCount);
        for (var level = 0; level < name.NestingLevel; level++)
        {
            text.Append('.');
            AppendSegment(name.GetNestedTypeName(level), name.GetNestedTypeAdditionalTypeParameterCount(level));
        }
        return text.ToString();

        void AppendSegment(string segment, int arity)
        {
            text.Append(segment);
            if (arity == 0)
                return;
            text.Append('<');
            if (arguments == null)
                text.Append(',', arity - 1);
            else
                text.AppendJoin(", ", arguments.Skip(used).Take(arity));
            text.Append('>');
            used += arity;
        }
    }

    /// <summary>
    /// Format a list of methods as comma-joined call-site references. Used by
    /// <see cref="SymbolResolver"/> to list overload candidates in error messages.
    /// </summary>
    public static string FormatMethodList(IEnumerable<IMethod> methods) =>
        string.Join(", ", methods.Select(FormatMethodReference));

    /// <summary>
    /// Format a resolved method as a full signature — parameter types and return type —
    /// anchored on a caller-supplied declaring-type name, e.g.
    /// <c>Shapes.Circle.Equals(Circle) → bool</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="FormatMethodReference"/> this does not use the method's own
    /// <c>DeclaringType</c>: <c>decompile</c> anchors its header on the type the
    /// caller asked about, and reports an inherited or extension origin separately. The
    /// return type is included because it is part of what distinguishes the overload that
    /// resolution actually picked — a bare <c>// Shapes.Circle.Equals</c> header is ambiguous
    /// by construction, and would let a caller mistake one overload for another.
    /// </remarks>
    public static string FormatResolvedSignature(IMethod method, string declaringTypeName)
    {
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameterType));
        return $"{declaringTypeName}.{method.Name}({parameters}) → {FormatReturnType(method)}";
    }

    /// <summary>
    /// Render an <see cref="IType"/> as a compact C#-style type reference,
    /// recursively unfolding generics and arrays. Uses C# keywords for the
    /// well-known framework types (e.g., <c>float</c>, <c>string</c>) and
    /// short names elsewhere. A type nested in a generic type takes that type's arguments,
    /// so it is written with the types enclosing it, each carrying its own, as C# writes
    /// <c>Dictionary&lt;TKey, TValue&gt;.Entry</c>.
    /// </summary>
    /// <remarks>
    /// Every type built from other types is written with them, since they are what tells two
    /// such types apart: a pointer, a function pointer's signature, and the type a modifier
    /// is attached to. A reference is written as the type it refers to, and a modifier C# writes
    /// as a keyword, <c>ref readonly</c>, <c>in</c>, <c>volatile</c> or <c>init</c>, is left out:
    /// the keyword belongs to the parameter, return or member, and
    /// <see cref="FormatParameterType"/> and <see cref="FormatReturnType"/> write it. Any other
    /// modifier keeps its <c>modreq</c> or <c>modopt</c> form. A tuple is written as the
    /// <c>ValueTuple&lt;...&gt;</c> it is. ILSpy reads one as a tuple only where
    /// <c>System.ValueTuple</c> resolves, such as in an assembly that defines it, and as a
    /// constructed type elsewhere; writing the <c>ValueTuple&lt;...&gt;</c> makes both print alike.
    /// </remarks>
    public static string FormatTypeRef(IType type)
    {
        if (type is ParameterizedType pt)
        {
            var args = pt.TypeArguments.Select(FormatTypeRef).ToList();
            if (FullTypeNameOf(pt.GenericType) is { } name && TakesEnclosingArguments(name))
                return FormatSegments(name, args, qualified: false);
            return $"{TypeKeyword(pt.GenericType) ?? pt.GenericType.Name}<{string.Join(", ", args)}>";
        }
        if (type is TupleType tuple)
        {
            return FormatTypeRef(tuple.UnderlyingType);
        }
        if (type is ArrayType arr)
        {
            return FormatTypeRef(arr.ElementType) + "[]";
        }
        if (type is ByReferenceType byRef)
        {
            return FormatTypeRef(byRef.ElementType);
        }
        if (type is ModifiedType modified && IsKeywordModifier(modified.Modifier))
        {
            return FormatTypeRef(modified.ElementType);
        }
        if (type is TypeWithElementType decorated)
        {
            return FormatTypeRef(decorated.ElementType) + decorated.NameSuffix;
        }
        if (type is FunctionPointerType functionPointer)
        {
            return FormatFunctionPointer(functionPointer, FormatTypeRef);
        }
        return TypeKeyword(type) ?? type.Name;
    }

    /// <summary>
    /// True for the modifiers C# writes as a keyword: <c>InAttribute</c> on an <c>in</c>
    /// parameter or a <c>ref readonly</c> return, <c>IsVolatile</c> on a <c>volatile</c> field,
    /// and <c>IsExternalInit</c> on an <c>init</c> accessor's return.
    /// </summary>
    private static bool IsKeywordModifier(IType modifier) =>
        (modifier.Namespace, modifier.Name) is ("System.Runtime.InteropServices", "InAttribute")
            or ("System.Runtime.CompilerServices", "IsVolatile")
            or ("System.Runtime.CompilerServices", "IsExternalInit");

    /// <summary>
    /// A function pointer as C# writes it, <c>delegate* unmanaged[Cdecl]&lt;ref int, void&gt;</c>:
    /// the calling convention, then the parameter types and last the return type, each written
    /// by <paramref name="format"/>.
    /// </summary>
    private static string FormatFunctionPointer(FunctionPointerType pointer, Func<IType, string> format)
    {
        var conventions = new List<string>();
        if (pointer.CallingConvention is not (SignatureCallingConvention.Default or SignatureCallingConvention.Unmanaged))
        {
            conventions.Add(pointer.CallingConvention switch
            {
                SignatureCallingConvention.CDecl => "Cdecl",
                SignatureCallingConvention.StdCall => "Stdcall",
                SignatureCallingConvention.ThisCall => "Thiscall",
                SignatureCallingConvention.FastCall => "Fastcall",
                SignatureCallingConvention.VarArgs => "Varargs",
                var other => other.ToString(),
            });
        }
        // C# names a convention by its CallConv type without the prefix: CallConvCdecl is Cdecl.
        conventions.AddRange(pointer.CustomCallingConventions.Select(convention =>
            convention.Name.StartsWith("CallConv", StringComparison.Ordinal) && convention.Name.Length > "CallConv".Length
                ? convention.Name["CallConv".Length..]
                : format(convention)));

        var unmanaged = pointer.CallingConvention == SignatureCallingConvention.Default ? ""
            : conventions.Count == 0 ? " unmanaged"
            : $" unmanaged[{string.Join(", ", conventions)}]";
        var parameters = pointer.ParameterTypes.Select((parameter, i) =>
            RefKeyword(pointer.ParameterReferenceKinds[i]) + format(parameter));
        var returnType = pointer.ReturnType is ByReferenceType
            ? RefKeyword(pointer.ReturnIsRefReadOnly ? ReferenceKind.RefReadOnly : ReferenceKind.Ref) + format(pointer.ReturnType)
            : format(pointer.ReturnType);
        return $"delegate*{unmanaged}<{string.Join(", ", parameters.Append(returnType))}>";
    }

    private static string RefKeyword(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Ref => "ref ",
        ReferenceKind.Out => "out ",
        ReferenceKind.In => "in ",
        ReferenceKind.RefReadOnly => "ref readonly ",
        _ => "",
    };

    private static bool TakesEnclosingArguments(FullTypeName name) =>
        name.NestingLevel > 0
        && name.TypeParameterCount > name.GetNestedTypeAdditionalTypeParameterCount(name.NestingLevel - 1);

    /// <summary>
    /// The C# keyword for a well-known framework type, or <c>null</c> if the type
    /// has no keyword form. Public so <see cref="TypeMatcher"/> can recognize keyword
    /// patterns the way this formatter renders them.
    /// </summary>
    /// <remarks>
    /// ILSpy reads most <c>System.IntPtr</c> and <c>System.UIntPtr</c> in a signature as special
    /// types already named <c>nint</c> and <c>nuint</c>. The two rows here cover the rest, such as
    /// a member signature of another assembly, which ILens decodes itself.
    /// </remarks>
    public static string TypeKeyword(IType type) => KeywordFor(type.Namespace, type.Name);

    /// <summary>
    /// The C# keyword for the type <paramref name="name"/> in <paramref name="namespace"/>,
    /// such as <c>int</c> for <c>System.Int32</c>, or null when it has none.
    /// </summary>
    public static string KeywordFor(string @namespace, string name) =>
        @namespace == "System" ? name switch
        {
            "Boolean" => "bool",
            "Byte"    => "byte",
            "SByte"   => "sbyte",
            "Char"    => "char",
            "Decimal" => "decimal",
            "Double"  => "double",
            "Single"  => "float",
            "Int32"   => "int",
            "UInt32"  => "uint",
            "Int64"   => "long",
            "UInt64"  => "ulong",
            "Int16"   => "short",
            "UInt16"  => "ushort",
            "IntPtr"  => "nint",
            "UIntPtr" => "nuint",
            "Object"  => "object",
            "String"  => "string",
            "Void"    => "void",
            _ => null,
        } : null;
}
