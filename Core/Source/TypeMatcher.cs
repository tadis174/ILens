using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Matches an <see cref="IType"/> against a user-supplied string pattern. Used by
/// tools that accept type filters from MCP clients (e.g. find_methods's parameter-type
/// and return-type filters). The matching rules are intentionally loose so an LLM
/// can specify types without knowing the exact reflection name.
/// </summary>
public static class TypeMatcher
{
    /// <summary>
    /// Match a type against a pattern. Accepts the type's short name (<c>Boolean</c>),
    /// full name (<c>System.Boolean</c>), or — for the well-known framework types —
    /// its C# keyword (<c>bool</c>) the way <see cref="ReferenceFormatter.FormatTypeRef"/>
    /// renders it. Generic-erasing — <c>List</c> matches any <c>List&lt;T&gt;</c>.
    /// Arrays matched via trailing <c>[]</c>. <c>Nullable&lt;T&gt;</c> is unwrapped
    /// so <c>int</c> matches <c>int?</c>. <c>ref</c>/<c>out</c>/<c>in</c> surface as
    /// <see cref="ByReferenceType"/> and are peeled before any other shape check,
    /// so <c>int[]</c> matches <c>ref int[]</c>.
    /// </summary>
    public static bool Matches(IType type, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        // ref / out / in surface as ByReferenceType — peel first so 'int[]' matches
        // 'ref int[]' too. If we checked ArrayType before peeling, a ByReferenceType
        // wrapping an ArrayType would never reach the array branch.
        var target = type;
        if (target is ByReferenceType byRef) target = byRef.ElementType;

        if (pattern.EndsWith("[]"))
        {
            return target is ArrayType arr && Matches(arr.ElementType, pattern[..^2]);
        }
        if (target is ArrayType) return false;

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

        return target.Name == pattern
            || target.FullName == pattern
            || ReferenceFormatter.TypeKeyword(target) == pattern;
    }
}
