using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Renders symbols and types as call-site references — fully qualified names,
/// no accessibility/modifiers. Used by analyze tool output.
/// For declaration-style signatures see <see cref="SignatureFormatter"/>.
/// </summary>
public static class Formatter
{
    /// <summary>
    /// Format a list of symbols as one fully-qualified name per line.
    /// When <paramref name="limit"/> is set, caps output at that many lines and
    /// appends a truncation marker if more matches existed.
    /// </summary>
    public static string FormatSymbolList(IEnumerable<ISymbol> symbols, int? limit = null)
    {
        var lines = symbols.Select(FormatSymbol).Distinct().ToList();
        if (lines.Count == 0)
            return "(no results)";

        if (limit.HasValue && lines.Count > limit.Value)
        {
            var capped = lines.Take(limit.Value);
            return string.Join("\n", capped) +
                $"\n... (truncated, {lines.Count - limit.Value} more matches; raise limit to see all)";
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Format a single symbol to its display name.
    /// </summary>
    public static string FormatSymbol(ISymbol symbol)
    {
        return symbol switch
        {
            IMethod method => FormatMethodReference(method),
            IProperty prop => $"{prop.DeclaringType.FullName}.{prop.Name}",
            IField field => $"{field.DeclaringType.FullName}.{field.Name}",
            IEvent evt => $"{evt.DeclaringType.FullName}.{evt.Name}",
            ITypeDefinition type => type.FullName,
            IType type => type.FullName,
            IEntity entity => entity.FullName,
            _ => symbol.ToString() ?? "(unknown)"
        };
    }

    /// <summary>
    /// Format a method as a fully-qualified call-site reference with its parameter
    /// types in parentheses, e.g. <c>Verse.Pawn.GetThing(int, bool)</c>.
    /// Uses C# keywords for well-known framework types and unfolds generics.
    /// </summary>
    public static string FormatMethodReference(IMethod method)
    {
        var parameters = string.Join(", ",
            method.Parameters.Select(p => FormatTypeRef(p.Type)));
        return $"{method.DeclaringType.FullName}.{method.Name}({parameters})";
    }

    /// <summary>
    /// Render an <see cref="IType"/> as a compact C#-style type reference,
    /// recursively unfolding generics and arrays. Uses C# keywords for the
    /// well-known framework types (e.g., <c>float</c>, <c>string</c>) and
    /// short names elsewhere.
    /// </summary>
    public static string FormatTypeRef(IType type)
    {
        if (type is ParameterizedType pt)
        {
            var args = string.Join(", ", pt.TypeArguments.Select(FormatTypeRef));
            return $"{TypeKeyword(pt.GenericType) ?? pt.GenericType.Name}<{args}>";
        }
        if (type is ArrayType arr)
        {
            return FormatTypeRef(arr.ElementType) + "[]";
        }
        if (type is ByReferenceType byRef)
        {
            return FormatTypeRef(byRef.ElementType);
        }
        return TypeKeyword(type) ?? type.Name;
    }

    private static string TypeKeyword(IType type) =>
        type.Namespace == "System" ? type.Name switch
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
            "Object"  => "object",
            "String"  => "string",
            "Void"    => "void",
            _ => null,
        } : null;
}
