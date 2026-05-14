using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Member kinds the list_members tool can emit. A subset of <see cref="SymbolCategory"/>
/// without <c>Type</c>, since types aren't members of other types in the relevant sense.
/// </summary>
public enum MemberKind
{
    Method,
    Property,
    Field,
    Event,
}

/// <summary>
/// Accessibility filter for member-listing tools. <see cref="PublicProtected"/> matches
/// what summarize_type emits today (public + every flavor of protected).
/// </summary>
public enum AccessibilityFilter
{
    Public,
    PublicProtected,
    All,
}

/// <summary>
/// Helpers on <see cref="AccessibilityFilter"/>. Shared by tools that filter members
/// (list_members, find_methods) so the inclusion logic stays in one place.
/// </summary>
public static class AccessibilityFilterExtensions
{
    public static bool Permits(this AccessibilityFilter filter, Accessibility actual) =>
        filter switch
        {
            AccessibilityFilter.Public => actual == Accessibility.Public,
            AccessibilityFilter.PublicProtected =>
                actual is Accessibility.Public
                    or Accessibility.Protected
                    or Accessibility.ProtectedOrInternal
                    or Accessibility.ProtectedAndInternal,
            // AccessibilityFilter.All and any future / unknown enum value fall through here.
            _ => true,
        };
}
