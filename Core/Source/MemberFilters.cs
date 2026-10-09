using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Member kinds the list_members tool can emit: the <see cref="SymbolCategory"/> values of a
/// type's members, with its nested types in place of <c>Type</c>. A constructor is a method.
/// </summary>
public enum MemberKind
{
    Method,
    Property,
    Field,
    Event,
    NestedType,
}

/// <summary>
/// Accessibility filter for the tools that list types and members. <see cref="PublicProtected"/>,
/// their default, is public and every flavor of protected.
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
