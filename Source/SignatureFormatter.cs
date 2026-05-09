using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Renders members and parameters as one-line C#-style declaration signatures
/// (no method bodies, no XML doc). Used by list_members and find_methods.
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
    /// Render a parameter as <c>[modifier ]Type name</c> — handles <c>ref</c>, <c>out</c>,
    /// <c>in</c>, and <c>params</c>. Type rendering uses <see cref="Formatter.FormatTypeRef"/>.
    /// </summary>
    public static string FormatParameter(IParameter p)
    {
        var prefix = "";
        if (p.ReferenceKind == ReferenceKind.Ref)  prefix = "ref ";
        else if (p.ReferenceKind == ReferenceKind.Out) prefix = "out ";
        else if (p.ReferenceKind == ReferenceKind.In)  prefix = "in ";
        else if (p.IsParams) prefix = "params ";
        return $"{prefix}{Formatter.FormatTypeRef(p.Type)} {p.Name}";
    }

    private static string FormatMethodSignature(IMethod method)
    {
        var acc = FormatAccessibility(method.Accessibility);
        var modifiers = FormatMethodModifiers(method);
        var ret = Formatter.FormatTypeRef(method.ReturnType);
        var typeParams = method.TypeParameters.Count > 0
            ? "<" + string.Join(", ", method.TypeParameters.Select(p => p.Name)) + ">"
            : "";
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        return $"{acc}{modifiers} {ret} {method.Name}{typeParams}({parameters})";
    }

    private static string FormatPropertySignature(IProperty property)
    {
        var acc = FormatAccessibility(property.Accessibility);
        var modifiers = FormatMemberModifiers(property.IsStatic, property.IsAbstract,
            property.IsOverride, property.IsVirtual, property.IsSealed);
        var ret = Formatter.FormatTypeRef(property.ReturnType);

        var accessors = new StringBuilder();
        if (property.CanGet)
            AppendAccessor(accessors, "get", property.Getter, property.Accessibility);
        if (property.CanSet)
            AppendAccessor(accessors, "set", property.Setter, property.Accessibility);

        return $"{acc}{modifiers} {ret} {property.Name} {{ {accessors}}}";
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

        var t = Formatter.FormatTypeRef(field.Type);
        var line = $"{acc}{modifiers} {t} {field.Name}";

        // For consts, the literal value carries the gameplay-relevant information.
        if (field.IsConst && field.GetConstantValue() is { } value)
            line += $" = {FormatConstant(value)}";

        return line;
    }

    private static string FormatEventSignature(IEvent evt)
    {
        var acc = FormatAccessibility(evt.Accessibility);
        var modifiers = FormatMemberModifiers(evt.IsStatic, evt.IsAbstract,
            evt.IsOverride, evt.IsVirtual, evt.IsSealed);
        var t = Formatter.FormatTypeRef(evt.ReturnType);
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
