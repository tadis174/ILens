using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Matches an <see cref="IType"/> against a user-supplied string pattern. Used by
/// tools that accept type filters from MCP clients (e.g. find_methods's parameter-type
/// and return-type filters). The matching rules are intentionally loose so an LLM
/// can specify types without knowing the exact reflection name.
/// </summary>
public static class TypeMatcher
{
    private static readonly Regex PassingKeyword = new(@"^(ref\s+readonly|ref|out|in)\s+");

    /// <summary>
    /// Match a type against a pattern. Accepts the type's short name (<c>Boolean</c>),
    /// full name (<c>System.Boolean</c>, a nested type's with '.' or '+'), the end of its
    /// full name after any '.' (<c>Dictionary.Entry</c>), or — for the
    /// well-known framework types — its C# keyword (<c>bool</c>) the way
    /// <see cref="ReferenceFormatter.FormatTypeRef"/> renders it. A generic name without
    /// arity erases the arguments — <c>List</c> matches any <c>List&lt;T&gt;</c>; an unbound
    /// arity matches any arguments of that count — <c>List&lt;&gt;</c> or <c>List`1</c>; and
    /// an argument list compares each argument as a pattern of its own —
    /// <c>List&lt;int&gt;</c> matches only <c>List&lt;int&gt;</c>, which lets a type copied
    /// from ILens's own output match what it came from.
    /// Arrays matched via trailing <c>[]</c>, pointers via trailing <c>*</c>. A tuple matches
    /// as the <c>ValueTuple&lt;...&gt;</c> it prints as, and <c>delegate*</c> matches any
    /// function pointer. <c>Nullable&lt;T&gt;</c> is unwrapped
    /// so <c>int</c> matches <c>int?</c>. <c>ref</c>/<c>out</c>/<c>in</c> surface as
    /// <see cref="ByReferenceType"/>, and a modifier such as <c>volatile</c>'s <c>modreq</c> as
    /// <see cref="ModifiedType"/>. Both are peeled before any other shape check,
    /// so <c>int[]</c> matches <c>ref int[]</c>. A pattern that starts with the keyword ILens
    /// prints before a type passed or returned by reference, <c>ref</c>, <c>out</c>, <c>in</c> or
    /// <c>ref readonly</c>, matches only such a type, so <c>ref int</c> tells <c>M(ref int)</c>
    /// from <c>M(int)</c>. Which of the keywords it is goes unchecked: the type is the same
    /// reference for all four, and C# lets no two overloads differ in them alone. Whatever the
    /// rules above leave out, the type
    /// written as <see cref="ReferenceFormatter.FormatTypeRef"/> prints it matches, whitespace
    /// aside, which covers a function pointer's signature.
    /// </summary>
    public static bool Matches(IType type, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim();
        if (PassingKeyword.Match(pattern) is { Success: true } keyword)
        {
            var referenced = type;
            while (referenced is ModifiedType modified)
                referenced = modified.ElementType;
            if (referenced is not ByReferenceType)
                return false;
            pattern = pattern[keyword.Length..];
        }

        // A type copied from ILens's own output always finds what it came from.
        if (Compact(pattern) == Compact(ReferenceFormatter.FormatTypeRef(type))) return true;

        // ref / out / in surface as ByReferenceType, and a modifier as ModifiedType — peel
        // both first so 'int[]' matches 'ref int[]' too. If we checked ArrayType before
        // peeling, a ByReferenceType wrapping an ArrayType would never reach the array branch.
        var target = type;
        while (target is ByReferenceType or ModifiedType)
            target = ((TypeWithElementType)target).ElementType;
        if (target is TupleType tuple) target = tuple.UnderlyingType;

        if (target is FunctionPointerType) return Compact(pattern) == "delegate*";

        if (pattern.EndsWith("[]"))
        {
            return target is ArrayType arr && Matches(arr.ElementType, pattern[..^2]);
        }
        if (pattern.EndsWith('*'))
        {
            return target is PointerType pointer && Matches(pointer.ElementType, pattern[..^1]);
        }
        if (target is ArrayType or PointerType) return false;

        var parsed = ParsedTypeName.Parse(pattern);
        if (parsed == null) return false;

        if (!parsed.HasArity)
        {
            // Unwrap Nullable<T> so 'int' matches 'int?'. Done before generic erasing
            // so the inner T is what's matched, not the open Nullable type.
            if (target is ParameterizedType nullablePt
                && nullablePt.TypeArguments.Count == 1
                && nullablePt.GenericType.FullName == "System.Nullable")
            {
                target = nullablePt.TypeArguments[0];
            }

            // Generic-erasing: List<int> matches by GenericType ('List')
            if (target is ParameterizedType pt) target = pt.GenericType;

            return NameMatches(target, parsed.BareName)
                || ReferenceFormatter.TypeKeyword(target) == pattern;
        }

        var generic = target is ParameterizedType constructed ? constructed.GenericType : target;
        if (!NameMatches(generic, parsed.BareName)
            || ReferenceFormatter.FullTypeNameOf(generic) is not { } name)
            return false;

        var arities = ParsedTypeName.SegmentArities(name);
        if (!parsed.ArityAgrees(arities)) return false;

        return !parsed.HasArguments
            || (target is ParameterizedType withArguments
                && ArgumentsMatch(parsed, arities, withArguments.TypeArguments));
    }

    /// <summary>
    /// True if <paramref name="bareName"/> is the type's name, its full name, or the end of
    /// its full name after any '.': <c>Entry</c>, <c>Dictionary.Entry</c> and
    /// <c>System.Collections.Generic.Dictionary.Entry</c> all name the same type.
    /// </summary>
    /// <remarks>
    /// ILSpy reads <c>System.IntPtr</c> and <c>System.UIntPtr</c> in a signature as special types
    /// named after their C# keywords, <c>nint</c> and <c>nuint</c>, with no namespace, so their
    /// .NET names are supplied here.
    /// </remarks>
    private static bool NameMatches(IType type, string bareName)
    {
        var fullName = type.Kind switch
        {
            TypeKind.NInt => "System.IntPtr",
            TypeKind.NUInt => "System.UIntPtr",
            _ => type.FullName,
        };
        return type.Name == bareName || fullName == bareName
            || fullName.EndsWith("." + bareName, StringComparison.Ordinal);
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// True if each argument the pattern lists matches the type argument in its place. A
    /// constructed type lists its arguments segment by segment, outermost first, so a
    /// segment's own arguments start after those of every segment before it.
    /// </summary>
    private static bool ArgumentsMatch(
        ParsedTypeName parsed, IReadOnlyList<int> arities, IReadOnlyList<IType> arguments)
    {
        var offset = arities.Count - parsed.Segments.Count;
        var position = arities.Take(offset).Sum();
        for (var i = 0; i < parsed.Segments.Count; i++)
        {
            var arity = arities[offset + i];
            if (parsed.Segments[i].Arguments is { } patterns)
            {
                for (var j = 0; j < arity; j++)
                {
                    if (position + j >= arguments.Count || !Matches(arguments[position + j], patterns[j]))
                        return false;
                }
            }
            position += arity;
        }
        return true;
    }
}
