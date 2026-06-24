using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class CompareTypeTool
{
    [McpServerTool(Name = "compare_type", ReadOnly = true),
     Description("Compare a type across two assemblies — added / removed members, plus a body-changed " +
        "flag per method. No C# decompilation invoked; method-body equality uses normalized IL. " +
        "Output uses '+' for added members, '-' for removed, and '~' for method bodies that changed " +
        "while keeping the same signature.")]
    public static string CompareType(
        AssemblyHostRegistry registry,
        [Description("Path to the first assembly (must be under an allowed root).")] string assemblyA,
        [Description("Path to the second assembly (must be under an allowed root).")] string assemblyB,
        [Description("Fully qualified type name, e.g. 'System.String'.")] string typeName)
    {
        var hostA = registry.GetOrLoad(assemblyA);
        var hostB = registry.GetOrLoad(assemblyB);
        var comparer = new AssemblyComparer(hostA, hostB);

        var diff = comparer.CompareType(typeName);

        var header = diff.Side switch
        {
            AssemblyComparer.TypePresence.Added =>
                $"{typeName} is present only in B ({diff.Changes.Count} member(s)):",
            AssemblyComparer.TypePresence.Removed =>
                $"{typeName} is present only in A ({diff.Changes.Count} member(s)):",
            _ when diff.Changes.Count == 0 =>
                $"{typeName} is identical in both assemblies.",
            _ =>
                $"{typeName}: {diff.Changes.Count} member change(s):",
        };

        if (diff.Changes.Count == 0)
            return header;

        return header + "\n" + string.Join("\n", diff.Changes.Select(FormatMember));
    }

    private static string FormatMember(AssemblyComparer.MemberChange c) => c.Kind switch
    {
        AssemblyComparer.MemberChangeKind.Added       => $"+ {c.Signature}",
        AssemblyComparer.MemberChangeKind.Removed     => $"- {c.Signature}",
        AssemblyComparer.MemberChangeKind.BodyChanged => $"~ {c.Signature}  [body changed]",
        _ => $"? {c.Signature}",
    };
}
