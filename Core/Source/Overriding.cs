using System.Reflection;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Whether a member overrides a member of a base class, and which one.
/// </summary>
/// <remarks>
/// <para>Ask <see cref="Of"/> and <see cref="Overridden"/> for this, never ILSpy's
/// <c>IMember.IsOverride</c>, which reads the flags alone: virtual and no new slot. Since C# 9,
/// on .NET 5 and later, an override may return a more specific type than the member it
/// overrides, <c>override Letter Copy()</c> over <c>virtual Sheet Copy()</c>. The runtime finds
/// the slot an override fills by the whole signature, so the compiler writes such a member as
/// a new slot, with a MethodImpl row that names the member it overrides and with
/// <c>PreserveBaseOverridesAttribute</c>; for a property both sit on the getter. By its flags
/// it is a new virtual member, and a sealed one is no virtual member at all.</para>
/// <para>The attribute tells such a member here, as it does for ILSpy's decompiler, which
/// prints <c>override</c> by it. It is read without the class of the overridden member, which
/// ILens's own type system does not hold when another assembly defines it; whether a MethodImpl
/// row names a class's member or an interface's is not. In 1,579 library files, of the
/// .NET 10 runtime with its desktop part, the .NET Framework, an SDK, a game and its mods,
/// counted on 2026-10-13, the methods that are a new slot with a row naming a class's member
/// and the methods with the attribute are the same 49.</para>
/// <para>The member overridden is the nearest one of the name and parameters up the base
/// classes, which is the one the row names: the declaration that introduced the member or an
/// override of it, whatever that one returns. A member declared anew, <c>new virtual</c>, has
/// such a base member as well and overrides nothing, so whether a member overrides is asked
/// before which.</para>
/// </remarks>
public static class Overriding
{
    /// <summary>True if <paramref name="member"/> overrides a member of a base class.</summary>
    public static bool Of(IMember member) => member.IsOverride || ReturnsMoreSpecific(member);

    /// <summary>
    /// The member <paramref name="member"/> overrides, or null: for a member that overrides
    /// nothing, and for an override whose base class the type system does not hold.
    /// </summary>
    /// <remarks>
    /// ILSpy's <c>InheritanceHelper.GetBaseMember</c> is not asked for the first base member as
    /// it stands. It passes over the member's own class by comparing objects, and an event
    /// builds its class anew at every question in a type system that keeps no objects, as
    /// ILens's own is. There the first base member of an event is the event itself, on which
    /// a walk up the overrides does not end.
    /// </remarks>
    public static IMember Overridden(IMember member)
    {
        if (!Of(member))
            return null;
        var own = member.MemberDefinition;
        return InheritanceHelper.GetBaseMembers(member, includeImplementedInterfaces: false)
            .FirstOrDefault(candidate => !candidate.MemberDefinition.Equals(own));
    }

    /// <summary>
    /// True for an override that no class can override again. ILSpy's <c>IsSealed</c> does not
    /// say so for a new slot.
    /// </summary>
    public static bool IsSealed(IMember member) =>
        member.IsSealed || (ReturnsMoreSpecific(member) && IsFinal(MarkedMethod(member)));

    /// <summary>
    /// True for an override with another return type than the member it overrides has, which
    /// its flags do not show as an override.
    /// </summary>
    private static bool ReturnsMoreSpecific(IMember member) =>
        MarkedMethod(member)?.HasAttribute(KnownAttribute.PreserveBaseOverrides) == true;

    /// <summary>The method that carries the compiler's mark: the method itself, or a property's getter.</summary>
    private static IMethod MarkedMethod(IMember member) => member.MemberDefinition switch
    {
        IMethod method => method,
        IProperty property => property.Getter,
        _ => null,
    };

    private static bool IsFinal(IMethod method) =>
        method is { ParentModule: MetadataModule module, MetadataToken: { IsNil: false, Kind: HandleKind.MethodDefinition } token }
        && (module.MetadataFile.Metadata.GetMethodDefinition((MethodDefinitionHandle)token).Attributes
            & MethodAttributes.Final) != 0;
}
