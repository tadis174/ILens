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

        // The members of a C# 14 extension block are listed under their block, as the decompiled
        // class declares them, and the methods the compiler made of them not among the methods.
        // Only a static class declares blocks, and it inherits none.
        var extensions = type.ExtensionInfo;

        var nameFilter = NameFilter.Of(name_contains);

        // A constructor is listed under its type's name, so the filter finds it by that name as
        // well as by its own, .ctor or .cctor, which the other tools print and take.
        bool NameMatches(IEntity entity) =>
            nameFilter.Matches(entity)
            || (entity is IMethod { IsConstructor: true, DeclaringTypeDefinition: { } declaring }
                && nameFilter.Matches(declaring));

        // Each group is titled by its full count, which a group the limit cuts keeps.
        List<(string Title, List<string> Lines)> Groups(AccessibilityFilter visible, bool withGenerated)
        {
            bool Passes(IEntity entity) => visible.Permits(entity.Accessibility) && NameMatches(entity);

            var collected = new List<(string Title, List<string> Lines)>();
            void Add(Noun noun, List<string> lines) => collected.Add((noun.Count(lines.Count), lines));

            // The type's own constructors only: C# does not inherit them.
            if (kindSet.Contains(MemberKind.Method))
                Add(Noun.Regular("method"), Collect(typesToScan, type,
                    t => t.DeclaredMethods().Where(m => !m.IsAccessor && (!m.IsConstructor || t.Equals(type))
                        && extensions?.InfoOfImplementationMember(m) is null),
                    Passes, withGenerated));
            if (kindSet.Contains(MemberKind.Property))
                Add(new Noun("property", "properties"), Collect(typesToScan, type, t => t.Properties,
                    Passes, withGenerated));
            if (kindSet.Contains(MemberKind.Field))
                Add(Noun.Regular("field"), Collect(typesToScan, type, t => t.Fields,
                    Passes, withGenerated));
            if (kindSet.Contains(MemberKind.Event))
                Add(Noun.Regular("event"), Collect(typesToScan, type, t => t.Events,
                    Passes, withGenerated));
            if (extensions != null)
                collected.AddRange(CollectBlocks(extensions, kindSet, visible, NameMatches, withGenerated));
            if (kindSet.Contains(MemberKind.NestedType))
                Add(new Noun("nested type", "nested types"), CollectNestedTypes(typesToScan, type,
                    Passes, withGenerated));
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
        foreach (var (title, lines) in groups.Where(group => group.Lines.Count > 0))
        {
            if (shown >= limit)
                break;
            sb.Append('\n').Append(title).Append(':');
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

    /// <summary>
    /// One group for each line a C# 14 extension block opens with, titled by its count and that
    /// line, <c>3 members of extension(Host host)</c>, sorted by the line. Blocks whose lines read
    /// alike, such as two that differ only in an attribute on the receiver, which no line writes,
    /// are one group, as a list merges lines alike. A member is written as the decompiled class
    /// declares it, and after it, where its name is not theirs, the names of the methods the
    /// compiler made of it, which the other tools take:
    /// <c>public int Doubled { get; }  [get_Doubled]</c>. The filters take a member by its own
    /// kind, accessibility and name, the name filter by its methods' names as well, and count it
    /// as generated where its methods are, since the class the compiler declares it in is.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: ExtensionInfo gives each
    /// block by its marker method, whose one parameter is the receiver, gives a block's members
    /// as the compiler's grouping class declares them, a property once for its accessors, and
    /// maps each to the method the compiler made of it (ExtensionGroups, GetMembersOfGroup,
    /// InfoOfExtensionMember). The cases: ListMembers_ListsABlocksMembersUnderTheBlock and
    /// ListMembers_ListsBlocksWhoseLinesReadAlikeAsOne in ExtensionBlockTests.
    /// </remarks>
    private static IEnumerable<(string Title, List<string> Lines)> CollectBlocks(
        ExtensionInfo extensions,
        HashSet<MemberKind> kinds,
        AccessibilityFilter visible,
        Func<IEntity, bool> nameMatches,
        bool includeGenerated)
    {
        var blocks = new SortedDictionary<string, List<(IMember Member, List<IMethod> Methods)>>(StringComparer.Ordinal);
        foreach (var (marker, typeParameters) in extensions.ExtensionGroups)
        {
            var block = SignatureFormatter.FormatExtensionBlock(marker, typeParameters);
            if (!blocks.TryGetValue(block, out var members))
                blocks[block] = members = [];
            foreach (var member in extensions.GetMembersOfGroup(marker))
            {
                if (!kinds.Contains(member is IProperty ? MemberKind.Property : MemberKind.Method)
                    || !visible.Permits(member.Accessibility))
                    continue;
                IMethod[] declared = member is IProperty property ? [property.Getter, property.Setter] : [(IMethod)member];
                var methods = declared
                    .Select(method => TypeWalker.ImplementationOf(extensions, method))
                    .Where(method => method != null)
                    .ToList();
                if (!nameMatches(member) && !methods.Any(nameMatches))
                    continue;
                if (!includeGenerated && methods.Count > 0 && methods.All(CompilerGeneratedFilter.IsCompilerGenerated))
                    continue;
                members.Add((member, methods));
            }
        }

        static string MethodsMark(IMember member, List<IMethod> methods)
        {
            var named = methods.Where(method => method.Name != member.Name).ToList();
            return named.Count == 0
                ? ""
                : $"  [{string.Join(", ", named.Select(method => ReferenceFormatter.FormatIdentifier(method.Name)))}]";
        }
        var noun = new Noun("member", "members");
        foreach (var (block, members) in blocks)
        {
            var declaration = ReferenceFormatter.Distinguishing(members.Select(entry => entry.Member),
                SignatureFormatter.FormatMember, member => SignatureFormatter.FormatMember(member, full: true));
            var lines = members
                .Select(entry => (entry.Member, Line: ObsoleteMark(entry.Member) + declaration(entry.Member)
                    + MethodsMark(entry.Member, entry.Methods)))
                .OrderBy(entry => entry.Member.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.Line, StringComparer.Ordinal)
                .Select(entry => entry.Line)
                .ToList();
            yield return ($"{noun.Count(lines.Count)} of {block}", lines);
        }
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
