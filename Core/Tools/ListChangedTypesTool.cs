using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class ListChangedTypesTool
{
    [McpServerTool(Name = "list_changed_types", ReadOnly = true),
     Description("Enumerate types that differ between two assemblies — added, removed, or changed " +
        "(metadata, members, or method bodies). Pure metadata + normalized IL walk; the C# decompiler " +
        "is not invoked. Members are matched by name plus parameter and return types, so any signature " +
        "change surfaces as a remove+add. Method-body equality is decided by IL disassembly with operand " +
        "tokens resolved to symbolic names — bodies whose source is unchanged digest identically across " +
        "rebuilds, even when metadata tokens around them shift. " +
        "By default hides compiler-generated noise — " + CompilerGeneratedFilter.PatternsDescription +
        " Pass excludeCompilerGenerated=false to include everything.")]
    public static string ListChangedTypes(
        AssemblyHostRegistry registry,
        [Description("Path to the first assembly (must be under an allowed root).")] string assemblyA,
        [Description("Path to the second assembly (must be under an allowed root).")] string assemblyB,
        [Description("Optional namespace filter, e.g. 'System.IO'. Omit to scan the whole module.")] string namespaceFilter = null,
        [Description("Drop types the compiler emitted (closures, anonymous types, source-generator output). Default true.")] bool excludeCompilerGenerated = true)
    {
        var hostA = registry.GetOrLoad(assemblyA);
        var hostB = registry.GetOrLoad(assemblyB);
        var comparer = new AssemblyComparer(hostA, hostB);

        var changes = comparer
            .EnumerateChangedTypes(namespaceFilter, excludeCompilerGenerated)
            .ToList();

        if (changes.Count == 0)
            return "No type changes detected.";

        var lines = changes.Select(FormatChange);
        return $"{changes.Count} change(s):\n" + string.Join("\n", lines);
    }

    private static string FormatChange(AssemblyComparer.TypeChange c) => c.Presence switch
    {
        AssemblyComparer.TypePresence.Added   => $"Added:   {c.TypeName}",
        AssemblyComparer.TypePresence.Removed => $"Removed: {c.TypeName}",
        AssemblyComparer.TypePresence.Changed => $"Changed [{FormatKinds(c.Kinds)}]: {c.TypeName}",
        _ => $"{c.Presence}: {c.TypeName}",
    };

    private static string FormatKinds(AssemblyComparer.ChangeKinds kinds)
    {
        var parts = new List<string>(3);
        if ((kinds & AssemblyComparer.ChangeKinds.Metadata) != 0) parts.Add("metadata");
        if ((kinds & AssemblyComparer.ChangeKinds.Members) != 0) parts.Add("members");
        if ((kinds & AssemblyComparer.ChangeKinds.Bodies) != 0) parts.Add("bodies");
        return string.Join(",", parts);
    }
}
