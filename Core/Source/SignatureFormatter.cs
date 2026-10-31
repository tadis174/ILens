using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Renders members, parameters and types as one-line C#-style declarations (no method
/// bodies, no XML doc). Used by list_members and compare. A name a line declares, a
/// parameter's, a member's or a type parameter's, takes the '@' C# writes before a keyword
/// (<see cref="ReferenceFormatter.FormatIdentifier"/>); the types a line names are written as
/// the reference form writes them.
/// </summary>
public static class SignatureFormatter
{
    /// <summary>
    /// Render a member as a single-line C#-flavored signature.
    /// </summary>
    public static string FormatMember(IMember member) => FormatMember(member, full: false);

    /// <summary>
    /// <see cref="FormatMember(IMember)"/>, with <paramref name="full"/> every type in the line
    /// written in full (see <see cref="ReferenceFormatter.FormatTypeRef(IType, bool)"/>), for a
    /// line that would otherwise read alike another member's.
    /// </summary>
    public static string FormatMember(IMember member, bool full) => FormatMember(member, full, defaults: true);

    private static string FormatMember(IMember member, bool full, bool defaults) => member switch
    {
        IMethod m   => FormatMethodSignature(m, full, defaults),
        IProperty p => FormatPropertySignature(p, full, defaults),
        IField f    => FormatFieldSignature(f, full),
        IEvent e    => FormatEventSignature(e, full),
        _ => member.Name,
    };

    /// <summary>
    /// Render a member's declaration as <see cref="FormatMember(IMember, bool)"/> does in full,
    /// without a constant's value and without the parameters' defaults: the form in which the
    /// comparison of two builds reads a declaration, which compares the values on their own
    /// (<see cref="AssemblyComparer.DeclarationsDiffer"/>).
    /// </summary>
    public static string FormatDeclaration(IMember member) =>
        member is IField field ? FormatFieldDeclaration(field, full: true) : FormatMember(member, full: true, defaults: false);

    /// <summary>
    /// Render a parameter as <c>[modifier ]Type name[ = default]</c>: <c>ref</c>, <c>out</c>,
    /// <c>in</c> and <c>ref readonly</c> through <see cref="ReferenceFormatter.FormatParameterType(IParameter, bool)"/>,
    /// <c>params</c> on an array or a params collection (<see cref="ParameterPassing.IsParams"/>),
    /// the name with the '@' C# writes before a keyword, <c>object @object</c>
    /// (<see cref="ReferenceFormatter.FormatIdentifier"/>), and, with <paramref name="defaults"/>,
    /// the default a call that leaves the argument out passes, as C# declares it,
    /// <c>int width = 5</c> (<see cref="ConstantFormatter.DefaultOf"/>).
    /// </summary>
    private static string FormatParameter(IParameter p, bool full, bool defaults) =>
        $"{(ParameterPassing.IsParams(p) ? "params " : "")}{ReferenceFormatter.FormatParameterType(p, full)} " +
        ReferenceFormatter.FormatIdentifier(p.Name) +
        (defaults && TakesDefault(p) ? " = " + ConstantFormatter.DefaultOf(p, full) : "");

    /// <summary>
    /// Whether a declaration writes a default after <paramref name="p"/>, as the decompiled code
    /// does: an optional parameter whose metadata holds a value, passed by value, <c>in</c> or
    /// <c>ref readonly</c>. C# gives a <c>ref</c> or <c>out</c> parameter none.
    /// </summary>
    public static bool TakesDefault(IParameter p) =>
        p.IsOptional && p.HasConstantValueInSignature
        && ParameterPassing.Of(p) is { ByReference: false } or { ReadOnly: true };

    /// <summary>
    /// Render a constant whose value differs between two builds as its declaration in A with
    /// both values, <c>public const int Limit = 5 → 6</c>, each written as its build declares it
    /// (<see cref="ConstantFormatter.ValueOf"/>). A null value is written, which the declaration
    /// alone leaves out. With <paramref name="full"/> its type is written in full, for a line among
    /// others of its name that name another type of the same name, which only metadata no C# or VB
    /// compiler writes has, since a constant shares its name with no other member.
    /// </summary>
    public static string FormatConstantChange(IField field, IField inB, bool full = false) =>
        $"{FormatFieldDeclaration(field, full)} = {ConstantFormatter.ValueOf(field, full)} → {ConstantFormatter.ValueOf(inB, full)}";

    /// <summary>
    /// Render a type's declaration on one line: the words before its name
    /// (<see cref="FormatTypeKeywords"/>), its full name with its type parameters, the base types
    /// its metadata lists and the conditions on its own type parameters, such as
    /// <c>public class System.Collections.Generic.List&lt;T&gt; : IList&lt;T&gt;, IList</c>,
    /// <c>public struct System.Nullable&lt;T&gt; where T : struct</c> or
    /// <c>public delegate System.Func&lt;in T, out TResult&gt;</c>. A base every type of its
    /// kind has, such as <c>object</c> or <c>ValueType</c>, is left out, and an enum names its
    /// underlying type only when that is not <c>int</c>. The type parameters of the types
    /// enclosing a nested type have their conditions and their in or out on those types, as C#
    /// declares them. With <paramref name="full"/> every type the line names after the type's own
    /// name is written in full (see <see cref="ReferenceFormatter.FormatTypeRef(IType, bool)"/>),
    /// for a declaration that would otherwise read alike another's.
    /// </summary>
    public static string FormatTypeDeclaration(ITypeDefinition type, bool full = false)
    {
        var declaration = $"{FormatTypeKeywords(type)} {FormatDeclaredName(type)}";

        string Format(IType named) => ReferenceFormatter.FormatTypeRef(named, full);
        var bases = type.Kind == TypeKind.Enum
            ? (type.EnumUnderlyingType is { } underlying && !underlying.IsKnownType(KnownTypeCode.Int32)
                ? [Format(underlying)]
                : new List<string>())
            : type.DirectBaseTypes
                .Where(b => b.ReflectionName is not ("System.Object" or "System.ValueType" or "System.Enum"
                    or "System.MulticastDelegate" or "System.Delegate"))
                .Select(Format)
                .ToList();
        var conditions = ReferenceFormatter.FormatWhereClauses(TypeWalker.OwnTypeParameters(type), Format);
        return (bases.Count == 0 ? declaration : $"{declaration} : {string.Join(", ", bases)}") + conditions;
    }

    /// <summary>
    /// The words a type's declaration writes before its name: its accessibility, static, abstract
    /// or sealed on a class, readonly and ref on a struct, record, and its kind, such as
    /// <c>public static class</c>, <c>private readonly struct</c> or <c>public sealed record</c>.
    /// The comparison of two builds reads them as part of a type's metadata, so a word added here
    /// counts there as well.
    /// </summary>
    public static string FormatTypeKeywords(ITypeDefinition type)
    {
        var words = new List<string>();
        var accessibility = FormatAccessibility(type.Accessibility);
        if (accessibility.Length > 0)
            words.Add(accessibility);
        switch (type.Kind)
        {
            case TypeKind.Class:
                if (type.IsStatic) words.Add("static");
                else if (type.IsAbstract) words.Add("abstract");
                else if (type.IsSealed) words.Add("sealed");
                words.Add(type.IsRecord ? "record" : "class");
                break;
            case TypeKind.Struct:
                if (type.IsReadOnly) words.Add("readonly");
                if (type.IsByRefLike) words.Add("ref");
                words.Add(type.IsRecord ? "record struct" : "struct");
                break;
            default:
                words.Add(type.Kind.ToString().ToLowerInvariant());
                break;
        }
        return string.Join(" ", words);
    }

    /// <summary>
    /// A type's full name as its declaration writes it: its namespace, the types enclosing it,
    /// and each one's own type parameters by name, such as
    /// <c>System.Collections.Generic.Dictionary&lt;TKey, TValue&gt;.Enumerator</c>. The type's own
    /// type parameters carry the in or out C# declares a variant one with, as in
    /// <c>System.Collections.Generic.IEnumerable&lt;out T&gt;</c>; those of the types enclosing it
    /// carry theirs on those types.
    /// </summary>
    private static string FormatDeclaredName(ITypeDefinition type)
    {
        var segments = new List<string> { Segment(type, withVariance: true) };
        for (var t = type.DeclaringTypeDefinition; t != null; t = t.DeclaringTypeDefinition)
            segments.Add(Segment(t, withVariance: false));
        segments.Reverse();
        var name = string.Join(".", segments);
        return string.IsNullOrEmpty(type.Namespace) ? name : $"{type.Namespace}.{name}";

        static string Segment(ITypeDefinition segment, bool withVariance)
        {
            var own = TypeWalker.OwnTypeParameters(segment)
                .Select(p => (withVariance ? FormatVariance(p) : "") + ReferenceFormatter.FormatIdentifier(p.Name))
                .ToList();
            return own.Count == 0 ? segment.Name : $"{segment.Name}<{string.Join(", ", own)}>";
        }
    }

    /// <summary>
    /// The word C# writes before a variant type parameter of an interface or a delegate,
    /// <c>out </c> or <c>in </c>, as the type parameter's metadata row records it; empty for one
    /// that is not variant.
    /// </summary>
    private static string FormatVariance(ITypeParameter parameter) => parameter.Variance switch
    {
        VarianceModifier.Covariant => "out ",
        VarianceModifier.Contravariant => "in ",
        _ => "",
    };

    private static string FormatMethodSignature(IMethod method, bool full, bool defaults)
    {
        var acc = FormatAccessibility(method.Accessibility);

        // An extension method takes this before its first parameter, by the rule the decompiler
        // writes it by: a static method with ExtensionAttribute, whatever its class, so that a
        // VB module's extension method takes it as well. The reference form leaves it out, as it
        // leaves out params: neither tells two methods apart.
        var parameters = string.Join(", ", method.Parameters.Select((parameter, index) =>
            (index == 0 && method.IsExtensionMethod ? "this " : "") + FormatParameter(parameter, full, defaults)));

        // C# declares a constructor under its type's name and without a return type, and a
        // static one without accessibility.
        if (method.IsConstructor)
        {
            var typeName = method.DeclaringTypeDefinition?.Name ?? method.Name;
            return method.IsStatic ? $"static {typeName}()" : $"{acc} {typeName}({parameters})";
        }

        var modifiers = FormatMemberModifiers(method);
        var ret = ReferenceFormatter.FormatReturnType(method, full);
        var typeParams = method.TypeParameters.Count > 0
            ? "<" + string.Join(", ", method.TypeParameters.Select(p => ReferenceFormatter.FormatIdentifier(p.Name))) + ">"
            : "";

        // An override and an explicit interface implementation take the conditions of the member
        // they override or implement: C# lets them repeat only class or struct, or write default,
        // to tell T? apart, and the decompiler writes none.
        var conditions = method.TypeParameters.Count == 0 || Overriding.Of(method) || method.IsExplicitInterfaceImplementation
            ? ""
            : ReferenceFormatter.FormatWhereClauses(method.TypeParameters, type => ReferenceFormatter.FormatTypeRef(type, full));
        return $"{acc}{modifiers} {ret} {ReferenceFormatter.FormatIdentifier(method.Name)}{typeParams}({parameters}){conditions}";
    }

    private static string FormatPropertySignature(IProperty property, bool full, bool defaults)
    {
        var acc = FormatAccessibility(property.Accessibility);
        var modifiers = FormatMemberModifiers(property);
        var ret = ReferenceFormatter.FormatReturnType(property, full);

        // A property with parameters is an indexer in C#. It prints under its metadata name, Item
        // unless [IndexerName] gives another, rather than C#'s 'this', because that name is what
        // the other tools take, as in get_Item.
        var name = ReferenceFormatter.FormatIdentifier(property.Name);
        if (property.Parameters.Count > 0)
            name += $"[{string.Join(", ", property.Parameters.Select(parameter => FormatParameter(parameter, full, defaults)))}]";

        var accessors = new StringBuilder();
        if (property.CanGet)
            AppendAccessor(accessors, "get", property.Getter, property.Accessibility);
        if (property.CanSet)
            AppendAccessor(accessors, property.Setter.IsInitOnly ? "init" : "set", property.Setter,
                property.Accessibility);

        return $"{acc}{modifiers} {ret} {name} {{ {accessors}}}";
    }

    /// <summary>
    /// Append <c>get;</c> or <c>private set;</c>-style accessor fragments. The accessor's
    /// own modifier is rendered only when it differs from the property — so
    /// <c>public int X { get; private set; }</c> round-trips correctly.
    /// </summary>
    private static void AppendAccessor(StringBuilder sb, string role,
        IMethod accessor, Accessibility propertyAccessibility)
    {
        if (accessor != null && accessor.Accessibility != propertyAccessibility)
            sb.Append(FormatAccessibility(accessor.Accessibility)).Append(' ');
        sb.Append(role).Append("; ");
    }

    private static string FormatFieldSignature(IField field, bool full)
    {
        var line = FormatFieldDeclaration(field, full);

        // For a const, the literal value is the meaningful information — render it inline.
        if (field.IsConst && field.GetConstantValue() is not null)
            line += $" = {ConstantFormatter.ValueOf(field, full)}";

        return line;
    }

    /// <summary>A field's declaration without its value: accessibility, modifiers, type and name.</summary>
    private static string FormatFieldDeclaration(IField field, bool full)
    {
        var acc = FormatAccessibility(field.Accessibility);
        string modifiers;
        if (field.IsConst) modifiers = " const";
        else if (field.IsStatic && field.IsReadOnly) modifiers = " static readonly";
        else if (field.IsStatic) modifiers = " static";
        else if (field.IsReadOnly) modifiers = " readonly";
        else modifiers = "";
        if (field.IsVolatile) modifiers += " volatile";

        var t = ReferenceFormatter.FormatTypeRef(field.Type, full);
        return $"{acc}{modifiers} {t} {ReferenceFormatter.FormatIdentifier(field.Name)}";
    }

    private static string FormatEventSignature(IEvent evt, bool full)
    {
        var acc = FormatAccessibility(evt.Accessibility);
        var modifiers = FormatMemberModifiers(evt);
        var t = ReferenceFormatter.FormatTypeRef(evt.ReturnType, full);
        return $"{acc}{modifiers} event {t} {ReferenceFormatter.FormatIdentifier(evt.Name)}";
    }

    /// <summary>
    /// The modifiers C# writes before a member's type; an interface's member takes those of
    /// <see cref="FormatInterfaceMemberModifiers"/>. An override is asked about before abstract,
    /// since C# writes <c>abstract override</c> for an abstract member that overrides:
    /// <c>abstract</c> alone, below a member of the same signature, declares a new member that
    /// hides that one.
    /// </summary>
    private static string FormatMemberModifiers(IMember member)
    {
        if (member.DeclaringTypeDefinition?.Kind == TypeKind.Interface)
            return FormatInterfaceMemberModifiers(member);
        if (member.IsStatic) return " static";
        if (Overriding.Of(member))
            return member.IsAbstract ? " abstract override" : Overriding.IsSealed(member) ? " sealed override" : " override";
        if (member.IsAbstract) return " abstract";
        if (member.IsVirtual) return " virtual";
        return "";
    }

    /// <summary>
    /// The modifiers of a member an interface declares, as C# writes them there, with the word
    /// that stands for the body a declaration line does not show. A member the implementing
    /// type has to supply takes none, which keeps the line of nearly every interface member
    /// short. One with a default implementation takes <c>virtual</c>: C# allows the word
    /// there, source mostly omits it, and without the body nothing else tells such a member
    /// from the first kind. A static member is <c>static</c>, <c>static abstract</c> or
    /// <c>static virtual</c>.
    /// </summary>
    private static string FormatInterfaceMemberModifiers(IMember member)
    {
        if (member.IsStatic)
            return member.IsAbstract ? " static abstract" : member.IsVirtual ? " static virtual" : " static";

        // An implementation of a base interface's member has a body unless it makes that
        // member abstract again, and prints as a class's explicit implementation does. ILSpy
        // reads it as a sealed override, since the compiler gives it no slot of its own.
        if (member.IsExplicitInterfaceImplementation)
            return member.IsAbstract ? " abstract" : "";

        if (member.IsAbstract) return "";
        if (member.IsVirtual) return " virtual";

        // What is left has a body no class can replace. C# requires sealed for it and has no
        // word for a private member, which is always of this kind.
        return member.Accessibility == Accessibility.Private ? "" : " sealed";
    }

    private static string FormatAccessibility(Accessibility a) => a switch
    {
        Accessibility.Public               => "public",
        Accessibility.Protected            => "protected",
        Accessibility.Internal             => "internal",
        Accessibility.ProtectedOrInternal  => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private              => "private",
        _ => "",
    };
}
