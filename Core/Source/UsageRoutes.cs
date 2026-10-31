using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// The lines an answer about who uses a member ends with when code can reach the member
/// without naming it: through an interface member it implements, and through the declaration
/// it overrides. The answer lists only what names the member itself, so without these lines
/// an empty one would read as proof that nothing reaches the member.
/// </summary>
public static class UsageRoutes
{
    /// <summary>
    /// The lines for <paramref name="member"/>, a member of the inspected assembly, none when
    /// it neither implements nor overrides. <paramref name="listsCalls"/> is passed on to
    /// <see cref="ReferenceFormatter.FormatOverrideRoute"/>.
    /// </summary>
    /// <remarks>
    /// A member can have both lines: an override is the member a class binds an interface
    /// member to where the declaration it overrides implements nothing, as in a class that
    /// lists the interface below the class that introduced the virtual member. What the member
    /// implements is read as <c>implements</c> reads it
    /// (<see cref="AssemblyHost.FindImplementedInterfaceMembers"/>). Where a base type of its
    /// class was not read, the line that names interface members may be missing or incomplete,
    /// and a last line says so, since its absence would read as "implements nothing".
    /// </remarks>
    public static IReadOnlyList<string> For(AssemblyHost host, IMember member, bool listsCalls)
    {
        var lines = new List<string>();
        var (implemented, unread) = host.FindImplementedInterfaceMembers(member);
        if (ReferenceFormatter.FormatImplementationRoute(implemented.Select(found => found.Member)) is { } implementation)
            lines.Add(implementation);
        if (ReferenceFormatter.FormatOverrideRoute(member, listsCalls) is { } overridden)
            lines.Add(overridden);
        if (unread.Count > 0)
            lines.Add(
                $"Not checked: whether it implements {(implemented.Count == 0 ? "an interface member" : "more interface members")}; " +
                UnreadLibrary.Reason(unread.Select(unreadBase => (unreadBase.TypeName, unreadBase.Libraries))));
        return lines;
    }
}
