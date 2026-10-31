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
    /// An array is matched by the brackets that end the pattern, read as C# writes them, the
    /// outermost array's first and a comma for each dimension past the first: <c>int[,]</c>
    /// matches a two-dimensional array and <c>int[][,]</c> an array of them, and <c>[]</c> an
    /// array of any number of dimensions. Pointers via trailing <c>*</c>. A tuple matches
    /// as the <c>ValueTuple&lt;...&gt;</c> it prints as, and <c>delegate*</c> matches any
    /// function pointer. <c>Nullable&lt;T&gt;</c> is unwrapped
    /// so <c>int</c> matches <c>int?</c>. A trailing <c>?</c> is read as C# reads it: after a
    /// value type it is that type's <c>Nullable&lt;T&gt;</c>, so <c>int?</c> matches only
    /// <c>int?</c>, and after any other type it is the nullable annotation, which C# lets no two
    /// overloads differ in alone, so <c>string?</c> matches what <c>string</c> matches.
    /// <c>ref</c>/<c>out</c>/<c>in</c> surface as
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
    public static bool Matches(IType type, string pattern) => Matches(type, pattern, exact: false);

    /// <summary>
    /// <see cref="Matches(IType, string)"/>, or with <paramref name="exact"/> a fit of the type as
    /// ILens writes it: a pattern without a keyword fits only a type that is no reference,
    /// <c>Nullable&lt;T&gt;</c> is not unwrapped, so <c>int</c> does not fit <c>int?</c>, and a
    /// name without type arguments fits only a type without them, so <c>List</c> fits no
    /// <c>List&lt;T&gt;</c>, and <c>[]</c> fits only an array of one dimension, so <c>int[]</c>
    /// fits no <c>int[,]</c>. Type arguments and the elements of arrays and pointers are held to
    /// the same rules. A trailing <c>?</c> fits a type parameter that is no value type only where
    /// the code marks it <c>T?</c> (see <see cref="NullableMatches"/>). The forms of a name, an
    /// unbound arity and <c>delegate*</c> fit as in the loose match. A hint is held as an exact
    /// fit first, so that a method whose parameter types the caller wrote is reached beside
    /// namesakes the loose match also fits.
    /// </summary>
    public static bool Matches(IType type, string pattern, bool exact)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim();
        var referenced = type;
        while (referenced is ModifiedType modified)
            referenced = modified.ElementType;
        if (PassingKeyword.Match(pattern) is { Success: true } keyword)
        {
            if (referenced is not ByReferenceType)
                return false;
            pattern = pattern[keyword.Length..];
        }
        else if (exact && referenced is ByReferenceType)
            return false;

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

        if (pattern.EndsWith('?'))
        {
            return NullableMatches(target, pattern[..^1], exact);
        }
        if (NamesAnArray(pattern, out var element, out var rank))
        {
            // Held loosely, '[]' names an array of any number of dimensions, as 'int' names int?.
            return target is ArrayType arr
                && (arr.Dimensions == rank || (rank == 1 && !exact))
                && Matches(arr.ElementType, element, exact);
        }
        if (pattern.EndsWith('*'))
        {
            return target is PointerType pointer && Matches(pointer.ElementType, pattern[..^1], exact);
        }
        if (target is ArrayType or PointerType) return false;

        var parsed = ParsedTypeName.Parse(pattern);
        if (parsed == null) return false;

        if (!parsed.HasArity)
        {
            if (exact)
            {
                // A name without type arguments is the type without them.
                if (target is ParameterizedType) return false;
            }
            else
            {
                // Unwrap Nullable<T> so 'int' matches 'int?'. Done before generic erasing
                // so the inner T is what's matched, not the open Nullable type.
                if (ReferenceFormatter.NullableUnderlying(target) is { } underlying)
                {
                    target = underlying;
                }

                // Generic-erasing: List<int> matches by GenericType ('List')
                if (target is ParameterizedType pt) target = pt.GenericType;
            }

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
                && ArgumentsMatch(parsed, arities, withArguments.TypeArguments, exact));
    }

    /// <summary>
    /// A pattern that ends with '?', <paramref name="inner"/> without it, read as C# reads it.
    /// After a value type the '?' makes that type's <c>Nullable&lt;T&gt;</c>, so the pattern fits a
    /// <c>Nullable&lt;T&gt;</c> whose T fits <paramref name="inner"/> and never the value type
    /// itself. After any other type it is the nullable annotation, which C# lets no two overloads
    /// differ in alone, so it is passed over: <c>string?</c> fits what <c>string</c> fits.
    /// </summary>
    /// <remarks>
    /// After a type parameter that is no value type the '?' can name either of two types that
    /// overload each other, the type parameter and <c>Nullable&lt;T&gt;</c>, as in
    /// <c>M&lt;T&gt;(T) where T : class</c> beside <c>M&lt;T&gt;(T?) where T : struct</c>. Held
    /// exactly, it therefore fits the type parameter only where the code marks it <c>T?</c> as
    /// well, by the annotation the type system keeps; held loosely, it fits it either way. Without
    /// that rule <c>T?</c> fits both such methods, and the overload error lists the second as
    /// <c>T?</c>, which leads back to the error. Over 665 library files of a game, its mods and the
    /// .NET runtime, one such pair was counted.
    /// </remarks>
    private static bool NullableMatches(IType target, string inner, bool exact)
    {
        if (ReferenceFormatter.NullableUnderlying(target) is { } underlying)
            return Matches(underlying, inner, exact);
        if (target.IsReferenceType == false)
            return false;
        if (exact && target is ITypeParameter && target.Nullability != Nullability.Nullable)
            return false;
        return Matches(target, inner, exact);
    }

    /// <summary>
    /// True if the pattern names an array, read as C# writes one: the brackets that end the
    /// pattern belong to the arrays from the outermost inward, so <c>int[][,]</c> is an array of
    /// two-dimensional arrays. <paramref name="rank"/> is the outermost array's number of
    /// dimensions, one more than the commas in its brackets, and <paramref name="element"/> the
    /// pattern without those brackets, which names its element. False for a pattern that ends
    /// in no such brackets.
    /// </summary>
    internal static bool NamesAnArray(string pattern, out string element, out int rank)
    {
        (int Start, int End) outermost = (-1, -1);
        var end = pattern.Length;
        while (end > 0 && pattern[end - 1] == ']')
        {
            var open = pattern.LastIndexOf('[', end - 1);
            if (open < 0 || pattern[(open + 1)..(end - 1)].Any(c => c != ',' && !char.IsWhiteSpace(c)))
                break;
            outermost = (open, end);
            end = open;
        }
        if (outermost.Start < 0)
        {
            element = null;
            rank = 0;
            return false;
        }
        element = pattern[..outermost.Start] + pattern[outermost.End..];
        rank = pattern[outermost.Start..outermost.End].Count(c => c == ',') + 1;
        return true;
    }

    /// <summary>
    /// A list of patterns with every text that holds several of them split into its parts, so
    /// that <c>"string, int"</c> is read as the two patterns it was written for, on its own or
    /// as an element among others. A comma inside angle or square brackets belongs to one type,
    /// as in <c>Dictionary&lt;int, string&gt;</c> or <c>int[,]</c>. Outside them it has no
    /// reading as part of one: ILens prints no type with a comma there, a tuple included, which
    /// prints as <c>ValueTuple&lt;...&gt;</c>. Left unsplit, the text would be one pattern that
    /// no type matches, and "no method takes this" the answer to a question nobody asked.
    /// </summary>
    public static string[] SplitPatterns(string[] patterns) =>
        patterns?.SelectMany(ParsedTypeName.SplitOutsideBrackets).ToArray();

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
        ParsedTypeName parsed, IReadOnlyList<int> arities, IReadOnlyList<IType> arguments, bool exact)
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
                    if (position + j >= arguments.Count || !Matches(arguments[position + j], patterns[j], exact))
                        return false;
                }
            }
            position += arity;
        }
        return true;
    }
}
