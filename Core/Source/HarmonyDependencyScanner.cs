using System.Collections.Immutable;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Walks a Harmony-patching assembly and extracts the full reflective surface a
/// caller needs to ask "do my patches still bind on game version X?":
/// every patch target, from <c>[HarmonyPatch]</c> attributes, from
/// <c>TargetMethod</c>/<c>TargetMethods</c> bodies and from calls to Harmony.Patch,
/// every field the assembly looks up by reflection in any class, and every field
/// Harmony hands a patch method through a <c>___name</c> parameter. The scan
/// returns records, and <c>find_harmony_dependencies</c> writes each as one line
/// of text.
///
/// Patch methods and target resolvers are found as Harmony finds them, by their
/// name or by their marker attribute, and each patch method binds to its own
/// target: the class's <c>[HarmonyPatch]</c> with the method's own laid over it.
///
/// Attribute-based bindings are read directly from metadata. TargetMethod and
/// TargetMethods bodies, Harmony.Patch calls and field lookups are found in the IL
/// and read from the decompiler's syntax trees by <see cref="HarmonyValueReader"/>,
/// which follows a value back to where the assembly writes it.
/// </summary>
public sealed class HarmonyDependencyScanner
{
    public const string HarmonyLibNamespace = "HarmonyLib";

    // The patch types, in the order Harmony tries them: a method is of the first type it is
    // named after or marked with, as [HarmonyPrefix] marks a prefix. Harmony 2.4 also tries
    // InnerPrefix and InnerPostfix, which patch a call inside the target; 2.4.1 has neither an
    // attribute nor a HarmonyMethod field that names that call, so a line could not say what
    // one patches.
    private static readonly string[] PatchTypes =
    {
        "Prefix", "Postfix", "Transpiler", "Finalizer", "ReversePatch"
    };

    // The patch types of Harmony.Patch's arguments after the original, in their order.
    private static readonly string[] PatchCallTypes = { "Prefix", "Postfix", "Transpiler", "Finalizer" };

    // HarmonyLib.MethodType by ordinal, which is what an attribute stores. Harmony 2.3.6 has the
    // first seven values and 2.4.1 all of them; a value a later version adds prints as Unknown(n).
    private static readonly string[] MethodTypeNames =
    {
        "Normal", "Getter", "Setter", "Constructor", "StaticConstructor", "Enumerator", "Async",
        "Finalizer", "EventAdd", "EventRemove", "OperatorImplicit", "OperatorExplicit",
        "OperatorUnaryPlus", "OperatorUnaryNegation", "OperatorLogicalNot", "OperatorOnesComplement",
        "OperatorIncrement", "OperatorDecrement", "OperatorTrue", "OperatorFalse", "OperatorAddition",
        "OperatorSubtraction", "OperatorMultiply", "OperatorDivision", "OperatorModulus",
        "OperatorBitwiseAnd", "OperatorBitwiseOr", "OperatorExclusiveOr", "OperatorLeftShift",
        "OperatorRightShift", "OperatorEquality", "OperatorInequality", "OperatorGreaterThan",
        "OperatorLessThan", "OperatorGreaterThanOrEqual", "OperatorLessThanOrEqual", "OperatorComma"
    };

    private static readonly ResolvedTarget Unread = new("?", null, null);

    private readonly AssemblyHost _host;
    private readonly HarmonyValueReader _reader;

    public HarmonyDependencyScanner(AssemblyHost host)
    {
        _host = host;
        _reader = new HarmonyValueReader(host);
    }

    /// <summary>
    /// A patch target, with <c>MethodType</c> the kind of method patched when <c>TargetMember</c>
    /// does not show it, such as Enumerator, and otherwise null.
    /// </summary>
    public sealed record Patch(
        string TargetType,
        string TargetMember,
        IReadOnlyList<string> ParamTypes,
        string MethodType,
        string PatchType,
        string ResolutionKind,
        string PatchSite);

    public sealed record FieldAccess(
        string ContextType,
        string FieldName,
        string Accessor,
        string PatchSite);

    public sealed record ScanResult(
        IReadOnlyList<Patch> Patches,
        IReadOnlyList<FieldAccess> FieldAccesses);

    public ScanResult Scan()
    {
        var patches = new List<Patch>();
        var fieldAccesses = new List<FieldAccess>();

        foreach (var type in _host.TypeSystem.MainModule.TypeDefinitions)
            ProcessPatchClass(type, patches, fieldAccesses);
        ScanPatchCalls(patches, fieldAccesses);
        ScanFieldLookups(fieldAccesses);

        return new ScanResult(patches, fieldAccesses);
    }

    private void ProcessPatchClass(
        ITypeDefinition type, List<Patch> patches, List<FieldAccess> fieldAccesses)
    {
        // Every type is looked at, so each method's Harmony attributes are read once.
        var methods = type.Methods
            .Select(method => (Method: method, HarmonyAttributes: HarmonyAttributesOf(method)))
            .ToList();
        var targetMethodMethod = FindTargetResolver(methods, "TargetMethod");
        var targetMethodsMethod = FindTargetResolver(methods, "TargetMethods");
        bool hasBodyResolver = targetMethodMethod != null || targetMethodsMethod != null;
        var patchMethods = methods
            .Select(method => (PatchType: PatchTypeOf(method.Method, method.HarmonyAttributes), method.Method,
                NamesTarget: method.HarmonyAttributes.Contains("HarmonyPatch")))
            .Where(patchMethod => patchMethod.PatchType != null)
            .ToList();

        // A class counts through its patch methods' own [HarmonyPatch] too, since that may be
        // all that names its targets: on a class carrying only a category, or one applied by
        // hand without any class attribute.
        var classTarget = ReadTargetSpec(type);
        bool isPatchClass = classTarget != null
            || hasBodyResolver
            || patchMethods.Any(patchMethod => patchMethod.NamesTarget);
        if (!isPatchClass) return;

        if (patchMethods.Count == 0)
            patchMethods.Add(("(none)", null, false));
        var firstPatch = patches.Count;

        // An empty [HarmonyPatch] paired with TargetMethod/TargetMethods is the
        // canonical "I'll compute the target myself" marker — suppress its
        // (targetType=?) attribute entry so it doesn't drown the actual
        // resolved targets the body produces.
        foreach (var (patchType, patchSite, namesTarget) in patchMethods)
        {
            var target = Merge(classTarget, namesTarget ? ReadTargetSpec(patchSite) : null);
            if (target == null || (target.TargetType == null && hasBodyResolver))
                continue;
            patches.Add(BuildPatchFromAttribute(target, patchType, patchSite, type));
        }

        if (targetMethodMethod != null)
        {
            var resolved = ResolveTargetMethodBody(targetMethodMethod);
            // A TargetMethod that fans out to multiple lookups, or makes one the scanner
            // cannot read, can't have a single binding at load time — surface that as
            // DynamicTargetMethod so callers know to inspect manually.
            var kind = resolved.Count > 1 || resolved[0].TargetType == "?" ? "DynamicTargetMethod"
                : resolved[0].Searched ? "TargetMethodSearch"
                : "TargetMethod";
            foreach (var rt in resolved)
                foreach (var (patchType, patchSite, _) in patchMethods)
                    patches.Add(BuildPatchFromBody(rt, kind, patchType, patchSite, type));
        }

        if (targetMethodsMethod != null)
        {
            foreach (var rt in ResolveTargetMethodBody(targetMethodsMethod))
                foreach (var (patchType, patchSite, _) in patchMethods)
                    patches.Add(BuildPatchFromBody(
                        rt, rt.Searched ? "TargetMethodsSearch" : "TargetMethods", patchType, patchSite, type));
        }

        AddInjectedFields(type, patchMethods.Select(patchMethod => (patchMethod.PatchType, patchMethod.Method)),
            patches.Skip(firstPatch).ToList(), fieldAccesses);
    }

    /// <summary>
    /// The fields Harmony hands a prefix, postfix or finalizer through a parameter named ___ and the
    /// field's name. Harmony looks the field up with AccessTools.Field in the class declaring the
    /// patched method, or by its index with AccessTools.DeclaredField when ___ is followed by digits,
    /// and fails to patch when it is missing. A field is listed on each class the method's targets
    /// name, which is the class declaring the patched method unless that method is inherited, and on
    /// '?' when the scan names none.
    /// </summary>
    private static void AddInjectedFields(ITypeDefinition patchClass,
        IEnumerable<(string PatchType, IMethod Method)> patchMethods, List<Patch> classPatches, List<FieldAccess> output)
    {
        foreach (var (patchType, method) in patchMethods)
        {
            if (method is null || patchType is not ("Prefix" or "Postfix" or "Finalizer"))
                continue;
            var site = $"{ReferenceFormatter.FormatTypeName(patchClass)}.{method.Name}";
            AddInjectedFields(method, site, classPatches.Where(patch => patch.PatchSite == site), output);
        }
    }

    /// <summary>The ___name parameters of <paramref name="method"/>, as fields of each class <paramref name="patches"/> target.</summary>
    private static void AddInjectedFields(IMethod method, string site, IEnumerable<Patch> patches, List<FieldAccess> output)
    {
        var classes = patches.Select(patch => patch.TargetType).Distinct().ToList();
        if (classes.Count == 0)
            classes.Add("?");
        foreach (var parameter in method.Parameters.Where(parameter => parameter.Name.StartsWith("___", StringComparison.Ordinal)))
        {
            var name = parameter.Name["___".Length..];
            var field = name.Length == 0 || name.All(char.IsAsciiDigit) ? "?" : name;
            output.AddRange(classes.Select(@class => new FieldAccess(@class, field, "Injected", site)));
        }
    }

    /// <summary>
    /// The patches the assembly applies by calling Harmony.Patch. Each call gives one line for each
    /// patch method it passes, as prefix, postfix, transpiler or finalizer by the argument's position,
    /// and each target it names, read in each frame its member is read in. The label names the member
    /// that writes the values out: the calling member, or the member whose call or construction gives
    /// it its values. A call the IL shows and the tree does not is listed with '?' throughout, so a
    /// patch is never left out unseen. The ___name parameters of the prefixes, postfixes and
    /// finalizers these calls pass are listed as attribute patches' are. PatchProcessor is not read:
    /// its one use in the workshop corpus re-applies the patches a method already has and names no
    /// patch method.
    /// </summary>
    private void ScanPatchCalls(List<Patch> patches, List<FieldAccess> fieldAccesses)
    {
        var callPatches = new List<Patch>();
        var patchMethods = new Dictionary<string, HarmonyValueReader.PatchMethodValue>(StringComparer.Ordinal);
        foreach (var (member, calls) in _host.MembersCalling(IsPatchCall))
        {
            var tree = _reader.Tree(member);
            var invocations = tree?.Descendants.OfType<InvocationExpression>()
                .Where(invocation => invocation.GetSymbol() is IMethod method
                    && IsPatchCall(method.DeclaringType?.ReflectionName, method.Name))
                .ToList() ?? [];
            for (var unseen = invocations.Count; unseen < calls.Count; unseen++)
                callPatches.Add(new Patch("?", null, null, null, "?", $"PatchCall in {SiteOf(member)}", "?"));
            foreach (var invocation in invocations)
                callPatches.AddRange(ReadPatchCall(member, tree, invocation, patchMethods));
        }
        patches.AddRange(callPatches);

        var listed = fieldAccesses.ToHashSet();
        foreach (var group in callPatches.Where(patch => patch.PatchType is "Prefix" or "Postfix" or "Finalizer")
                     .GroupBy(patch => patch.PatchSite))
        {
            if (!patchMethods.TryGetValue(group.Key, out var patchMethod)
                || patchMethod.Class.Type?.GetDefinition() is not { } patchClass)
                continue;
            var injected = new List<FieldAccess>();
            foreach (var method in patchClass.Methods.Where(method => method.Name == patchMethod.Name))
                AddInjectedFields(method, group.Key, group, injected);
            fieldAccesses.AddRange(injected.Distinct().Where(listed.Add));
        }
    }

    private static bool IsPatchCall(string type, string name) =>
        type == "HarmonyLib.Harmony" && name == "Patch";

    /// <summary>
    /// The lines one Harmony.Patch call gives: in each frame, every patch method of every position
    /// with every target, the same line once.
    /// </summary>
    private IEnumerable<Patch> ReadPatchCall(IMethod member, SyntaxTree tree, InvocationExpression invocation,
        Dictionary<string, HarmonyValueReader.PatchMethodValue> patchMethods)
    {
        var arguments = invocation.Arguments.ToList();
        var lines = new List<Patch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in _reader.Frames(member, tree, arguments))
        {
            var origin = SiteOf(frame.Origin);
            var targets = HarmonyValueReader.Flatten(_reader.Read(arguments.ElementAtOrDefault(0), frame))
                .Where(value => value is not HarmonyValueReader.NullValue)
                .ToList();
            for (var position = 1; position <= PatchCallTypes.Length; position++)
            {
                var methods = HarmonyValueReader.Flatten(_reader.Read(arguments.ElementAtOrDefault(position), frame))
                    .Select(HarmonyValueReader.PatchMethodOf)
                    .Where(value => value is not HarmonyValueReader.NullValue)
                    .Distinct();
                foreach (var method in methods)
                {
                    var site = method is HarmonyValueReader.PatchMethodValue named ? $"{named.Class.Name}.{named.Name}" : "?";
                    if (method is HarmonyValueReader.PatchMethodValue known)
                        patchMethods.TryAdd(site, known);
                    foreach (var target in targets)
                    {
                        var line = target is HarmonyValueReader.MethodValue found
                            ? new Patch(found.Type.Name, found.Name, found.ParameterTypes, found.MethodType, PatchCallTypes[position - 1],
                                $"{(found.Searched ? "PatchCallSearch" : "PatchCall")} in {origin}", site)
                            : new Patch("?", null, null, null, PatchCallTypes[position - 1], $"PatchCall in {origin}", site);
                        if (seen.Add(KeyOf(line)))
                            lines.Add(line);
                    }
                }
            }
        }
        return lines;
    }

    private static string KeyOf(Patch patch) =>
        string.Join("\n", patch.TargetType, patch.TargetMember, patch.ParamTypes is null ? "-" : string.Join(",", patch.ParamTypes),
            patch.MethodType, patch.PatchType, patch.ResolutionKind, patch.PatchSite);

    /// <summary>A member as the lines name the member a dependency comes from: its class and its name.</summary>
    private static string SiteOf(IMethod member) =>
        $"{ReferenceFormatter.FormatTypeName(member.DeclaringTypeDefinition)}.{member.Name}";

    /// <summary>
    /// Every field the assembly looks up by reflection, in any class. The members whose IL, or the IL
    /// of code the compiler generated for them, calls a field lookup are decompiled, and each lookup is
    /// read from the syntax tree. A call the IL shows that the tree does not, as when a member cannot be
    /// decompiled, is listed with '?' for its class and field, so a field is never left out unseen.
    /// </summary>
    private void ScanFieldLookups(List<FieldAccess> output)
    {
        foreach (var (member, calls) in _host.MembersCalling(FieldLookupReader.IsLookup))
        {
            var site = SiteOf(member);
            var (fields, found) = _reader.Tree(member) is { } tree
                ? FieldLookupReader.Read(_reader, member, tree)
                : ([], []);
            output.AddRange(fields.Select(field => new FieldAccess(field.Class, field.Field, field.Label, site)));

            var unseen = calls.GroupBy(call => call)
                .SelectMany(group => Enumerable.Repeat(group.Key, Math.Max(0, group.Count() - found.Count(call => call == group.Key))));
            output.AddRange(unseen.Select(call => new FieldAccess("?", "?", FieldLookupReader.LabelOf(call), site)));
        }
    }

    /// <summary>
    /// The target named by the <c>[HarmonyPatch]</c> attributes on a patch class or a patch
    /// method, or null when it carries none. Harmony lets one target be split across several
    /// attributes (e.g. <c>[HarmonyPatch(typeof(X))] [HarmonyPatch("Y")]</c>), so the
    /// arguments are combined by their type rather than by attribute.
    /// </summary>
    private TargetSpec ReadTargetSpec(IEntity owner)
    {
        TargetSpec spec = null;
        bool anyDecodeError = false;

        foreach (var attr in owner.GetAttributes())
        {
            if (!IsHarmonyAttribute(attr.AttributeType, "HarmonyPatch")) continue;

            spec ??= new TargetSpec();
            // The high-level decoder empties FixedArguments when an argument's
            // enum type is unresolvable (the MethodType.Constructor case). Note it
            // and recover those attributes from raw metadata below.
            if (attr.HasDecodeErrors)
            {
                anyDecodeError = true;
                continue;
            }
            foreach (var arg in attr.FixedArguments)
                AbsorbArg(spec, arg);
        }

        if (anyDecodeError)
            AbsorbLenientHarmonyAttributes(spec, owner);
        return spec;
    }

    /// <summary>
    /// The target a patch method binds to, merged as Harmony merges it: each part the
    /// method's own <c>[HarmonyPatch]</c> names replaces the class's. Null when neither
    /// carries one.
    /// </summary>
    private static TargetSpec Merge(TargetSpec classTarget, TargetSpec methodTarget) =>
        classTarget == null ? methodTarget
        : methodTarget == null ? classTarget
        : new TargetSpec
        {
            TargetType = methodTarget.TargetType ?? classTarget.TargetType,
            TypeNamedByString = methodTarget.TargetType != null
                ? methodTarget.TypeNamedByString
                : classTarget.TypeNamedByString,
            TargetMember = methodTarget.TargetMember ?? classTarget.TargetMember,
            ParamTypes = methodTarget.ParamTypes ?? classTarget.ParamTypes,
            MethodType = methodTarget.MethodType ?? classTarget.MethodType,
        };

    /// <summary>
    /// The target with its patched method named as the other tools take it, and without its
    /// method type once the name shows it. A getter, setter or event accessor patch names the
    /// property or event and binds its accessor. A constructor patch without argument types binds
    /// the parameterless constructor, as AccessTools.DeclaredConstructor does with none.
    /// </summary>
    private TargetSpec NamePatchedMethod(TargetSpec spec) =>
        MethodNamedByKind(spec.MethodType) is { } method
            ? Named(spec, method) with { ParamTypes = spec.MethodType == "Constructor" ? spec.ParamTypes ?? [] : spec.ParamTypes }
        : IsIndexerPatch(spec) ? NameIndexerAccessor(spec)
        : spec.MethodType switch
        {
            "Normal" => spec with { MethodType = null },
            "Getter" => Named(spec, "get_" + spec.TargetMember),
            "Setter" => Named(spec, "set_" + spec.TargetMember),
            "EventAdd" when spec.TargetMember != null => Named(spec, "add_" + spec.TargetMember),
            "EventRemove" when spec.TargetMember != null => Named(spec, "remove_" + spec.TargetMember),
            _ => spec,
        };

    /// <summary>
    /// Whether a getter or setter patch names no property, which Harmony binds to an indexer's
    /// accessor by the index types alone: the indexer with those index types, or the only one when
    /// the patch names none.
    /// </summary>
    private static bool IsIndexerPatch(TargetSpec spec) =>
        spec.MethodType is "Getter" or "Setter" && spec.TargetMember == null;

    /// <summary>
    /// An indexer patch's target, named as the other tools take the accessor: a setter takes the
    /// index types and then the value. The matching indexer of a class the scanned assembly defines
    /// gives its own accessor name and value type; otherwise the name is C#'s default, Item, which
    /// <c>[IndexerName]</c> can change, and a setter's value type is unknown, written ?.
    /// </summary>
    private TargetSpec NameIndexerAccessor(TargetSpec spec)
    {
        var (types, _) = _host.Resolver.FindTypes(spec.TargetType);
        var indexers = types.Count == 1
            ? types[0].Properties.Where(property => property.IsIndexer).ToList()
            : new List<IProperty>();
        var indexer = spec.ParamTypes == null
            ? indexers.Count == 1 ? indexers[0] : null
            : indexers.FirstOrDefault(candidate => SymbolResolver.MatchesParameterTypes(candidate, spec.ParamTypes));

        var setter = spec.MethodType == "Setter";
        var accessor = setter ? indexer?.Setter : indexer?.Getter;
        var name = accessor?.Name ?? (setter ? "set_Item" : "get_Item");
        var parameters = setter && spec.ParamTypes != null
            ? spec.ParamTypes.Append(indexer == null ? "?" : ReferenceFormatter.FormatTypeRef(indexer.ReturnType)).ToList()
            : spec.ParamTypes;
        return Named(spec, name) with { ParamTypes = parameters };
    }

    private static TargetSpec Named(TargetSpec spec, string method) =>
        spec with { TargetMember = method, MethodType = null };

    /// <summary>
    /// The method a method type names by itself, or null for one that needs a member name. Harmony
    /// binds a constructor, finalizer or operator patch by its kind and ignores any member name:
    /// .ctor, .cctor, Finalize, or the operator's method, op_Addition for OperatorAddition.
    /// </summary>
    private static string MethodNamedByKind(string methodType) => methodType switch
    {
        "Constructor" => ".ctor",
        "StaticConstructor" => ".cctor",
        "Finalizer" => "Finalize",
        { } kind when kind.StartsWith("Operator", StringComparison.Ordinal) => "op_" + kind["Operator".Length..],
        _ => null,
    };

    /// <summary>
    /// Recover <c>[HarmonyPatch]</c> targets that ILSpy's high-level decoder
    /// dropped — the <c>MethodType</c>-bearing overloads — by reading the raw
    /// attribute blob via <see cref="LenientAttributeReader"/>. Merges into the
    /// same <see cref="TargetSpec"/> the high-level pass populates.
    /// </summary>
    private void AbsorbLenientHarmonyAttributes(TargetSpec spec, IEntity owner)
    {
        foreach (var decoded in _host.ReadCustomAttributesLenient(owner))
        {
            if (decoded.AttributeTypeName != "HarmonyLib.HarmonyPatch") continue;

            // HarmonyPatch(string typeName, string methodName, MethodType) is the one constructor
            // that takes two strings, and Harmony looks the first up with AccessTools.TypeByName.
            var arguments = decoded.FixedArguments;
            var namesTypeByString = arguments.Count > 1
                && arguments[0].ArgType == "System.String" && arguments[1].ArgType == "System.String";
            if (namesTypeByString && arguments[0].Value is string typeText && spec.TargetType == null)
            {
                spec.TargetType = TextTypeName(typeText);
                spec.TypeNamedByString = true;
            }

            foreach (var arg in namesTypeByString ? arguments.Skip(1) : arguments)
            {
                if (arg.ArgType == "System.Type" && arg.Value is string typeName)
                    spec.TargetType ??= CSharpTypeName(typeName, qualified: true);
                else if (arg.ArgType == "System.String" && arg.Value is string member)
                    spec.TargetMember ??= member;
                else if (IsMethodTypeArg(arg.ArgType) && arg.Value is int ordinal)
                    spec.MethodType ??= MethodTypeOrdinalToName(ordinal);
                else if (arg.ArgType == "System.Type[]"
                    && arg.Value is IReadOnlyList<string> paramTypes)
                    spec.ParamTypes ??= paramTypes.Select(name => CSharpTypeName(name, qualified: false)).ToList();
            }
        }
    }

    /// <summary>
    /// A type named by text, as Harmony's AccessTools.TypeByName takes it, written as ILens writes
    /// types: a full name with '+' for nesting, an assembly-qualified one, or a bare class name that
    /// matches the first class of that name anywhere, which stays as written.
    /// </summary>
    internal static string TextTypeName(string text) =>
        CSharpTypeName(LenientAttributeReader.NormalizeTypeName(text), qualified: true);

    /// <summary>A type named by text, written as a parameter type is: by its keyword or short name.</summary>
    internal static string ParameterTypeOfText(string text) =>
        CSharpTypeName(LenientAttributeReader.NormalizeTypeName(text), qualified: false);

    /// <summary>
    /// A type name read from an attribute blob, written as ILens writes types, with '.' for
    /// nesting rather than '+'. A <paramref name="qualified"/> name is written as
    /// <see cref="ReferenceFormatter.FormatTypeName(IType)"/> writes a type's name, with its
    /// namespace and qualified type arguments, <c>List&lt;System.Int32&gt;</c>; otherwise as a
    /// parameter type is written, by its keyword or short name, <c>List&lt;int&gt;</c>.
    /// </summary>
    private static string CSharpTypeName(string name, bool qualified)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        if (name.EndsWith("[]", StringComparison.Ordinal))
            return CSharpTypeName(name[..^2], qualified) + "[]";

        var open = name.IndexOf('<');
        var arguments = open < 0 ? "" : "<" + string.Join(", ",
            SplitTypeArguments(name[(open + 1)..^1]).Select(argument => CSharpTypeName(argument, qualified))) + ">";
        var bare = open < 0 ? name : name[..open];

        var nesting = bare.Split('+');
        var dot = nesting[0].LastIndexOf('.');
        var @namespace = dot < 0 ? "" : nesting[0][..dot];
        var outer = dot < 0 ? nesting[0] : nesting[0][(dot + 1)..];
        if (!qualified && nesting.Length == 1 && arguments.Length == 0
            && ReferenceFormatter.KeywordFor(@namespace, outer) is { } keyword)
            return keyword;

        var nested = string.Join(".", nesting.Skip(1).Prepend(outer));
        return (qualified && @namespace.Length > 0 ? @namespace + "." : "") + nested + arguments;
    }

    /// <summary>The type arguments of a generic name, split at the commas between them.</summary>
    private static IEnumerable<string> SplitTypeArguments(string list)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '<') depth++;
            else if (list[i] == '>') depth--;
            else if (list[i] == ',' && depth == 0)
            {
                yield return list[start..i].Trim();
                start = i + 1;
            }
        }
        yield return list[start..].Trim();
    }

    private static bool IsMethodTypeArg(string argType) =>
        argType == HarmonyLibNamespace + ".MethodType";

    /// <summary>
    /// Fold one constructor argument into the cumulative target spec. Distinguishes
    /// by the parameter's CLR type — <c>System.Type</c> sets the target type,
    /// <c>string</c> sets the member name, <c>System.Type[]</c> sets the param-type
    /// list, and an integer maps to the <c>MethodType</c> enum.
    /// </summary>
    private static void AbsorbArg(TargetSpec spec,
        CustomAttributeTypedArgument<IType> arg)
    {
        if (arg.Type is ICSharpCode.Decompiler.TypeSystem.ArrayType
            && arg.Value is ImmutableArray<CustomAttributeTypedArgument<IType>> arr)
        {
            spec.ParamTypes = arr
                .Select(a => a.Value is IType type ? ReferenceFormatter.FormatTypeRef(type) : "?")
                .ToList();
            return;
        }

        switch (arg.Value)
        {
            case IType typeRef:
                spec.TargetType = ReferenceFormatter.FormatTypeName(typeRef);
                break;
            case string name:
                spec.TargetMember = name;
                break;
            case int methodTypeOrdinal when IsMethodTypeEnum(arg.Type):
                spec.MethodType = MethodTypeOrdinalToName(methodTypeOrdinal);
                break;
        }
    }

    private static bool IsMethodTypeEnum(IType paramType) =>
        paramType.Namespace == HarmonyLibNamespace && paramType.Name == "MethodType";

    private static string MethodTypeOrdinalToName(int ordinal) =>
        ordinal >= 0 && ordinal < MethodTypeNames.Length
            ? MethodTypeNames[ordinal]
            : $"Unknown({ordinal})";

    /// <summary>The names of the HarmonyLib attributes on a method, such as HarmonyPrefix.</summary>
    private static IReadOnlyCollection<string> HarmonyAttributesOf(IMethod method)
    {
        List<string> names = null;
        foreach (var attr in method.GetAttributes())
            if (attr.AttributeType.Namespace == HarmonyLibNamespace)
                (names ??= new List<string>()).Add(attr.AttributeType.Name);
        return (IReadOnlyCollection<string>)names ?? Array.Empty<string>();
    }

    /// <summary>
    /// The patch type Harmony gives a method carrying <paramref name="harmonyAttributes"/>, or
    /// null when it is no patch method.
    /// </summary>
    private static string PatchTypeOf(IMethod method, IReadOnlyCollection<string> harmonyAttributes) =>
        PatchTypes.FirstOrDefault(patchType =>
            method.Name == patchType || harmonyAttributes.Contains("Harmony" + patchType));

    /// <summary>
    /// The method that computes a patch class's targets, TargetMethod or TargetMethods, found as
    /// Harmony finds it: by its <c>[HarmonyTargetMethod]</c> or <c>[HarmonyTargetMethods]</c>
    /// attribute, else by its name.
    /// </summary>
    private static IMethod FindTargetResolver(
        IReadOnlyList<(IMethod Method, IReadOnlyCollection<string> HarmonyAttributes)> methods, string name) =>
        methods.FirstOrDefault(method => method.HarmonyAttributes.Contains("Harmony" + name)).Method
        ?? methods.FirstOrDefault(method => method.Method.Name == name).Method;

    private static bool IsHarmonyAttribute(IType type, string name) =>
        type.Namespace == HarmonyLibNamespace && type.Name == name;

    /// <summary>
    /// The targets a <c>TargetMethod</c> or <c>TargetMethods</c> body returns, as
    /// <see cref="HarmonyValueReader"/> reads its returns: a method it looks up, one a search of a
    /// class's methods picks, the elements of a list it returns and the yields of an iterator. A body
    /// that returns a value the reader cannot read, or that cannot be decompiled, also yields '?', so
    /// a list that is not complete says so. Several targets from one <c>TargetMethod</c> mean the
    /// binding is branch-dependent, which the caller flags.
    /// </summary>
    private List<ResolvedTarget> ResolveTargetMethodBody(IMethod method)
    {
        if (_reader.Tree(method) is not { } tree)
            return [Unread];

        var targets = new List<HarmonyValueReader.MethodValue>();
        var unread = false;
        var returned = HarmonyValueReader.ReturnedExpressions(tree).Select(item => item.Expression).ToList();
        foreach (var frame in _reader.Frames(method, tree, returned))
        {
            foreach (var value in HarmonyValueReader.Flatten(_reader.Returns(frame)))
            {
                if (value is HarmonyValueReader.MethodValue target)
                    targets.Add(target);
                else if (value is not HarmonyValueReader.NullValue)
                    unread = true;
            }
        }

        var resolved = targets.Distinct()
            .Select(target => new ResolvedTarget(target.Type.Name, target.Name, target.ParameterTypes, target.Searched, target.MethodType))
            .ToList();
        if (unread || resolved.Count == 0)
            resolved.Add(Unread);
        return resolved;
    }

    private Patch BuildPatchFromAttribute(
        TargetSpec spec, string patchType, IMethod patchMethod, ITypeDefinition patchClass)
    {
        // Classified first, because naming drops the method type the classification reads.
        var resolutionKind = ClassifyAttributeResolution(spec);
        spec = NamePatchedMethod(spec);
        var patchClassName = ReferenceFormatter.FormatTypeName(patchClass);
        var patchSite = patchMethod != null
            ? $"{patchClassName}.{patchMethod.Name}"
            : patchClassName;
        return new Patch(
            spec.TargetType ?? "?",
            spec.TargetMember,
            spec.ParamTypes,
            spec.MethodType,
            patchType,
            resolutionKind,
            patchSite);
    }

    private static Patch BuildPatchFromBody(
        ResolvedTarget target, string resolutionKind, string patchType,
        IMethod patchMethod, ITypeDefinition patchClass)
    {
        var patchClassName = ReferenceFormatter.FormatTypeName(patchClass);
        var patchSite = patchMethod != null
            ? $"{patchClassName}.{patchMethod.Name}"
            : patchClassName;
        return new Patch(
            target.TargetType,
            target.MemberName,
            target.ParamTypes,
            target.MethodType,
            patchType,
            resolutionKind,
            patchSite);
    }

    /// <summary>
    /// Decide TypedAttribute vs StringTargeted. A class named by a string is
    /// StringTargeted wherever it lives. A patch Harmony binds by its kind alone, on a
    /// constructor, finalizer, operator or indexer, needs no member name, so typeof names
    /// all of its target and it is TypedAttribute wherever it lives. Otherwise the named member is
    /// probed on the target type — a target the patch project couldn't have referenced
    /// with <c>nameof(...)</c> (non-public) is necessarily string-literal-
    /// targeted. The distinction matters because string-targeted patches
    /// silently break on rename in a future game version, while
    /// <c>nameof</c>-based ones don't.
    ///
    /// **Limitation**: the probe only looks in the scanned assembly's
    /// <c>MainModule.TypeDefinitions</c>. In the typical use case — a mod
    /// patching another assembly's code (the host application's main DLL,
    /// engine framework DLLs, etc.) — the target type lives in a referenced
    /// assembly that ILens isn't pointed at, so the lookup misses and the
    /// result silently degrades to <c>Attribute</c>. The heuristic is sharp
    /// only when the target lives in the same DLL being scanned.
    /// </summary>
    private string ClassifyAttributeResolution(TargetSpec spec)
    {
        if (spec.TypeNamedByString) return "StringTargeted";
        if (spec.TargetType == null) return "Attribute";
        if (MethodNamedByKind(spec.MethodType) != null || IsIndexerPatch(spec)) return "TypedAttribute";
        var member = NamePatchedMethod(spec).TargetMember;
        if (member == null) return "Attribute";

        // A name several types share cannot be probed, and is no reason to fail the scan.
        var (matches, _) = _host.Resolver.FindTypes(spec.TargetType);
        if (matches.Count != 1) return "Attribute";
        var target = matches[0];

        var isPublic = target.Methods
            .Concat(target.Properties.SelectMany(p => new[] { p.Getter, p.Setter }))
            .Concat(target.Events.SelectMany(e => new[] { e.AddAccessor, e.RemoveAccessor }))
            .Any(m => m != null && m.Name == member && m.Accessibility == Accessibility.Public);
        return isPublic ? "TypedAttribute" : "StringTargeted";
    }

    private sealed record TargetSpec
    {
        public string TargetType;
        public bool TypeNamedByString;
        public string TargetMember;
        public List<string> ParamTypes;
        public string MethodType;
    }

    /// <summary>
    /// A target a body looks up; <see cref="Searched"/> when the body's own tests pick the overload,
    /// and <see cref="MethodType"/> the kind of method patched when the name does not show it.
    /// </summary>
    private sealed record ResolvedTarget(
        string TargetType, string MemberName, IReadOnlyList<string> ParamTypes, bool Searched = false, string MethodType = null);
}
