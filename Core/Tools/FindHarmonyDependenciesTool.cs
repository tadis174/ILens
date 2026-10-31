using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindHarmonyDependenciesTool
{
    [McpServerTool(Name = "find_harmony_dependencies", ReadOnly = true),
     Description("List what a Harmony-patching assembly depends on: every patch target, from " +
        "[HarmonyPatch] attributes, TargetMethod(s) bodies and Harmony.Patch calls; every field it reaches " +
        "through AccessTools, Traverse, Type.GetField, a transpiler or a ___name patch parameter; and every " +
        "method, property, event or constructor of another assembly it looks up by reflection, in a " +
        "transpiler or by a delegate patch parameter, unless a patch line shows its result. One line " +
        "each: the patch kind, Field, Method, Property, Event or Member, the target, '<-', the method " +
        "depending on it, and in brackets how it was found. '?' marks a part not read; a line with one " +
        "found N times ends with ×N. Unapplied marks a target named where Harmony applies no patch. A " +
        "target without parameter types names no overload. " +
        "TypedAttribute and StringTargeted are told apart in the scanned assembly, for a " +
        "string-named type and for a constructor, finalizer, operator or indexer; elsewhere both are " +
        "Attribute. Pair with compare on two builds of the patched assembly to see what still binds.")]
    public static string FindHarmonyDependencies(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        using var lease = registry.GetOrLoad(assembly);
        // A reference assembly holds no code, so no patch applied in code shows, and a compiler's
        // reference assembly also leaves out the private patch methods, so its patch classes
        // would read as applying nothing.
        if (lease.Host.IsReferenceAssembly)
            throw AssemblyHost.ReferenceAssemblyError(
                "the patches it applies and the members it looks up cannot be read from it", "ask the build the mod runs as.");
        var result = new HarmonyDependencyScanner(lease.Host).Scan();

        // Sorted by target, so everything that depends on one member stands together.
        var lines = result.Patches
            .Select(patch => (Target: TargetOf(patch), patch.PatchSite,
                Line: $"{patch.PatchType} {TargetOf(patch)} <- {patch.PatchSite} [{patch.ResolutionKind}]"))
            .Concat(result.FieldAccesses
                .Select(access => (Target: FieldOf(access), access.PatchSite,
                    Line: $"Field {FieldOf(access)} <- {access.PatchSite} [{access.Accessor}]")))
            .Concat(result.MemberLookups
                .Select(lookup => (Target: MemberOf(lookup), PatchSite: lookup.Site,
                    Line: $"{lookup.Word} {MemberOf(lookup)} <- {lookup.Site} [{lookup.Label}]")))
            .OrderBy(entry => entry.Target, StringComparer.Ordinal)
            .ThenBy(entry => entry.PatchSite, StringComparer.Ordinal)
            .Select(entry => entry.Line)
            // A line several calls give stands once, since it names one dependency. One with a part
            // not read ends with how often it was found, since what those calls name may differ.
            .GroupBy(line => line, StringComparer.Ordinal)
            .Select(group => group.Count() > 1 && UnreadPart.IsMatch(group.Key) ? $"{group.Key} ×{group.Count()}" : group.Key)
            .ToList();
        return ListOutput.Format(lines, limit, new Noun("dependency", "dependencies"));
    }

    // A '?' for a part the scan could not read. A '?' right after a name or a '>' is C#'s mark of a
    // nullable value type, as in int? or KeyValuePair<int, int>?, which a parameter type can carry.
    private static readonly Regex UnreadPart = new(@"(?<![\w>])\?", RegexOptions.CultureInvariant);

    /// <summary>
    /// A field as Class.field, with '?' for a part the scan could not read, and '?' alone when it
    /// could read neither.
    /// </summary>
    private static string FieldOf(HarmonyDependencyScanner.FieldAccess access) =>
        access is { ContextType: "?", FieldName: "?" } ? "?" : $"{access.ContextType}.{access.FieldName}";

    /// <summary>
    /// A member looked up as Class.member, with the parameter types when the lookup pins an
    /// overload, '?' for a part the scan could not read, and '?' alone when it could read neither.
    /// </summary>
    private static string MemberOf(HarmonyDependencyScanner.MemberLookup lookup)
    {
        if (lookup.ContextType is null && lookup.MemberName is null)
            return "?";
        var parameters = lookup.ParamTypes is null ? "" : $"({string.Join(", ", lookup.ParamTypes)})";
        return $"{lookup.ContextType ?? "?"}.{lookup.MemberName ?? "?"}{parameters}";
    }

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
