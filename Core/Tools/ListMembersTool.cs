using System.ComponentModel;
using System.Text;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class ListMembersTool
{
    [McpServerTool(Name = "list_members", ReadOnly = true),
     Description("List a type's members, grouped by kind, one declaration per line without bodies. " +
        "The header gives the type's declaration and base types. A constructor is listed under the " +
        "type's name among the methods, a member inherited from a base type is marked [from Base], " +
        "and an obsolete one [Obsolete].")]
    public static string ListMembers(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.TypeName)] string type_name,
        [Description("Kinds of member to list, all when omitted.")] MemberKind[] kinds = null,
        [Description(ParameterDescriptions.Accessibility)] AccessibilityFilter accessibility = AccessibilityFilter.PublicProtected,
        [Description(ParameterDescriptions.NameContains)] string name_contains = null,
        [Description("Include members declared on base types (stops at System.Object).")] bool include_inherited = false,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        using var lease = registry.GetOrLoad(assembly);
        var type = lease.Host.Resolver.ResolveType(type_name);

        var kindSet = kinds is { Length: > 0 } ? kinds.ToHashSet() : Enum.GetValues<MemberKind>().ToHashSet();
        var typesToScan = new List<IType> { type };
        UnreadBase unread = null;
        if (include_inherited)
        {
            // The base classes another assembly defines come from the assembly's references,
            // since ILens's own type system holds none of them.
            typesToScan.AddRange(TypeWalker.WalkConstructedBaseTypes(type));
            var external = lease.Host.ReadExternalBases(type);
            typesToScan.AddRange(external.Levels);
            unread = external.Unread;
        }
        bool Passes(IEntity entity) =>
            accessibility.Permits(entity.Accessibility)
            && (string.IsNullOrEmpty(name_contains)
                || entity.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase));

        // The type's own constructors only: C# does not inherit them.
        var groups = new List<(Noun Noun, List<string> Lines)>();
        if (kindSet.Contains(MemberKind.Method))
            groups.Add((Noun.Regular("method"), Collect(typesToScan, type,
                t => t.Methods.Where(m => !m.IsAccessor && (!m.IsConstructor || t.Equals(type))),
                Passes, include_compiler_generated)));
        if (kindSet.Contains(MemberKind.Property))
            groups.Add((new Noun("property", "properties"), Collect(typesToScan, type, t => t.Properties,
                Passes, include_compiler_generated)));
        if (kindSet.Contains(MemberKind.Field))
            groups.Add((Noun.Regular("field"), Collect(typesToScan, type, t => t.Fields,
                Passes, include_compiler_generated)));
        if (kindSet.Contains(MemberKind.Event))
            groups.Add((Noun.Regular("event"), Collect(typesToScan, type, t => t.Events,
                Passes, include_compiler_generated)));
        if (kindSet.Contains(MemberKind.NestedType))
            groups.Add((new Noun("nested type", "nested types"), CollectNestedTypes(typesToScan, type,
                Passes, include_compiler_generated)));

        var subject = "of " + SignatureFormatter.FormatTypeDeclaration(type);
        var total = groups.Sum(group => group.Lines.Count);
        var sb = new StringBuilder(ListOutput.Header(total, new Noun("member", "members"), subject));

        // The limit counts entries across the groups; a group it cuts keeps its full count, and
        // a group after the cut is left out.
        var shown = 0;
        foreach (var (noun, lines) in groups.Where(group => group.Lines.Count > 0))
        {
            if (shown >= limit)
                break;
            sb.Append('\n').Append(noun.Count(lines.Count)).Append(':');
            foreach (var line in lines.Take(limit - shown))
                sb.Append("\n  ").Append(line);
            shown += Math.Min(lines.Count, limit - shown);
        }
        if (shown < total)
            sb.Append('\n').Append(ListOutput.Truncation(total - shown));
        if (unread != null)
            sb.Append('\n').Append(
                $"Not shown: what {unread.TypeName} and its base types declare; ILens could not load {unread.Assembly}");

        return sb.ToString();
    }

    /// <summary>
    /// The declaration lines of the members <paramref name="selector"/> picks, sorted by name
    /// with the constructors first, since they are declared under the type's name rather than
    /// their own.
    /// </summary>
    private static List<string> Collect(
        List<IType> typesToScan,
        ITypeDefinition declaredOn,
        Func<ITypeDefinition, IEnumerable<IMember>> selector,
        Func<IEntity, bool> passes,
        bool include_compiler_generated)
    {
        // Walk derived → base. Dedup by signature so an override hides the base declaration.
        // A base member prints as its type declares it but is keyed as declaredOn sees it: a
        // generic base declares in its type parameters what an override takes as the type
        // arguments the chain gives them.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<(IMember Member, string Line)>();

        foreach (var scanned in typesToScan)
        {
            var t = scanned.GetDefinition()!;
            var substitution = scanned.GetSubstitution();
            var origin = t.Equals(declaredOn) ? "" : $"  [from {ReferenceFormatter.FormatTypeName(t)}]";
            foreach (var member in selector(t))
            {
                if (!passes(member))
                    continue;
                if (!include_compiler_generated && CompilerGeneratedFilter.IsCompilerGenerated(member))
                    continue;
                if (!seen.Add(MemberKey(member.Specialize(substitution))))
                    continue;
                members.Add((member, ObsoleteMark(member) + SignatureFormatter.FormatMember(member) + origin));
            }
        }

        return members
            .OrderBy(entry => entry.Member is IMethod { IsConstructor: true } ? 0 : 1)
            .ThenBy(entry => entry.Member.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Line, StringComparer.Ordinal)
            .Select(entry => entry.Line)
            .ToList();
    }

    /// <summary>
    /// The nested types, one per line by the name the other tools take, sorted by that name.
    /// </summary>
    private static List<string> CollectNestedTypes(
        List<IType> typesToScan,
        ITypeDefinition declaredOn,
        Func<IEntity, bool> passes,
        bool include_compiler_generated)
    {
        var nestedTypes = new List<(string Name, string Line)>();
        foreach (var scanned in typesToScan)
        {
            var t = scanned.GetDefinition()!;
            var origin = t.Equals(declaredOn) ? "" : $"  [from {ReferenceFormatter.FormatTypeName(t)}]";
            foreach (var nested in t.NestedTypes)
            {
                if (!passes(nested))
                    continue;
                if (!include_compiler_generated && CompilerGeneratedFilter.IsCompilerGenerated(nested))
                    continue;
                var name = ReferenceFormatter.FormatTypeName(nested);
                nestedTypes.Add((name, ObsoleteMark(nested) + name + origin));
            }
        }
        return nestedTypes
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Line)
            .ToList();
    }

    private static string ObsoleteMark(IEntity entity) =>
        entity.GetAttributes().Any(attribute => attribute.AttributeType.ReflectionName == "System.ObsoleteAttribute")
            ? "[Obsolete] "
            : "";

    private static string MemberKey(IMember member) => member switch
    {
        // Distinguish overloads on the same kind by parameter types: a method's, and an
        // indexer's, since every indexer of a type shares one name.
        IParameterizedMember m => $"{member.SymbolKind}:{m.Name}({string.Join(",", m.Parameters.Select(p => p.Type.ReflectionName))})",
        _ => $"{member.SymbolKind}:{member.Name}",
    };
}
