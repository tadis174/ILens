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

        // A generated type named here is listed whatever the switch says: it was asked for by
        // name, and an empty answer would read as "it has no members".
        var includeGenerated = include_compiler_generated || CompilerGeneratedFilter.IsCompilerGenerated(type);

        // A constructor is listed under its type's name, so the filter finds it by that name as
        // well as by its own, .ctor or .cctor, which the other tools print and take.
        bool NameMatches(IEntity entity) =>
            string.IsNullOrEmpty(name_contains)
            || entity.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase)
            || (entity is IMethod { IsConstructor: true, DeclaringTypeDefinition: { } declaring }
                && declaring.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase));

        List<(Noun Noun, List<string> Lines)> Groups(AccessibilityFilter visible, bool withGenerated)
        {
            bool Passes(IEntity entity) => visible.Permits(entity.Accessibility) && NameMatches(entity);

            // The type's own constructors only: C# does not inherit them.
            var collected = new List<(Noun Noun, List<string> Lines)>();
            if (kindSet.Contains(MemberKind.Method))
                collected.Add((Noun.Regular("method"), Collect(typesToScan, type,
                    t => t.DeclaredMethods().Where(m => !m.IsAccessor && (!m.IsConstructor || t.Equals(type))),
                    Passes, withGenerated)));
            if (kindSet.Contains(MemberKind.Property))
                collected.Add((new Noun("property", "properties"), Collect(typesToScan, type, t => t.Properties,
                    Passes, withGenerated)));
            if (kindSet.Contains(MemberKind.Field))
                collected.Add((Noun.Regular("field"), Collect(typesToScan, type, t => t.Fields,
                    Passes, withGenerated)));
            if (kindSet.Contains(MemberKind.Event))
                collected.Add((Noun.Regular("event"), Collect(typesToScan, type, t => t.Events,
                    Passes, withGenerated)));
            if (kindSet.Contains(MemberKind.NestedType))
                collected.Add((new Noun("nested type", "nested types"), CollectNestedTypes(typesToScan, type,
                    Passes, withGenerated)));
            return collected;
        }
        int Count(AccessibilityFilter visible, bool withGenerated) =>
            Groups(visible, withGenerated).Sum(group => group.Lines.Count);

        var groups = Groups(accessibility, includeGenerated);
        var subject = "of " + SignatureFormatter.FormatTypeDeclaration(type);
        var total = groups.Sum(group => group.Lines.Count);

        // What the two filters left out is what the list gains under the other settings, so an
        // overridden member counts once there as it does here. An answer with members names
        // only what the accessibility filter left out, so the generated ones are not counted.
        var withEveryAccessibility = Count(AccessibilityFilter.All, includeGenerated);
        var generatedLeftOut = total > 0 ? 0 : Count(AccessibilityFilter.All, true) - withEveryAccessibility;
        var leftOut = new LeftOut(withEveryAccessibility - total, generatedLeftOut,
            generatedLeftOut > 0 && Count(accessibility, true) < generatedLeftOut);

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
        if (ListOutput.NotShown(leftOut, accessibility, total == 0) is { } notShown)
            sb.Append('\n').Append(notShown);
        if (unread != null)
            sb.Append('\n').Append(
                $"Not shown: what {unread.TypeName} and its base types declare; {unread.Reason}");

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
        bool includeGenerated)
    {
        // Walk derived → base. A base member is left out where a class below declares one that
        // hides it as C# does, so an override hides the declaration it overrides; two members of
        // one type are always two members, and none hides another. A base member prints as its
        // type declares it but is keyed as declaredOn sees it: a generic base declares in its type
        // parameters what an override takes as the type arguments the chain gives them.
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<(IMember Member, string Origin)>();

        foreach (var scanned in typesToScan)
        {
            var t = scanned.GetDefinition()!;
            var substitution = scanned.GetSubstitution();
            var origin = t.Equals(declaredOn) ? "" : $"  [from {ReferenceFormatter.FormatTypeName(t)}]";
            var declared = new List<string>();
            foreach (var member in selector(t))
            {
                if (!passes(member))
                    continue;
                if (!includeGenerated && CompilerGeneratedFilter.IsCompilerGenerated(member))
                    continue;
                var key = MemberKey(member.Specialize(substitution));
                if (hidden.Contains(key))
                    continue;
                declared.Add(key);
                members.Add((member, origin));
            }
            hidden.UnionWith(declared);
        }

        // A declaration that reads alike another's is written in full; the mark of the class a
        // member comes from is no part of the comparison, since the two still read as one member.
        var declaration = ReferenceFormatter.Distinguishing(members.Select(entry => entry.Member),
            SignatureFormatter.FormatMember, member => SignatureFormatter.FormatMember(member, full: true));
        return members
            .Select(entry => (entry.Member, Line: ObsoleteMark(entry.Member) + declaration(entry.Member) + entry.Origin))
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
        bool includeGenerated)
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
                if (!includeGenerated && CompilerGeneratedFilter.IsCompilerGenerated(nested))
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

    /// <summary>
    /// What a member hides a base class's member by, as C# hides one: its kind and name, and for
    /// a method or an indexer its parameter types, and a method's count of type parameters, since
    /// <c>Find(string)</c> leaves a base class's <c>Find&lt;T&gt;(string)</c> callable.
    /// </summary>
    private static string MemberKey(IMember member) => member switch
    {
        IParameterizedMember m => $"{member.SymbolKind}:{m.Name}`{(m as IMethod)?.TypeParameters.Count ?? 0}" +
            $"({string.Join(",", m.Parameters.Select(p => p.Type.ReflectionName))})",
        _ => $"{member.SymbolKind}:{member.Name}",
    };
}
