using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Walks a Harmony-patching assembly and extracts the full reflective surface a
/// caller needs to ask "do my patches still bind on game version X?":
/// every patch target (from <c>[HarmonyPatch]</c> attributes and from
/// <c>TargetMethod</c>/<c>TargetMethods</c> bodies) and every reflective field
/// access (<c>AccessTools.Field</c>, <c>AccessTools.FieldRefAccess</c>,
/// <c>Traverse.Create(...).Field</c>). Results are structured data; the tool
/// layer serializes to JSON.
///
/// Attribute-based bindings are read directly from metadata. Body-derived
/// bindings (TargetMethod, TargetMethods, AccessTools calls, Traverse calls)
/// are extracted by decompiling each patch-class method to C# and regex-
/// matching the deterministic ILSpy output — appropriate here because the
/// input is reproducible decompiler text, not arbitrary hand-written source.
/// </summary>
public sealed class HarmonyDependencyScanner
{
    public const string HarmonyLibNamespace = "HarmonyLib";

    private static readonly HashSet<string> PatchMethodNames = new(StringComparer.Ordinal)
    {
        "Prefix", "Postfix", "Transpiler", "Finalizer"
    };

    private static readonly string[] MethodTypeNames =
    {
        "Normal", "Getter", "Setter", "Constructor", "StaticConstructor",
        "Enumerator", "Async"
    };

    // Decompiler-output regexes. Anchored on the well-known HarmonyLib API names
    // and tolerant of namespace-prefixed and whitespace-varied renderings.
    private static readonly Regex AccessToolsMethodRx = new(
        @"AccessTools\.Method\s*\(\s*typeof\s*\(\s*([^)]+?)\s*\)\s*,\s*""([^""]+)""(?:\s*,\s*new\s+(?:[\w.:]*\s*)?Type\s*\[\s*\d*\s*\]\s*\{\s*([^}]*)\s*\})?",
        RegexOptions.Compiled);

    private static readonly Regex AccessToolsFieldRx = new(
        @"AccessTools\.Field\s*\(\s*typeof\s*\(\s*([^)]+?)\s*\)\s*,\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex AccessToolsFieldRefAccessRx = new(
        @"AccessTools\.FieldRefAccess\s*<\s*([^,>]+?)\s*,\s*[^>]+?\s*>\s*\(\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled);

    // The decompiler inserts a `(object)` cast around the Traverse.Create
    // argument because Create's parameter type is object. The optional
    // `(?:\([^)]*\)\s*)?` group skips one nested cast before the receiver
    // expression itself.
    private static readonly Regex TraverseFieldRx = new(
        @"Traverse\.Create\s*\(\s*(?:\([^)]*\)\s*)?([^)]+?)\s*\)\s*\.\s*Field\s*\(\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex TypeofInArrayRx = new(
        @"typeof\s*\(\s*([^)]+?)\s*\)",
        RegexOptions.Compiled);

    private readonly AssemblyHost _host;

    public HarmonyDependencyScanner(AssemblyHost host)
    {
        _host = host;
    }

    public sealed record Patch(
        string TargetType,
        string TargetMember,
        IReadOnlyList<string> ParamTypes,
        string MethodType,
        string PatchType,
        string ResolutionKind,
        string PatchClass,
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

        return new ScanResult(patches, fieldAccesses);
    }

    private void ProcessPatchClass(
        ITypeDefinition type, List<Patch> patches, List<FieldAccess> fieldAccesses)
    {
        var attrTargets = ExtractAttributeTargets(type);
        var targetMethodMethod = type.Methods.FirstOrDefault(m => m.Name == "TargetMethod");
        var targetMethodsMethod = type.Methods.FirstOrDefault(m => m.Name == "TargetMethods");

        bool isPatchClass = attrTargets.Count > 0
            || targetMethodMethod != null
            || targetMethodsMethod != null;
        if (!isPatchClass) return;

        var patchMethods = FindPatchMethods(type);
        if (patchMethods.Count == 0)
            patchMethods.Add(("(none)", null));

        // An empty [HarmonyPatch] paired with TargetMethod/TargetMethods is the
        // canonical "I'll compute the target myself" marker — suppress its
        // (targetType=?) attribute entry so it doesn't drown the actual
        // resolved targets the body produces.
        bool hasBodyResolver = targetMethodMethod != null || targetMethodsMethod != null;
        foreach (var target in attrTargets)
        {
            if (target.TargetType == null && hasBodyResolver)
                continue;
            foreach (var (patchType, patchSite) in patchMethods)
                patches.Add(BuildPatchFromAttribute(target, patchType, patchSite, type));
        }

        if (targetMethodMethod != null)
        {
            var resolved = ResolveTargetMethodBody(targetMethodMethod);
            // A TargetMethod that fans out to multiple AccessTools.Method calls
            // can't have a single binding at load time — surface that as
            // DynamicTargetMethod so callers know to inspect manually.
            var kind = resolved.Count > 1 ? "DynamicTargetMethod" : "TargetMethod";
            if (resolved.Count == 0)
            {
                resolved = new List<ResolvedTarget>
                {
                    new ResolvedTarget("?", null, null)
                };
                kind = "DynamicTargetMethod";
            }
            foreach (var rt in resolved)
                foreach (var (patchType, patchSite) in patchMethods)
                    patches.Add(BuildPatchFromBody(rt, kind, patchType, patchSite, type));
        }

        if (targetMethodsMethod != null)
        {
            var resolved = ResolveTargetMethodBody(targetMethodsMethod);
            if (resolved.Count == 0)
                resolved.Add(new ResolvedTarget("?", null, null));
            foreach (var rt in resolved)
                foreach (var (patchType, patchSite) in patchMethods)
                    patches.Add(BuildPatchFromBody(rt, "TargetMethods", patchType, patchSite, type));
        }

        foreach (var method in type.Methods)
            ScanForFieldAccesses(method, type, fieldAccesses);
    }

    /// <summary>
    /// Walk every <c>[HarmonyPatch]</c> attribute on the type and merge their
    /// constructor arguments into a single combined target spec — Harmony lets
    /// callers split the spec across multiple attributes
    /// (e.g. <c>[HarmonyPatch(typeof(X))] [HarmonyPatch("Y")]</c>), so we
    /// reconstruct the spec by argument type rather than by attribute count.
    /// Returns a list because a single attribute carries a typed param-types
    /// array, but a multi-attribute spec only ever resolves to one entry.
    /// </summary>
    private List<TargetSpec> ExtractAttributeTargets(ITypeDefinition type)
    {
        var spec = new TargetSpec();
        bool anyHarmonyPatch = false;
        bool anyDecodeError = false;

        foreach (var attr in type.GetAttributes())
        {
            var t = attr.AttributeType;
            if (t.Namespace != HarmonyLibNamespace) continue;
            if (t.Name != "HarmonyPatch") continue;

            anyHarmonyPatch = true;
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
            AbsorbLenientHarmonyAttributes(spec, type);

        // MethodType.Constructor / .StaticConstructor name the implicit member —
        // map them to the IL constructor names so the target is bindable. Done
        // once here so both the high-level and the lenient path benefit.
        if (spec.TargetMember == null)
            spec.TargetMember = spec.MethodType switch
            {
                "Constructor" => ".ctor",
                "StaticConstructor" => ".cctor",
                _ => null,
            };

        return anyHarmonyPatch ? new List<TargetSpec> { spec } : new List<TargetSpec>();
    }

    /// <summary>
    /// Recover <c>[HarmonyPatch]</c> targets that ILSpy's high-level decoder
    /// dropped — the <c>MethodType</c>-bearing overloads — by reading the raw
    /// attribute blob via <see cref="LenientAttributeReader"/>. Merges into the
    /// same <see cref="TargetSpec"/> the high-level pass populates.
    /// </summary>
    private void AbsorbLenientHarmonyAttributes(TargetSpec spec, ITypeDefinition type)
    {
        foreach (var decoded in _host.ReadCustomAttributesLenient(type))
        {
            if (decoded.AttributeTypeName != "HarmonyLib.HarmonyPatch") continue;
            foreach (var arg in decoded.FixedArguments)
            {
                if (arg.ArgType == "System.Type" && arg.Value is string typeName)
                    spec.TargetType ??= typeName;
                else if (arg.ArgType == "System.String" && arg.Value is string member)
                    spec.TargetMember ??= member;
                else if (IsMethodTypeArg(arg.ArgType) && arg.Value is int ordinal)
                    spec.MethodType ??= MethodTypeOrdinalToName(ordinal);
                else if (arg.ArgType == "System.Type[]"
                    && arg.Value is IReadOnlyList<string> paramTypes)
                    spec.ParamTypes ??= paramTypes.ToList();
            }
        }
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
                .Select(a => (a.Value as IType)?.FullName ?? "?")
                .ToList();
            return;
        }

        switch (arg.Value)
        {
            case IType typeRef:
                spec.TargetType = typeRef.FullName;
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

    private static List<(string PatchType, IMethod Method)> FindPatchMethods(ITypeDefinition type)
    {
        var result = new List<(string, IMethod)>();
        foreach (var m in type.Methods)
            if (PatchMethodNames.Contains(m.Name))
                result.Add((m.Name, m));
        return result;
    }

    /// <summary>
    /// Decompile a <c>TargetMethod</c> / <c>TargetMethods</c> body to C# and
    /// pull every <c>AccessTools.Method(typeof(X), "Y" [, new Type[] {...}])</c>
    /// call out of it. Each match becomes one resolved target; a body with no
    /// matches is reported as unresolved (DynamicTargetMethod) by the caller.
    /// Multiple matches inside a single <c>TargetMethod</c> mean the binding is
    /// branch-dependent — the caller flags that, too.
    /// </summary>
    private List<ResolvedTarget> ResolveTargetMethodBody(IMethod method)
    {
        string source;
        try
        {
            source = _host.DecompileMethod(method);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ILens] HarmonyDependencyScanner: failed to decompile " +
                $"{method.DeclaringType?.FullName}.{method.Name} for TargetMethod " +
                $"resolution: {ex.GetType().Name}: {ex.Message}");
            return new List<ResolvedTarget>();
        }

        var results = new List<ResolvedTarget>();
        foreach (Match match in AccessToolsMethodRx.Matches(source))
        {
            var targetType = match.Groups[1].Value.Trim();
            var memberName = match.Groups[2].Value;
            var paramTypes = ParseParamTypeArray(match.Groups[3].Value);
            results.Add(new ResolvedTarget(targetType, memberName, paramTypes));
        }
        return results;
    }

    private static List<string> ParseParamTypeArray(string arrayBody)
    {
        if (string.IsNullOrWhiteSpace(arrayBody)) return null;
        var list = new List<string>();
        foreach (Match m in TypeofInArrayRx.Matches(arrayBody))
            list.Add(m.Groups[1].Value.Trim());
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Decompile each patch-class method and scan the resulting C# for the
    /// three reflective-access shapes <c>AccessTools.Field</c>,
    /// <c>AccessTools.FieldRefAccess</c>, and <c>Traverse.Create(...).Field</c>.
    /// Decompilation cost is bounded by the patch-class count, not the whole
    /// assembly, because non-patch types are filtered out one level up.
    /// </summary>
    private void ScanForFieldAccesses(
        IMethod method, ITypeDefinition declaringType, List<FieldAccess> output)
    {
        string source;
        try
        {
            source = _host.DecompileMethod(method);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ILens] HarmonyDependencyScanner: failed to decompile " +
                $"{declaringType.FullName}.{method.Name} for field-access scan: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var patchSite = $"{declaringType.FullName}.{method.Name}";

        foreach (Match m in AccessToolsFieldRx.Matches(source))
            output.Add(new FieldAccess(
                m.Groups[1].Value.Trim(),
                m.Groups[2].Value,
                "AccessToolsField",
                patchSite));

        foreach (Match m in AccessToolsFieldRefAccessRx.Matches(source))
            output.Add(new FieldAccess(
                m.Groups[1].Value.Trim(),
                m.Groups[2].Value,
                "FieldRefAccess",
                patchSite));

        foreach (Match m in TraverseFieldRx.Matches(source))
        {
            var receiverExpr = m.Groups[1].Value.Trim();
            var contextType = InferTraverseReceiverType(receiverExpr, method);
            output.Add(new FieldAccess(
                contextType,
                m.Groups[2].Value,
                "TraverseField",
                patchSite));
        }
    }

    /// <summary>
    /// Best-effort: if the receiver expression names a parameter of
    /// <paramref name="method"/>, return that parameter's declared type.
    /// Otherwise return "&lt;runtime&gt;" — the receiver type is decided at
    /// runtime and the consumer has to inspect manually.
    /// </summary>
    private static string InferTraverseReceiverType(string receiverExpr, IMethod method)
    {
        foreach (var p in method.Parameters)
            if (p.Name == receiverExpr)
                return ReferenceFormatter.FormatTypeRef(p.Type);
        return "<runtime>";
    }

    private static Patch BuildPatchFromAttribute(
        TargetSpec spec, string patchType, IMethod patchMethod, ITypeDefinition patchClass)
    {
        var resolutionKind = ClassifyAttributeResolution(spec, patchClass);
        var patchSite = patchMethod != null
            ? $"{patchClass.FullName}.{patchMethod.Name}"
            : patchClass.FullName;
        return new Patch(
            spec.TargetType ?? "?",
            spec.TargetMember,
            spec.ParamTypes,
            spec.MethodType,
            patchType,
            resolutionKind,
            patchClass.FullName,
            patchSite);
    }

    private static Patch BuildPatchFromBody(
        ResolvedTarget target, string resolutionKind, string patchType,
        IMethod patchMethod, ITypeDefinition patchClass)
    {
        var patchSite = patchMethod != null
            ? $"{patchClass.FullName}.{patchMethod.Name}"
            : patchClass.FullName;
        return new Patch(
            target.TargetType,
            target.MemberName,
            target.ParamTypes,
            null,
            patchType,
            resolutionKind,
            patchClass.FullName,
            patchSite);
    }

    /// <summary>
    /// Decide TypedAttribute vs StringTargeted by probing the named member on
    /// the target type — a target the patch project couldn't have referenced
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
    private static string ClassifyAttributeResolution(TargetSpec spec, ITypeDefinition patchClass)
    {
        if (spec.TargetType == null || spec.TargetMember == null) return "Attribute";

        var target = patchClass.Compilation.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.FullName == spec.TargetType);
        if (target == null) return "Attribute";

        var isPublic = target.Methods.Any(m =>
            m.Name == spec.TargetMember && m.Accessibility == Accessibility.Public);
        return isPublic ? "TypedAttribute" : "StringTargeted";
    }

    private sealed class TargetSpec
    {
        public string TargetType;
        public string TargetMember;
        public List<string> ParamTypes;
        public string MethodType;
    }

    private sealed record ResolvedTarget(
        string TargetType, string MemberName, List<string> ParamTypes);
}
