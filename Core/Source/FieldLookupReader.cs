using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Reads the fields some code looks up by reflection from the decompiler's syntax tree of it, where
/// each lookup is known by the method it calls: Harmony's lookups in AccessTools and their extension
/// spelling, Traverse's, the fields a transpiler names to Harmony's CodeInstruction, CodeMatch and
/// SymbolExtensions, and .NET's Type.GetField and GetFields. The class and the name each lookup is
/// given are read by <see cref="HarmonyValueReader"/>, followed as far as it follows a value, and
/// the field a lambda names where the call is given it. A class or a name it cannot read is '?'.
/// </summary>
internal static class FieldLookupReader
{
    /// <summary>
    /// The field lookups, by the full name of the type declaring each and its name, with the label
    /// their lines carry. The label says where the field is looked for: Field searches the base
    /// classes as well, DeclaredField the class alone. The extension spelling takes the label of the
    /// call it spells, and a transpiler's helper its own name.
    /// </summary>
    private static readonly Dictionary<(string Type, string Name), string> Labels = new()
    {
        [("HarmonyLib.AccessTools", "Field")] = "AccessToolsField",
        [("HarmonyLib.AccessToolsExtensions", "Field")] = "AccessToolsField",
        [("HarmonyLib.AccessTools", "DeclaredField")] = "DeclaredField",
        [("HarmonyLib.AccessToolsExtensions", "DeclaredField")] = "DeclaredField",
        [("HarmonyLib.AccessTools", "FieldRefAccess")] = "FieldRefAccess",
        [("HarmonyLib.AccessToolsExtensions", "FieldRefAccess")] = "FieldRefAccess",
        [("HarmonyLib.AccessTools", "StaticFieldRefAccess")] = "StaticFieldRefAccess",
        [("HarmonyLib.AccessToolsExtensions", "StaticFieldRefAccess")] = "StaticFieldRefAccess",
        [("HarmonyLib.AccessTools", "StructFieldRefAccess")] = "StructFieldRefAccess",
        [("HarmonyLib.AccessTools", "GetDeclaredFields")] = "GetDeclaredFields",
        [("HarmonyLib.AccessToolsExtensions", "GetDeclaredFields")] = "GetDeclaredFields",
        [("HarmonyLib.AccessTools", "GetFieldNames")] = "GetFieldNames",
        [("HarmonyLib.AccessToolsExtensions", "GetFieldNames")] = "GetFieldNames",
        [("HarmonyLib.Traverse", "Field")] = "TraverseField",
        [("HarmonyLib.CodeInstruction", "LoadField")] = "LoadField",
        [("HarmonyLib.CodeInstruction", "StoreField")] = "StoreField",
        [("HarmonyLib.CodeMatch", "LoadsField")] = "LoadsField",
        [("HarmonyLib.CodeMatch", "StoresField")] = "StoresField",
        [("HarmonyLib.SymbolExtensions", "GetFieldInfo")] = "GetFieldInfo",
        [("System.Type", "GetField")] = "GetField",
        [("System.Type", "GetFields")] = "GetFields",
    };

    // The lookups that enumerate a class's fields rather than name one.
    private static readonly HashSet<string> Enumerations = ["GetFields", "GetDeclaredFields", "GetFieldNames"];

    /// <summary>Whether a call to the method <paramref name="name"/> of <paramref name="type"/> looks up a field.</summary>
    public static bool IsLookup(string type, string name) => Labels.ContainsKey((type, name));

    /// <summary>The label of a lookup's lines.</summary>
    public static string LabelOf((string Type, string Name) lookup) => Labels[lookup];

    /// <summary>
    /// The fields the lookups in <paramref name="member"/>'s tree name, as class, field and label, and
    /// every lookup found, one per call. A lookup whose class or name comes from outside the member is
    /// read once for each call of the member, so it names one field for each class and name the calls
    /// give it. A lookup that takes a FieldInfo names no field: the FieldInfo comes from a lookup
    /// listed on its own. A lookup passed as a method group is called with arguments the code does
    /// not show.
    /// </summary>
    public static (List<(string Class, string Field, string Label)> Fields, List<(string Type, string Name)> Found) Read(
        HarmonyValueReader reader, IMethod member, SyntaxTree tree)
    {
        var fields = new List<(string Class, string Field, string Label)>();
        var found = new List<(string Type, string Name)>();
        foreach (var node in tree.Descendants)
        {
            switch (node)
            {
                case InvocationExpression invocation when invocation.GetSymbol() is IMethod method && KeyOf(method) is { } key:
                    found.Add(key);
                    if (!TakesFieldInfo(method))
                        fields.AddRange(ReadCall(reader, member, tree, invocation, method).Select(read => (read.Class, read.Field, Labels[key])));
                    break;
                case MemberReferenceExpression reference
                    when !(reference.Parent is InvocationExpression call && call.Target == reference)
                         && MethodGroupOf(reference) is var (groupKey, takesFieldInfo) && groupKey is { } methodGroup:
                    found.Add(methodGroup);
                    if (!takesFieldInfo)
                        fields.Add(("?", "?", Labels[methodGroup]));
                    break;
            }
        }
        return (fields, found);
    }

    /// <summary>
    /// The classes and fields one lookup names, read in each frame its member is read in. The class
    /// comes from the call's receiver, its Type argument, its first type argument, or its
    /// "Namespace.Type:field" text, which Harmony splits at the one colon; the field from its string
    /// argument. A class and a name that each have several values are not paired. An overload that
    /// takes an expression tree names the field its lambda reads.
    /// </summary>
    private static IEnumerable<(string Class, string Field)> ReadCall(
        HarmonyValueReader reader, IMethod member, SyntaxTree tree, InvocationExpression invocation, IMethod method)
    {
        var definition = method.MemberDefinition as IMethod ?? method;
        var parameters = definition.Parameters.Select(parameter => parameter.Type.ReflectionName).ToList();
        var arguments = invocation.Arguments.ToList();
        var lambdaAt = parameters.FindIndex(HarmonyValueReader.IsExpressionTree);
        if (lambdaAt >= 0)
            return [HarmonyValueReader.LambdaField(arguments.ElementAtOrDefault(lambdaAt)) ?? ("?", "?")];
        var target = (invocation.Target as MemberReferenceExpression)?.Target;
        var receiver = method.IsStatic || target is TypeReferenceExpression ? null : target;
        var typeAt = parameters.IndexOf("System.Type");
        var nameAt = parameters.LastIndexOf("System.String");
        var arity = Math.Max(definition.TypeParameters.Count, method.TypeArguments.Count);
        var classExpression = receiver ?? (typeAt >= 0 ? arguments.ElementAtOrDefault(typeAt) : null);
        var nameExpression = nameAt >= 0 ? arguments.ElementAtOrDefault(nameAt) : null;
        var enumerates = Enumerations.Contains(method.Name) || nameAt < 0;

        var pairs = new List<(string Class, string Field)>();
        foreach (var frame in reader.Frames(member, tree, new[] { classExpression, nameExpression }.Where(expression => expression is not null)))
        {
            List<string> classes;
            if (receiver is not null)
                classes = method.DeclaringType.ReflectionName == "HarmonyLib.Traverse"
                    ? reader.Read(receiver, frame).Select(value => value is HarmonyValueReader.TraverseValue traverse ? traverse.Class : "?").ToList()
                    : NamesOf(reader.Types(receiver, frame));
            else if (typeAt >= 0)
                classes = NamesOf(reader.Types(classExpression, frame));
            else if (arity == 2)
                classes = [HarmonyValueReader.TypeArgument(method, invocation)];
            else if (nameAt >= 0)
            {
                pairs.AddRange(reader.Texts(nameExpression, frame).Select(text =>
                    text is HarmonyValueReader.TextValue { Text: var joined } && joined.Split(':') is [var type, var field]
                        ? (HarmonyDependencyScanner.TextTypeName(type), field)
                        : ("?", "?")));
                continue;
            }
            else
                classes = [HarmonyValueReader.ObjectClass(arguments.ElementAtOrDefault(0))];

            List<string> names = enumerates
                ? ["?"]
                : reader.Texts(nameExpression, frame).Select(value => value is HarmonyValueReader.TextValue text ? text.Text : "?").ToList();
            classes = classes.Distinct().ToList();
            names = names.Distinct().ToList();
            if (classes.Count > 1 && names.Count > 1)
                pairs.Add(("?", "?"));
            else
                pairs.AddRange(classes.SelectMany(@class => names.Select(name => (@class, name))));
        }
        return pairs.Distinct();
    }

    private static List<string> NamesOf(IEnumerable<HarmonyValueReader.Value> types) =>
        types.Select(value => value is HarmonyValueReader.TypeValue type ? type.Name : "?").Distinct().ToList();

    private static bool TakesFieldInfo(IMethod method) =>
        (method.MemberDefinition as IMethod ?? method).Parameters
            .Any(parameter => parameter.Type.ReflectionName == "System.Reflection.FieldInfo");

    private static (string Type, string Name)? KeyOf(IMethod method) =>
        method.DeclaringType?.ReflectionName is { } type && Labels.ContainsKey((type, method.Name))
            ? (type, method.Name)
            : null;

    /// <summary>
    /// The lookup a method group names, and whether the overload it picks takes a FieldInfo. When the
    /// decompiler cannot load the lookup's assembly it leaves the member unresolved, and the delegate
    /// the group converts to, written as a cast or a constructor call, shows its parameters.
    /// </summary>
    private static ((string Type, string Name)? Key, bool TakesFieldInfo) MethodGroupOf(MemberReferenceExpression reference)
    {
        if (reference.GetSymbol() is IMethod method)
            return (KeyOf(method), TakesFieldInfo(method));
        var (targetType, name) = reference.GetResolveResult() switch
        {
            MethodGroupResolveResult group => (group.TargetType, group.MethodName),
            UnknownMemberResolveResult unknown => (unknown.TargetType, unknown.MemberName),
            _ => (null, null),
        };
        if (targetType?.ReflectionName is not { } type || !Labels.ContainsKey((type, name)))
            return (null, false);
        var @delegate = reference.Parent is CastExpression or ObjectCreateExpression
            ? reference.Parent.GetResolveResult().Type.GetDelegateInvokeMethod()
            : null;
        return ((type, name),
            @delegate?.Parameters.Any(parameter => parameter.Type.ReflectionName == "System.Reflection.FieldInfo") == true);
    }
}
