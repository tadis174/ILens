using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Type-system traversal helpers. Centralized so the inheritance-walk boundary
/// (stop at <c>System.Object</c>, follow class chain only) lives in one place.
/// </summary>
public static class TypeWalker
{
    /// <summary>
    /// Walk the base class chain of <paramref name="type"/>, excluding interfaces
    /// and <c>System.Object</c>. Yields base types closest-first.
    /// </summary>
    public static IEnumerable<ITypeDefinition> WalkBaseTypes(ITypeDefinition type)
    {
        var current = type;
        while (true)
        {
            var baseType = current.DirectBaseTypes
                .Select(t => t.GetDefinition())
                .FirstOrDefault(t => t != null && t.Kind == TypeKind.Class);

            if (baseType == null || baseType.FullName == "System.Object")
                yield break;

            yield return baseType;
            current = baseType;
        }
    }
}
