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
/// every target named where Harmony applies no patch, every field the assembly
/// looks up by reflection in any class, every field Harmony hands a patch method
/// through a <c>___name</c> parameter, every method, property and constructor
/// of another assembly's class that it looks up by reflection, and every method of
/// one that Harmony hands a patch method as a delegate. The scan returns
/// records, and <c>find_harmony_dependencies</c> writes them as lines of text,
/// printing a line several records give once.
///
/// Patch methods and target resolvers are found as Harmony finds them, by their
/// name or by their marker attribute, and each patch method binds to its own
/// target: the class's <c>[HarmonyPatch]</c> with the method's own laid over it.
///
/// A target named where Harmony applies no patch is listed with <see cref="Unapplied"/>
/// in place of the patch kind: the target of a method whose own <c>[HarmonyPatch]</c>,
/// laid over its class's, names one but whose name and attributes make it no patch
/// method, such as one named NewThingPostfix, and a target a class without a patch
/// method names itself. Harmony never looks up the target of such a method or of such a
/// class's attribute, so a patch its author meant never applies; it does run such a
/// class's TargetMethod, and a missing target then stops the mod's PatchAll. A class that
/// patches nothing and names no target, such as a container of nested patch classes,
/// gives no line.
///
/// Attribute-based bindings are read directly from metadata. TargetMethod and
/// TargetMethods bodies, Harmony.Patch calls and lookups are found in the IL
/// and read from the decompiler's syntax trees by <see cref="HarmonyValueReader"/>,
/// which follows a value back to where the assembly writes it.
/// </summary>
public sealed class HarmonyDependencyScanner
{
    public const string HarmonyLibNamespace = "HarmonyLib";

    // The patch types, in the order Harmony tries them: a method is of the first type it is
    // named after or marked with, as [HarmonyPrefix] marks a prefix. Harmony 2.4 also tries
    // InnerPrefix and InnerPostfix, which patch a call inside the target; 2.4 has neither an
    // attribute nor a HarmonyMethod field that names that call, so a line could not say what
    // one patches. The release candidates of 3.0 add HarmonyMethod fields that name it, which
    // the scanner does not read.
    private static readonly string[] PatchTypes =
    {
        "Prefix", "Postfix", "Transpiler", "Finalizer", "ReversePatch"
    };

    // The inner patch types named above. A method of one gets no line, but it is a patch method to
    // Harmony and so is not unapplied.
    private static readonly string[] InnerPatchTypes = { "InnerPrefix", "InnerPostfix" };

    /// <summary>The word that stands in place of the patch kind for a target Harmony applies no patch to.</summary>
    public const string Unapplied = "Unapplied";

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

    // The lookup calls whose results the patch lines show as targets, which get no line of their own.
    private readonly HashSet<HarmonyValueReader.Origin> _shownLookups = new();

    public HarmonyDependencyScanner(AssemblyHost host)
    {
        _host = host;
        _reader = new HarmonyValueReader(host, DelegateTarget);
    }

    /// <summary>
    /// A patch target, with <c>MethodType</c> the kind of method patched when <c>TargetMember</c>
    /// does not show it, such as Enumerator, and otherwise null. <c>PatchType</c> is
    /// <see cref="Unapplied"/> for a target Harmony applies no patch to.
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

    /// <summary>
    /// A method, property, event or constructor looked up by reflection: the first word of its
    /// line, Method, Property, Event or Member, the class and the member, each null where not read,
    /// the parameter types that pin an overload, and the label naming the lookup call.
    /// </summary>
    public sealed record MemberLookup(
        string Word,
        string ContextType,
        string MemberName,
        IReadOnlyList<string> ParamTypes,
        string Label,
        string Site);

    public sealed record ScanResult(
        IReadOnlyList<Patch> Patches,
        IReadOnlyList<FieldAccess> FieldAccesses,
        IReadOnlyList<MemberLookup> MemberLookups);

    /// <summary>
    /// The method a delegate type's Harmony attributes name: the class, null where they name none,
    /// the member, and the parameter types where they pin an overload.
    /// </summary>
    public sealed record DelegateMethod(string Class, string Member, IReadOnlyList<string> ParameterTypes);

    public ScanResult Scan()
    {
        var patches = new List<Patch>();
        var fieldAccesses = new List<FieldAccess>();
        var memberLookups = new List<MemberLookup>();

        foreach (var type in _host.TypeSystem.MainModule.TypeDefinitions)
            ProcessPatchClass(type, patches, fieldAccesses, memberLookups);
        var passed = ScanPatchCalls(patches, fieldAccesses, memberLookups);
        // A method a Harmony.Patch call passes is applied by hand, whatever its own [HarmonyPatch] says.
        patches.RemoveAll(patch => patch.PatchType == Unapplied && passed.Contains(patch.PatchSite));
        ScanFieldLookups(fieldAccesses);
        // Last, since it leaves out the lookups the patch lines above show as targets.
        ScanMemberLookups(memberLookups);

        return new ScanResult(patches, fieldAccesses, memberLookups);
    }

    private void ProcessPatchClass(
        ITypeDefinition type, List<Patch> patches, List<FieldAccess> fieldAccesses, List<MemberLookup> memberLookups)
    {
        // A delegate type's Harmony attributes name the method Harmony hands a patch as that
        // delegate. Harmony's PatchAll finds no patch method in such a type, so they name no target.
        if (type.Kind == TypeKind.Delegate)
            return;

        // Every type is looked at, so each method's Harmony attributes are read once.
        var methods = type.DeclaredMethods()
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
        // The methods whose own [HarmonyPatch] may name a target that Harmony never applies, since
        // their name and attributes make them no patch method.
        var unappliedMethods = methods
            .Where(method => method.HarmonyAttributes.Contains("HarmonyPatch")
                && PatchTypeOf(method.Method, method.HarmonyAttributes) == null
                && !IsInnerPatch(method.Method, method.HarmonyAttributes))
            .Select(method => method.Method)
            .ToList();

        // A class counts through its methods' own [HarmonyPatch] too, since that may be all that
        // names its targets: on a class carrying only a category, or one applied by hand without
        // any class attribute.
        var classTarget = ReadTargetSpec(type);
        bool isPatchClass = classTarget != null
            || hasBodyResolver
            || patchMethods.Any(patchMethod => patchMethod.NamesTarget)
            || unappliedMethods.Count > 0;
        if (!isPatchClass) return;

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

        // An unapplied target is listed only where it names a class, so that a '?' keeps meaning
        // that the scan could not read a target Harmony looks up.
        var unappliedLines = 0;
        foreach (var method in unappliedMethods)
        {
            var target = Merge(classTarget, ReadTargetSpec(method));
            if (target.TargetType == null)
                continue;
            patches.Add(BuildPatchFromAttribute(target, Unapplied, method, type));
            unappliedLines++;
        }

        // A class without a patch method lists the targets it names itself, under its own name: those
        // its TargetMethod or TargetMethods gives, and otherwise the one its [HarmonyPatch] names,
        // unless a method's line above carries it, merged with the method's own.
        var sites = patchMethods.Count > 0
            ? patchMethods.Select(patchMethod => (patchMethod.PatchType, patchMethod.Method)).ToList()
            : [(Unapplied, (IMethod)null)];
        if (patchMethods.Count == 0 && !hasBodyResolver && unappliedLines == 0 && classTarget?.TargetType != null)
            patches.Add(BuildPatchFromAttribute(classTarget, Unapplied, null, type));

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
                foreach (var (patchType, patchSite) in sites)
                    patches.Add(BuildPatchFromBody(rt, kind, patchType, patchSite, type));
        }

        if (targetMethodsMethod != null)
        {
            foreach (var rt in ResolveTargetMethodBody(targetMethodsMethod))
                foreach (var (patchType, patchSite) in sites)
                    patches.Add(BuildPatchFromBody(
                        rt, rt.Searched ? "TargetMethodsSearch" : "TargetMethods", patchType, patchSite, type));
        }

        AddInjectedFields(type, patchMethods.Select(patchMethod => (patchMethod.PatchType, patchMethod.Method)),
            patches.Skip(firstPatch).ToList(), fieldAccesses);
        foreach (var (patchType, method, _) in patchMethods)
            memberLookups.AddRange(InjectedDelegates(method, patchType, $"{ReferenceFormatter.FormatTypeName(type)}.{method.Name}"));
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
    /// The methods Harmony hands a prefix, postfix or finalizer as delegates, labeled Injected, each
    /// once. Harmony gives a parameter a delegate to the method its type's Harmony attributes name,
    /// see <see cref="DelegateTarget"/>, where the parameter's name is none of Harmony's own, all of
    /// which start with __, and fails the patch where that method is missing. A postfix that returns
    /// the type of its first parameter is handed the result there instead. Harmony first gives a
    /// parameter named like one of the patched method's, or mapped to one by [HarmonyArgument], that
    /// argument. The scan does not compare the names: only a method of this assembly can take a
    /// delegate type it defines, and a patch of another assembly's method that names a parameter so
    /// breaks either way. A method of a class the scanned assembly defines, a constructed generic
    /// class included, gives no line, as no lookup of one does.
    /// </summary>
    private IEnumerable<MemberLookup> InjectedDelegates(IMethod method, string patchType, string site)
    {
        if (patchType is not ("Prefix" or "Postfix" or "Finalizer"))
            return [];
        var parameters = method.Parameters.AsEnumerable();
        if (patchType == "Postfix" && method.Parameters.Count > 0 && !method.ReturnType.IsKnownType(KnownTypeCode.Void)
            && method.Parameters[0].Type.ReflectionName == method.ReturnType.ReflectionName)
            parameters = parameters.Skip(1);
        return parameters
            .Where(parameter => !parameter.Name.StartsWith("__", StringComparison.Ordinal))
            .Select(parameter => parameter.Type.GetDefinition() is { } type ? DelegateTarget(type) : null)
            .Where(target => target is not null && (target.Class is null || _host.Resolver.DefinitionOf(target.Class) is null))
            .Select(target => new MemberLookup("Method", target.Class, target.Member, target.ParameterTypes, "Injected", site))
            .DistinctBy(lookup => KeyOf(lookup));
    }

    /// <summary>
    /// The method a delegate type's [HarmonyDelegate] and [HarmonyPatch] attributes name, merged as
    /// for a patch and named as a patch's target is, which Harmony reads the same way for a delegate
    /// type, [HarmonyDelegate] deriving from [HarmonyPatch]. Null for a type that is no delegate type
    /// or carries no such attribute, and for one another assembly defines: ILens's type system does
    /// not tell such a type from any other class of that assembly, and where the decompiler's holds
    /// it, as for a type argument, the lenient reader could not read its attributes, which it reads
    /// from the scanned file alone.
    /// </summary>
    private DelegateMethod DelegateTarget(ITypeDefinition type)
    {
        if (type.Kind != TypeKind.Delegate || type.ParentModule?.AssemblyName != _host.TypeSystem.MainModule.AssemblyName
            || ReadTargetSpec(type) is not { } spec)
            return null;
        var named = NamePatchedMethod(spec);
        return new DelegateMethod(named.TargetType, named.TargetMember, named.ParamTypes);
    }

    private static string KeyOf(MemberLookup lookup) =>
        string.Join("\n", lookup.Word, lookup.ContextType, lookup.MemberName,
            lookup.ParamTypes is null ? "-" : string.Join(",", lookup.ParamTypes), lookup.Label, lookup.Site);

    /// <summary>
    /// The patches the assembly applies by calling Harmony.Patch. Each call gives one line for each
    /// patch method it passes, as prefix, postfix, transpiler or finalizer by the argument's position,
    /// and each target it names, read in each frame its member is read in. The label names the member
    /// that writes the values out: the calling member, or the member whose call or construction gives
    /// it its values. A call the IL shows and the tree does not is listed with '?' throughout, so a
    /// patch is never left out unseen. The ___name parameters of the prefixes, postfixes and
    /// finalizers these calls pass are listed as attribute patches' are. PatchProcessor is not read:
    /// its one use in the workshop corpus re-applies the patches a method already has and names no
    /// patch method. The methods Harmony hands those patch methods as delegates are listed as for
    /// attribute patches too. Returns the patch methods the calls pass, named as a line's site names
    /// them.
    /// </summary>
    private IReadOnlySet<string> ScanPatchCalls(List<Patch> patches, List<FieldAccess> fieldAccesses, List<MemberLookup> memberLookups)
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
        var listedDelegates = memberLookups.Select(lookup => KeyOf(lookup)).ToHashSet(StringComparer.Ordinal);
        foreach (var group in callPatches.Where(patch => patch.PatchType is "Prefix" or "Postfix" or "Finalizer")
                     .GroupBy(patch => patch.PatchSite))
        {
            if (!patchMethods.TryGetValue(group.Key, out var patchMethod)
                || patchMethod.Class.Type?.GetDefinition() is not { } patchClass)
                continue;
            var injected = new List<FieldAccess>();
            var methods = patchClass.DeclaredMethods().Where(method => method.Name == patchMethod.Name).ToList();
            foreach (var method in methods)
                AddInjectedFields(method, group.Key, group, injected);
            fieldAccesses.AddRange(injected.Distinct().Where(listed.Add));
            foreach (var patchType in group.Select(patch => patch.PatchType).Distinct())
                foreach (var method in methods)
                    memberLookups.AddRange(InjectedDelegates(method, patchType, group.Key)
                        .Where(lookup => listedDelegates.Add(KeyOf(lookup))));
        }
        return patchMethods.Keys.ToHashSet(StringComparer.Ordinal);
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
                        if (target is HarmonyValueReader.MethodValue shown)
                            _shownLookups.UnionWith(shown.Origins);
                        var line = target is HarmonyValueReader.MethodValue found
                            ? new Patch(found.Type.Name, found.Member, found.ParameterTypes, found.MethodType, PatchCallTypes[position - 1],
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
    /// Every field the assembly looks up by reflection or names to a transpiler's helpers, in any
    /// class. The members whose IL, or the IL of code the compiler generated for them, calls a field
    /// lookup are decompiled, and each lookup is read from the syntax tree. A call the IL shows that
    /// the tree does not, as when a member cannot be decompiled, is listed with '?' for its class and
    /// field, so a field is never left out unseen.
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
    /// Every method, property, event and constructor the assembly looks up by reflection or names to
    /// a transpiler's helpers, in any class, found and read as <see cref="ScanFieldLookups"/> finds
    /// and reads the fields. A lookup that names a member of a class the scanned assembly defines is
    /// left out, and so is one whose result a patch line shows as its target. A call the IL shows
    /// that the tree does not is listed with nothing read, so a member is never left out unseen.
    /// </summary>
    private void ScanMemberLookups(List<MemberLookup> output)
    {
        foreach (var (member, calls) in _host.MembersCalling(HarmonyValueReader.IsMemberLookup))
        {
            var site = SiteOf(member);
            var (lookups, found) = _reader.Tree(member) is { } tree
                ? MemberLookupReader.Read(_reader, member, tree, _shownLookups)
                : ([], []);
            output.AddRange(lookups.Select(lookup =>
                new MemberLookup(lookup.Word, lookup.Class, lookup.Member, lookup.ParameterTypes, lookup.Label, site)));

            var unseen = calls.GroupBy(call => call)
                .SelectMany(group => Enumerable.Repeat(group.Key, Math.Max(0, group.Count() - found.Count(call => call == group.Key))));
            output.AddRange(unseen.Select(call =>
                new MemberLookup(HarmonyValueReader.WordOf(call), null, null, null, HarmonyValueReader.LabelOf(call), site)));
        }
    }

    /// <summary>
    /// The target named by the <c>[HarmonyPatch]</c> attributes on a patch class or a patch
    /// method, or by those and <c>[HarmonyDelegate]</c> on a delegate type, or null when it carries
    /// none. Harmony lets one target be split across several attributes (e.g.
    /// <c>[HarmonyPatch(typeof(X))] [HarmonyPatch("Y")]</c>), so the arguments are combined by
    /// their type rather than by attribute.
    /// </summary>
    private TargetSpec ReadTargetSpec(IEntity owner)
    {
        TargetSpec spec = null;
        bool anyDecodeError = false;

        foreach (var attr in owner.GetAttributes())
        {
            if (!NamesTarget(attr.AttributeType.Namespace + "." + attr.AttributeType.Name)) continue;

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
    /// the parameterless constructor, as AccessTools.DeclaredConstructor does with none. The argument
    /// types stay where Harmony's GetOriginalMethod reads them: it binds a static constructor, a
    /// finalizer and an accessor named by its property or event without them, in every build from 2.0
    /// to 3.0 that has the kind, so such a target names no overload whatever its attribute carries.
    /// </summary>
    private TargetSpec NamePatchedMethod(TargetSpec spec) =>
        MethodNamedByKind(spec.MethodType) is { } method
            ? Named(spec, method, spec.MethodType switch
            {
                "Constructor" => spec.ParamTypes ?? [],
                "StaticConstructor" or "Finalizer" => null,
                _ => spec.ParamTypes,
            })
        : IsIndexerPatch(spec) ? NameIndexerAccessor(spec)
        : spec.MethodType switch
        {
            "Normal" => spec with { MethodType = null },
            "Getter" => Named(spec, "get_" + spec.TargetMember, null),
            "Setter" => Named(spec, "set_" + spec.TargetMember, null),
            "EventAdd" when spec.TargetMember != null => Named(spec, "add_" + spec.TargetMember, null),
            "EventRemove" when spec.TargetMember != null => Named(spec, "remove_" + spec.TargetMember, null),
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
        var indexers = _host.Resolver.DefinitionOf(spec.TargetType) is { } type
            ? type.Properties.Where(property => property.IsIndexer).ToList()
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
        return Named(spec, name, parameters);
    }

    private static TargetSpec Named(TargetSpec spec, string method, List<string> parameterTypes) =>
        spec with { TargetMember = method, MethodType = null, ParamTypes = parameterTypes };

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
    /// Recover <c>[HarmonyPatch]</c> and <c>[HarmonyDelegate]</c> targets that ILSpy's high-level
    /// decoder dropped — the overloads that take <c>MethodType</c> or <c>MethodDispatchType</c> —
    /// by reading the raw attribute blob via <see cref="LenientAttributeReader"/>. Merges into the
    /// same <see cref="TargetSpec"/> the high-level pass populates.
    /// </summary>
    private void AbsorbLenientHarmonyAttributes(TargetSpec spec, IEntity owner)
    {
        foreach (var decoded in _host.ReadCustomAttributesLenient(owner))
        {
            if (!NamesTarget(decoded.AttributeTypeName)) continue;

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
    /// namespace, <c>System.Collections.Generic.List&lt;int&gt;</c>, a nested class with the
    /// classes enclosing it, and a generic class without type arguments in C#'s unbound form,
    /// <c>System.Collections.Generic.Dictionary&lt;,&gt;.Enumerator</c>; otherwise as
    /// <see cref="ReferenceFormatter.FormatTypeRef(IType)"/> writes a parameter type, by its
    /// keyword or short name, <c>List&lt;int&gt;</c>, a nested class with the classes enclosing it
    /// where one of them has type arguments, <c>Dictionary&lt;int, string&gt;.Enumerator</c>, and by
    /// its own name otherwise, a generic class without type arguments by its name alone, and a
    /// nullable value type as <c>int?</c>. Type arguments are written as parameter types are either
    /// way, each class's after its own name. An array
    /// is written as <see cref="ReferenceFormatter.FormatTypeRef(IType)"/> writes one, with its
    /// dimensions and the outermost array's brackets first, where a reflection name lists them
    /// innermost first: <c>System.Int32[,][]</c> is <c>int[][,]</c>.
    /// </summary>
    private static string CSharpTypeName(string name, bool qualified)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        var element = LenientAttributeReader.WithoutArrayBrackets(name, out var brackets);
        if (brackets.Count > 0)
        {
            // "[*]" is an array of one dimension that need not start at zero, which C# cannot
            // write and the decompiler writes as "[]".
            var csharp = brackets.AsEnumerable().Reverse().Select(group => "[" + new string(',', group.Count(c => c == ',')) + "]");
            return CSharpTypeName(element, qualified) + string.Concat(csharp);
        }

        var classes = SplitOutsideArguments(name, '+').Select(NamedClass.Read).ToList();
        var dot = classes[0].Name.LastIndexOf('.');
        var @namespace = dot < 0 ? "" : classes[0].Name[..dot];
        classes[0] = classes[0] with { Name = classes[0].Name[(dot + 1)..] };
        var own = classes[^1];
        if (!qualified && classes.Count == 1 && own.Count == 0 && own.Arguments.Count == 0
            && ReferenceFormatter.KeywordFor(@namespace, own.Name) is { } keyword)
            return keyword;
        if (!qualified && classes.Count == 1 && own.Arguments.Count == 1 && (@namespace, own.Name) is ("System", "Nullable"))
            return own.Arguments[0] + "?";

        if (qualified)
            return (@namespace.Length > 0 ? @namespace + "." : "") + string.Join(".", classes.Select(named => named.Write(unbound: true)));
        return classes.SkipLast(1).Any(named => named.Arguments.Count > 0)
            ? string.Join(".", classes.Select(named => named.Write(unbound: false)))
            : own.Write(unbound: false);
    }

    /// <summary>
    /// One class of a name <see cref="CSharpTypeName"/> reads: its name, the count of type
    /// parameters its name carries where no type arguments follow it, and its type arguments, each
    /// written as a parameter type is.
    /// </summary>
    private sealed record NamedClass(string Name, int Count, IReadOnlyList<string> Arguments)
    {
        public static NamedClass Read(string text)
        {
            var open = text.IndexOf('<');
            if (open >= 0)
                return new NamedClass(text[..open], 0, SplitOutsideArguments(text[(open + 1)..^1], ',')
                    .Select(argument => CSharpTypeName(argument, qualified: false)).ToList());
            var tick = text.IndexOf('`');
            return tick >= 0 && int.TryParse(text[(tick + 1)..], out var count)
                ? new NamedClass(text[..tick], count, [])
                : new NamedClass(text, 0, []);
        }

        /// <summary>
        /// The class with its type arguments, or, where its name counts type parameters and no
        /// arguments follow it, in C#'s unbound form when <paramref name="unbound"/>,
        /// <c>Dictionary&lt;,&gt;</c>, and by its name alone otherwise.
        /// </summary>
        public string Write(bool unbound) =>
            Arguments.Count > 0 ? $"{Name}<{string.Join(", ", Arguments)}>"
            : unbound && Count > 0 ? $"{Name}<{new string(',', Count - 1)}>"
            : Name;
    }

    /// <summary>
    /// The parts of a name between the <paramref name="separator"/>s outside its angle brackets:
    /// the classes of a nested name at '+', and the type arguments of a generic one at ','.
    /// </summary>
    private static IEnumerable<string> SplitOutsideArguments(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] == separator && depth == 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }
        yield return text[start..].Trim();
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

    /// <summary>Whether Harmony 2.4 takes a method as a patch of a call inside its target, which gets no line.</summary>
    private static bool IsInnerPatch(IMethod method, IReadOnlyCollection<string> harmonyAttributes) =>
        InnerPatchTypes.Any(patchType =>
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

    /// <summary>
    /// Whether an attribute, by its full name, names a target: <c>[HarmonyPatch]</c>, and
    /// <c>[HarmonyDelegate]</c>, which C# lets stand on a delegate type alone.
    /// </summary>
    private static bool NamesTarget(string attributeType) =>
        attributeType is HarmonyLibNamespace + ".HarmonyPatch" or HarmonyLibNamespace + ".HarmonyDelegate";

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
                {
                    targets.Add(target);
                    _shownLookups.UnionWith(target.Origins);
                }
                else if (value is not HarmonyValueReader.NullValue)
                    unread = true;
            }
        }

        var resolved = targets.Distinct()
            .Select(target => new ResolvedTarget(target.Type.Name, target.Member, target.ParameterTypes, target.Searched, target.MethodType))
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

        // A name that means several types, or a type the scanned assembly only references or
        // forwards, cannot be probed, and is no reason to fail the scan.
        if (_host.Resolver.DefinitionOf(spec.TargetType) is not { } target) return "Attribute";

        var isPublic = target.DeclaredMethods()
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
