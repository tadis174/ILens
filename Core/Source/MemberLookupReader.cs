using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Reads the methods, properties, events and constructors some code looks up by reflection, or
/// names to a transpiler's helpers, from the decompiler's syntax tree of it. Each lookup is known
/// by the method it calls, a property's getter for a lookup that reads a property, and a
/// constructor for one that creates an object, as CodeMatch's does; <see cref="HarmonyValueReader"/>
/// holds the forms and reads what a lookup names, the same way as where its result is a patch
/// target. A class or a name it cannot read is null, which a line prints as '?'.
/// </summary>
internal static class MemberLookupReader
{
    /// <summary>
    /// The members the lookups in <paramref name="member"/>'s tree name, each with the first word and
    /// the label of its line, and every lookup found, one per call, object creation or property read.
    /// Two kinds of lookup give no line: one that names a member of a class the scanned assembly
    /// defines, which no other assembly's update can break, and one whose result is among
    /// <paramref name="shown"/>, the lookups the list already shows as patch targets. A lookup passed
    /// as a method group is called with arguments the code does not show, so it names nothing.
    /// </summary>
    public static (List<(string Word, string Class, string Member, IReadOnlyList<string> ParameterTypes, string Label)> Lookups,
        List<(string Type, string Name)> Found) Read(
        HarmonyValueReader reader, IMethod member, SyntaxTree tree, IReadOnlySet<HarmonyValueReader.Origin> shown)
    {
        var lookups = new List<(string Word, string Class, string Member, IReadOnlyList<string> ParameterTypes, string Label)>();
        var found = new List<(string Type, string Name)>();
        foreach (var node in tree.Descendants)
        {
            switch (node)
            {
                case InvocationExpression or ObjectCreateExpression or MemberReferenceExpression
                    when HarmonyValueReader.LookupMethod((Expression)node) is { } method && KeyOf(method) is { } key:
                    found.Add(key);
                    lookups.AddRange(ReadCall(reader, member, tree, (Expression)node, shown)
                        .Select(read => (read.Word, read.Class, read.Member, read.ParameterTypes, HarmonyValueReader.LabelOf(key))));
                    break;
                case MemberReferenceExpression reference
                    when !(reference.Parent is InvocationExpression call && call.Target == reference)
                         && MethodGroupOf(reference) is { } methodGroup:
                    found.Add(methodGroup);
                    lookups.Add((HarmonyValueReader.WordOf(methodGroup), null, null, null, HarmonyValueReader.LabelOf(methodGroup)));
                    break;
            }
        }
        return (lookups, found);
    }

    /// <summary>
    /// The members one lookup names, read in each frame its member is read in, the same one once.
    /// A property or event lookup whose accessor the code takes right at the call names that
    /// accessor, and a method lookup whose result the code makes generic right at the call names
    /// the type arguments it is made for.
    /// </summary>
    private static List<(string Word, string Class, string Member, IReadOnlyList<string> ParameterTypes)> ReadCall(
        HarmonyValueReader reader, IMethod member, SyntaxTree tree, Expression call,
        IReadOnlySet<HarmonyValueReader.Origin> shown)
    {
        var accessor = AccessorTaken(call);
        var madeGeneric = MadeGenericAt(call);
        var invocation = call as InvocationExpression;
        IEnumerable<Expression> given = HarmonyValueReader.ReadsInFrames(HarmonyValueReader.LookupMethod(call))
            ? HarmonyValueReader.ArgumentsOf(call)
                .Append(((invocation?.Target ?? call) as MemberReferenceExpression)?.Target)
                .Concat(madeGeneric?.Arguments ?? Enumerable.Empty<Expression>())
                .Where(expression => expression is not null)
            : [];
        var lines = new List<(string Word, string Class, string Member, IReadOnlyList<string> ParameterTypes)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in reader.Frames(member, tree, given))
        {
            foreach (var lookup in reader.Lookups(call, frame))
            {
                if (lookup.Type is not null && reader.Defines(lookup.Type))
                    continue;
                if (lookup.Value is not HarmonyValueReader.UnknownValue
                    && shown.Contains(new HarmonyValueReader.Origin(call, lookup.Value)))
                    continue;

                var takesAccessor = accessor is not null && lookup.Member is not null;
                IReadOnlyList<string> parameterTypes = takesAccessor && accessor == "set_" && lookup.ParameterTypes is not null
                    ? [.. lookup.ParameterTypes, "?"]
                    : lookup.ParameterTypes;
                IReadOnlyList<IReadOnlyList<string>> made = madeGeneric is not null && lookup.Word == "Method" && lookup.Member is not null
                    ? reader.TypeArgumentLists(madeGeneric.Arguments.ToList(), frame)
                    : [null];
                foreach (var typeArguments in made)
                {
                    var line = (Word: takesAccessor ? "Method" : lookup.Word, Class: lookup.Type?.Name,
                        Member: HarmonyValueReader.WithTypeArguments(takesAccessor ? accessor + lookup.Member : lookup.Member, typeArguments),
                        ParameterTypes: parameterTypes);
                    var key = string.Join("\n", line.Word, line.Class, line.Member,
                        line.ParameterTypes is null ? "-" : string.Join(",", line.ParameterTypes));
                    if (seen.Add(key))
                        lines.Add(line);
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// The MakeGenericMethod call that makes the method a lookup finds generic right at the call,
    /// behind ?. as well, and null where the code makes none there. A method made from a local or a
    /// field that holds it is not followed, as for an accessor taken.
    /// </summary>
    private static InvocationExpression MadeGenericAt(Expression lookup)
    {
        AstNode holder = lookup.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional } conditional
            ? conditional
            : lookup;
        return holder.Parent is MemberReferenceExpression { MemberName: "MakeGenericMethod" } reference
               && reference.Target == holder
               && reference.Parent is InvocationExpression call && call.Target == reference
            ? call
            : null;
    }

    /// <summary>
    /// The prefix of the accessor the code takes from a property or event lookup right at the call,
    /// behind ?. as well, with a member <see cref="HarmonyValueReader.AccessorPrefix"/> knows, such
    /// as GetGetMethod() or AddMethod. Null when it takes none there. An accessor taken from a local
    /// or a field that holds the property or the event is not followed, since the reader follows a
    /// value back to where it is written and not forward to where it is used; the line then names
    /// the property or the event, which is what the lookup names. So does an event's raise method,
    /// which a C# event does not have.
    /// </summary>
    private static string AccessorTaken(Expression lookup)
    {
        AstNode holder = lookup.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional } conditional
            ? conditional
            : lookup;
        if (holder.Parent is not MemberReferenceExpression reference || reference.Target != holder)
            return null;
        return reference.Parent is InvocationExpression call && call.Target == reference
            ? HarmonyValueReader.AccessorPrefix(call.GetSymbol() as IMember, called: true)
            : HarmonyValueReader.AccessorPrefix(reference.GetSymbol() as IMember, called: false);
    }

    private static (string Type, string Name)? KeyOf(IMethod method) =>
        method.DeclaringType?.ReflectionName is { } type && HarmonyValueReader.IsMemberLookup(type, method.Name)
            ? (type, method.Name)
            : null;

    /// <summary>
    /// The lookup a method group names. When the decompiler cannot load the lookup's assembly it
    /// leaves the member unresolved, and the reference still names the type and the method.
    /// </summary>
    private static (string Type, string Name)? MethodGroupOf(MemberReferenceExpression reference)
    {
        if (reference.GetSymbol() is IMethod method)
            return KeyOf(method);
        var (targetType, name) = reference.GetResolveResult() switch
        {
            MethodGroupResolveResult group => (group.TargetType, group.MethodName),
            UnknownMemberResolveResult unknown => (unknown.TargetType, unknown.MemberName),
            _ => (null, null),
        };
        return targetType?.ReflectionName is { } type && HarmonyValueReader.IsMemberLookup(type, name) ? (type, name) : null;
    }
}
