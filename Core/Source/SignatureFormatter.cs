using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Renders members, parameters and types as one-line C#-style declarations (no method
/// bodies, no XML doc). Used by list_members and compare.
/// </summary>
public static class SignatureFormatter
{
    /// <summary>
    /// Render a member as a single-line C#-flavored signature.
    /// </summary>
    public static string FormatMember(IMember member) => member switch
    {
        IMethod m   => FormatMethodSignature(m),
        IProperty p => FormatPropertySignature(p),
        IField f    => FormatFieldSignature(f),
        IEvent e    => FormatEventSignature(e),
        _ => member.Name,
    };

    /// <summary>
    /// Render a parameter as <c>[modifier ]Type name</c>: <c>ref</c>, <c>out</c>, <c>in</c>
    /// and <c>ref readonly</c> through <see cref="ReferenceFormatter.FormatParameterType"/>,
    /// and <c>params</c>.
    /// </summary>
    public static string FormatParameter(IParameter p) =>
        $"{(p.IsParams ? "params " : "")}{ReferenceFormatter.FormatParameterType(p)} {p.Name}";

    /// <summary>
    /// Render a type's declaration on one line: accessibility, modifiers, kind, its full name
    /// with its type parameters, and the base types its metadata lists, such as
    /// <c>public class System.Collections.Generic.List&lt;T&gt; : IList&lt;T&gt;, IList</c>. A
    /// base every type of its kind has, such as <c>object</c> or <c>ValueType</c>, is left out,
    /// and an enum names its underlying type only when that is not <c>int</c>.
    /// </summary>
    public static string FormatTypeDeclaration(ITypeDefinition type)
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
        words.Add(FormatDeclaredName(type));
        var declaration = string.Join(" ", words);

        var bases = type.Kind == TypeKind.Enum
            ? (type.EnumUnderlyingType is { } underlying && !underlying.IsKnownType(KnownTypeCode.Int32)
                ? [ReferenceFormatter.FormatTypeRef(underlying)]
                : new List<string>())
            : type.DirectBaseTypes
                .Where(b => b.ReflectionName is not ("System.Object" or "System.ValueType" or "System.Enum"
                    or "System.MulticastDelegate" or "System.Delegate"))
                .Select(ReferenceFormatter.FormatTypeRef)
                .ToList();
        return bases.Count == 0 ? declaration : $"{declaration} : {string.Join(", ", bases)}";
    }

    /// <summary>
    /// A type's full name as its declaration writes it: its namespace, the types enclosing it,
    /// and each one's own type parameters by name, such as
    /// <c>System.Collections.Generic.Dictionary&lt;TKey, TValue&gt;.Enumerator</c>.
    /// </summary>
    private static string FormatDeclaredName(ITypeDefinition type)
    {
        var segments = new List<string>();
        for (var t = type; t != null; t = t.DeclaringTypeDefinition)
        {
            var own = t.TypeParameters.Skip(t.DeclaringTypeDefinition?.TypeParameterCount ?? 0)
                .Select(p => p.Name)
                .ToList();
            segments.Add(own.Count == 0 ? t.Name : $"{t.Name}<{string.Join(", ", own)}>");
        }
        segments.Reverse();
        var name = string.Join(".", segments);
        return string.IsNullOrEmpty(type.Namespace) ? name : $"{type.Namespace}.{name}";
    }

    private static string FormatMethodSignature(IMethod method)
    {
        var acc = FormatAccessibility(method.Accessibility);
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));

        // C# declares a constructor under its type's name and without a return type, and a
        // static one without accessibility.
        if (method.IsConstructor)
        {
            var typeName = method.DeclaringTypeDefinition?.Name ?? method.Name;
            return method.IsStatic ? $"static {typeName}()" : $"{acc} {typeName}({parameters})";
        }

        var modifiers = FormatMethodModifiers(method);
        var ret = ReferenceFormatter.FormatReturnType(method);
        var typeParams = method.TypeParameters.Count > 0
            ? "<" + string.Join(", ", method.TypeParameters.Select(p => p.Name)) + ">"
            : "";
        return $"{acc}{modifiers} {ret} {method.Name}{typeParams}({parameters})";
    }

    private static string FormatPropertySignature(IProperty property)
    {
        var acc = FormatAccessibility(property.Accessibility);
        var modifiers = FormatMemberModifiers(property.IsStatic, property.IsAbstract,
            property.IsOverride, property.IsVirtual, property.IsSealed);
        var ret = ReferenceFormatter.FormatReturnType(property);

        // A property with parameters is an indexer in C#. It prints under its metadata name, Item
        // unless [IndexerName] gives another, rather than C#'s 'this', because that name is what
        // the other tools take, as in get_Item.
        var name = property.Parameters.Count > 0
            ? $"{property.Name}[{string.Join(", ", property.Parameters.Select(FormatParameter))}]"
            : property.Name;

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

    private static string FormatFieldSignature(IField field)
    {
        var acc = FormatAccessibility(field.Accessibility);
        string modifiers;
        if (field.IsConst) modifiers = " const";
        else if (field.IsStatic && field.IsReadOnly) modifiers = " static readonly";
        else if (field.IsStatic) modifiers = " static";
        else if (field.IsReadOnly) modifiers = " readonly";
        else modifiers = "";
        if (field.IsVolatile) modifiers += " volatile";

        var t = ReferenceFormatter.FormatTypeRef(field.Type);
        var line = $"{acc}{modifiers} {t} {field.Name}";

        // For a const, the literal value is the meaningful information — render it inline.
        if (field.IsConst && field.GetConstantValue() is { } value)
            line += $" = {FormatConstant(value)}";

        return line;
    }

    private static string FormatEventSignature(IEvent evt)
    {
        var acc = FormatAccessibility(evt.Accessibility);
        var modifiers = FormatMemberModifiers(evt.IsStatic, evt.IsAbstract,
            evt.IsOverride, evt.IsVirtual, evt.IsSealed);
        var t = ReferenceFormatter.FormatTypeRef(evt.ReturnType);
        return $"{acc}{modifiers} event {t} {evt.Name}";
    }

    private static string FormatMethodModifiers(IMethod m)
    {
        // Method-specific: skip "abstract"/"virtual"/"override" on interface members
        // (always implicitly abstract there) to keep signatures compact.
        if (m.DeclaringTypeDefinition?.Kind == TypeKind.Interface)
            return m.IsStatic ? " static" : "";
        return FormatMemberModifiers(m.IsStatic, m.IsAbstract, m.IsOverride, m.IsVirtual, m.IsSealed);
    }

    private static string FormatMemberModifiers(bool isStatic, bool isAbstract,
        bool isOverride, bool isVirtual, bool isSealed)
    {
        if (isStatic) return " static";
        if (isAbstract) return " abstract";
        if (isOverride) return isSealed ? " sealed override" : " override";
        if (isVirtual) return " virtual";
        return "";
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

    private static string FormatConstant(object value) => value switch
    {
        string s => "\"" + EscapeString(s) + "\"",
        char c   => "'" + EscapeChar(c) + "'",
        null     => "null",
        _        => value.ToString() ?? "?",
    };

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\n", "\\n")
         .Replace("\r", "\\r")
         .Replace("\t", "\\t");

    private static string EscapeChar(char c) => c switch
    {
        '\\' => "\\\\",
        '\'' => "\\'",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        _ => c.ToString(),
    };
}
