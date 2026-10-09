using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindHarmonyDependenciesTool
{
    [McpServerTool(Name = "find_harmony_dependencies", ReadOnly = true),
     Description("List what a Harmony-patching assembly depends on: every patch target, from " +
        "[HarmonyPatch] attributes, TargetMethod or TargetMethods bodies and Harmony.Patch calls, and every field it reaches " +
        "through AccessTools, Traverse or Type.GetField, or takes as a ___name patch parameter. One line " +
        "each: the patch kind or Field, the target, '<-' and the method that depends on it, and in " +
        "brackets how the target was found. A target without parameter types names no overload. " +
        "TypedAttribute and StringTargeted are told apart in the scanned assembly, for a " +
        "string-named type and for a constructor, finalizer, operator or indexer; elsewhere both are " +
        "Attribute. Pair with compare against two versions of the patched assembly to see whether " +
        "the patches still bind.")]
    public static string FindHarmonyDependencies(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        using var lease = registry.GetOrLoad(assembly);
        var result = new HarmonyDependencyScanner(lease.Host).Scan();

        // Sorted by target, so everything that depends on one member stands together.
        var lines = result.Patches
            .Select(patch => (Target: TargetOf(patch), patch.PatchSite,
                Line: $"{patch.PatchType} {TargetOf(patch)} <- {patch.PatchSite} [{patch.ResolutionKind}]"))
            .Concat(result.FieldAccesses
                .Select(access => (Target: FieldOf(access), access.PatchSite,
                    Line: $"Field {FieldOf(access)} <- {access.PatchSite} [{access.Accessor}]")))
            .OrderBy(entry => entry.Target, StringComparer.Ordinal)
            .ThenBy(entry => entry.PatchSite, StringComparer.Ordinal)
            .Select(entry => entry.Line)
            .ToList();
        return ListOutput.Format(lines, limit, new Noun("dependency", "dependencies"));
    }

    /// <summary>
    /// A field as Class.field, with '?' for a part the scan could not read, and '?' alone when it
    /// could read neither.
    /// </summary>
    private static string FieldOf(HarmonyDependencyScanner.FieldAccess access) =>
        access is { ContextType: "?", FieldName: "?" } ? "?" : $"{access.ContextType}.{access.FieldName}";

    /// <summary>
    /// The patched member as the other tools name it: its type, its name and, when the patch
    /// pins an overload, its parameter types. A method kind the name does not show, such as an
    /// enumerator's, follows in parentheses.
    /// </summary>
    private static string TargetOf(HarmonyDependencyScanner.Patch patch)
    {
        if (patch.TargetType == "?")
            return "?";
        var member = patch.TargetMember is null ? "" : "." + patch.TargetMember;
        var parameters = patch.ParamTypes is null ? "" : $"({string.Join(", ", patch.ParamTypes)})";
        var kind = patch.MethodType is null ? "" : $" ({patch.MethodType})";
        return patch.TargetType + member + parameters + kind;
    }
}
