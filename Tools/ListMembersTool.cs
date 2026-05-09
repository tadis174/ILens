using System.ComponentModel;
using System.Text;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class ListMembersTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "list_members", ReadOnly = true),
     Description("List members of a type, grouped by kind (Methods/Properties/Fields/Events). " +
        "Returns one signature per line — no method bodies, no XML doc. " +
        "Filter by member kind, accessibility, and case-insensitive name pattern. " +
        "Cheaper than summarize_type when you only need part of the API surface.")]
    public static string ListMembers(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'RimWorld.PlantProperties'.")] string typeName,
        [Description("Member kinds to include. Omit to include all four (Method, Property, Field, Event).")] MemberKind[] kinds = null,
        [Description("Accessibility filter. Default: PublicProtected.")] AccessibilityFilter accessibility = AccessibilityFilter.PublicProtected,
        [Description("Optional case-insensitive substring filter on member name.")] string namePattern = null,
        [Description("Include members declared on base types (stops at System.Object). Default: false.")] bool includeInherited = false,
        [Description("Cap on total result lines across all kinds. Default: 100.")] int? limit = null)
    {
        var (_, resolver) = registry.GetOrLoad(assembly);
        var type = resolver.ResolveType(typeName);

        var kindSet = NormalizeKinds(kinds);
        var typesToScan = TypesToScan(type, includeInherited);
        var empty = new List<(IMember Member, string Origin)>();

        // Collect once per kind so per-section counts are accurate before truncation.
        // IEnumerable<T> is covariant, so the lambdas return IEnumerable<IMember> implicitly.
        var methods    = kindSet.Contains(MemberKind.Method)
            ? Collect(typesToScan, t => t.Methods.Where(m => !m.IsConstructor && !m.IsAccessor),
                accessibility, namePattern, type) : empty;
        var properties = kindSet.Contains(MemberKind.Property)
            ? Collect(typesToScan, t => t.Properties, accessibility, namePattern, type) : empty;
        var fields     = kindSet.Contains(MemberKind.Field)
            ? Collect(typesToScan, t => t.Fields, accessibility, namePattern, type) : empty;
        var events     = kindSet.Contains(MemberKind.Event)
            ? Collect(typesToScan, t => t.Events, accessibility, namePattern, type) : empty;

        var totalAvailable = methods.Count + properties.Count + fields.Count + events.Count;
        if (totalAvailable == 0)
            return $"{type.FullName}: no members match the filter.";

        var cap = limit ?? DefaultLimit;
        var sb = new StringBuilder();
        sb.Append(type.FullName);
        if (includeInherited) sb.Append(" (with inherited)");
        sb.Append(":\n");

        var emitted = 0;
        emitted += AppendSection(sb, "Methods",    methods,    cap, emitted);
        emitted += AppendSection(sb, "Properties", properties, cap, emitted);
        emitted += AppendSection(sb, "Fields",     fields,     cap, emitted);
        emitted += AppendSection(sb, "Events",     events,     cap, emitted);

        if (emitted < totalAvailable)
            sb.Append($"\n... (truncated, {totalAvailable - emitted} more members; raise limit to see all)");

        return sb.ToString().TrimEnd();
    }

    private static HashSet<MemberKind> NormalizeKinds(MemberKind[] kinds)
    {
        if (kinds is null || kinds.Length == 0)
            return new HashSet<MemberKind>
                { MemberKind.Method, MemberKind.Property, MemberKind.Field, MemberKind.Event };
        return new HashSet<MemberKind>(kinds);
    }

    private static List<ITypeDefinition> TypesToScan(ITypeDefinition type, bool includeInherited)
    {
        var list = new List<ITypeDefinition> { type };
        if (includeInherited)
            list.AddRange(TypeWalker.WalkBaseTypes(type));
        return list;
    }

    private static List<(IMember Member, string Origin)> Collect(
        List<ITypeDefinition> typesToScan,
        Func<ITypeDefinition, IEnumerable<IMember>> selector,
        AccessibilityFilter accessibility,
        string namePattern,
        ITypeDefinition declaredOn)
    {
        // Walk derived → base. Dedup by member identity so an override hides the base.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(IMember, string)>();

        foreach (var t in typesToScan)
        {
            var origin = ReferenceEquals(t, declaredOn) ? "" : $"  [from {t.FullName}]";
            foreach (var member in selector(t))
            {
                if (!accessibility.Permits(member.Accessibility)) continue;
                if (!PassesNamePattern(member.Name, namePattern)) continue;

                var key = MemberKey(member);
                if (!seen.Add(key)) continue;
                results.Add((member, origin));
            }
        }

        return results;
    }

    private static string MemberKey(IMember member) => member switch
    {
        // Distinguish overloads on the same kind by parameter types.
        IMethod m => $"{member.SymbolKind}:{m.Name}({string.Join(",", m.Parameters.Select(p => p.Type.ReflectionName))})",
        _ => $"{member.SymbolKind}:{member.Name}",
    };

    private static bool PassesNamePattern(string name, string pattern) =>
        string.IsNullOrEmpty(pattern)
            || name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static int AppendSection(StringBuilder sb, string label,
        List<(IMember Member, string Origin)> members, int cap, int alreadyEmitted)
    {
        if (members.Count == 0) return 0;

        var remaining = cap - alreadyEmitted;
        if (remaining <= 0) return 0;

        var emitting = Math.Min(members.Count, remaining);
        sb.Append($"{label} ({members.Count}):\n");
        for (int i = 0; i < emitting; i++)
        {
            var (member, origin) = members[i];
            sb.Append("  ");
            sb.Append(SignatureFormatter.FormatMember(member));
            sb.Append(origin);
            sb.Append('\n');
        }
        return emitting;
    }
}
