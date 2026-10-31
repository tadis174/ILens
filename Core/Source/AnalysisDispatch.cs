using ICSharpCode.ILSpyX.Analyzers;

namespace ILens;

/// <summary>
/// Single source of truth mapping <see cref="AnalysisKind"/> values to the ILSpyX
/// analyzer header they trigger, the words their answer is counted in, and the symbol
/// categories they accept.
/// </summary>
/// <remarks>
/// <see cref="HeaderFor"/> names the ILSpyX analyzer that produces the result, for the kinds
/// one answers; <see cref="AnalysisKind.CallSites"/> has none, since
/// <see cref="CallSiteReport"/> answers it, and neither have <see cref="AnalysisKind.ReadBy"/>
/// and <see cref="AnalysisKind.AssignedBy"/>, which <see cref="FieldAccessScanner"/> answers
/// for a field, nor <see cref="AnalysisKind.Implements"/>, which
/// <see cref="InterfaceImplementations"/> answers, since the ILSpyX analyzer looks for the
/// interfaces in the type system it is handed, and ILens's own holds those of the inspected
/// assembly alone, nor <see cref="AnalysisKind.ImplementedBy"/>, which the same class answers
/// for a member, since the ILSpyX analyzers list every member of the asked member's name and
/// parameters, whether or not the interface member is bound to it, nor
/// <see cref="AnalysisKind.Uses"/>, which <see cref="MethodUseScanner"/>
/// answers, since the ILSpyX analyzer resolves what a body names without the method's type
/// parameters, nor <see cref="AnalysisKind.UsedBy"/>: who uses a method is answered by
/// <see cref="AssemblyHost.FindMethodUsers"/>, since the ILSpyX analyzers count the users of
/// a member the method hides, and who uses a type by <see cref="TypeUsageScanner"/>, whether
/// or not the assembly defines the type, nor <see cref="AnalysisKind.OverriddenBy"/>, which
/// <see cref="AssemblyHost.FindOverrides"/> answers, since the ILSpyX analyzers know an
/// override by its flags alone and list it under a member it does not override, nor
/// <see cref="AnalysisKind.ExtensionMethods"/>, which
/// <see cref="AssemblyHost.FindExtensionMethods"/> answers, since the ILSpyX analyzer looks in
/// static classes alone and passes over a receiver passed by reference. Two families
/// are synthesized by <c>analyze</c> instead, because ILSpyX exports no analyzer for
/// them: <see cref="AnalysisKind.ImplementedBy"/> on a type, and every usage kind on a
/// property or event — those route to the accessors, and for a field-like event also to
/// the field holding its subscriber list. A type the assembly does not define is answered by
/// <see cref="TypeUsageScanner"/>, since no ILSpyX analyzer can match it, and on a member
/// of such a type the kinds that need the other assembly's code are refused. Otherwise the
/// table governs which pairs are <em>accepted</em>, and only the execution differs.
/// </remarks>
public static class AnalysisDispatch
{
    /// <param name="Counted">What the answer's header counts: "users" in "12 users of X:".</param>
    /// <param name="Relation">The words between the count and the subject: "of" in "12 users of X:".</param>
    /// <param name="AsksWhoUses">
    /// The kind asks which code uses the symbol, so a use found in compiler-generated code is
    /// a use by the member it was written in and is reported under that member.
    /// </param>
    /// <param name="ReadsCode">
    /// The answer is read from method bodies, which a reference assembly does not hold, so the
    /// kind is refused for one (<see cref="AssemblyHost.ReferenceAssemblyError"/>). The other
    /// kinds read declarations alone.
    /// </param>
    private sealed record Spec(
        string Header, Noun Counted, string Relation, IReadOnlySet<SymbolCategory> Applies, bool AsksWhoUses,
        bool ReadsCode);

    private static readonly IReadOnlyDictionary<AnalysisKind, Spec> _table =
        new Dictionary<AnalysisKind, Spec>
        {
            [AnalysisKind.UsedBy]           = new(null,                Noun.Regular("user"),             "of",             Set(SymbolCategory.Type, SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: true,  ReadsCode: true),
            [AnalysisKind.InstantiatedBy]   = new("Instantiated By",   Noun.Regular("symbol"),           "instantiating",  Set(SymbolCategory.Type), AsksWhoUses: true,  ReadsCode: true),
            [AnalysisKind.ExposedBy]        = new("Exposed By",        Noun.Regular("member"),           "exposing",       Set(SymbolCategory.Type), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.ExtensionMethods] = new(null,                Noun.Regular("extension method"), "for",            Set(SymbolCategory.Type), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.AppliedTo]        = new("Applied To",        Noun.Regular("symbol"),           "marked with",    Set(SymbolCategory.Type), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.OverriddenBy]     = new(null,                Noun.Regular("override"),         "of",             Set(SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.ImplementedBy]    = new(null,                Noun.Regular("implementation"),   "of",             Set(SymbolCategory.Type, SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.Uses]             = new(null,                Noun.Regular("symbol"),           "used by",        Set(SymbolCategory.Method, SymbolCategory.Property), AsksWhoUses: false, ReadsCode: true),
            [AnalysisKind.Implements]       = new(null,                Noun.Regular("interface member"), "implemented by", Set(SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: false, ReadsCode: false),
            [AnalysisKind.ReadBy]           = new(null,                Noun.Regular("reader"),           "of",             Set(SymbolCategory.Field, SymbolCategory.Property), AsksWhoUses: true,  ReadsCode: true),
            [AnalysisKind.AssignedBy]       = new(null,                Noun.Regular("writer"),           "of",             Set(SymbolCategory.Field, SymbolCategory.Property), AsksWhoUses: true,  ReadsCode: true),
            [AnalysisKind.CallSites]        = new(null,                Noun.Regular("call"),             "to",             Set(SymbolCategory.Method), AsksWhoUses: true,  ReadsCode: true),
        };

    private static readonly IReadOnlyDictionary<SymbolCategory, IReadOnlyList<AnalysisKind>>
        _kindsByCategory = Enum.GetValues<SymbolCategory>().ToDictionary(
            cat => cat,
            cat => (IReadOnlyList<AnalysisKind>)_table
                .Where(kv => kv.Value.Applies.Contains(cat))
                .Select(kv => kv.Key)
                .ToList());

    /// <summary>The ILSpyX analyzer header for an analysis kind, or null when no analyzer answers it.</summary>
    public static string HeaderFor(AnalysisKind kind) => _table[kind].Header;

    /// <summary>
    /// The "Applied To" analyzer, which finds the attributes whose constructor belongs to the
    /// type asked about.
    /// </summary>
    private const string AppliedToAnalyzer = "AttributeAppliedToAnalyzer";

    /// <summary>The analyzers <see cref="AnalyzerFor"/> names, each with the kind it answers.</summary>
    private static readonly IReadOnlyList<(AnalysisKind Kind, string Analyzer)> _namedAnalyzers =
    [
        (AnalysisKind.AppliedTo, AppliedToAnalyzer),
    ];

    /// <summary>
    /// The type name of the analyzer that answers <paramref name="kind"/>, or null when the
    /// header's analyzer that shows the symbol asked about does.
    /// </summary>
    /// <remarks>
    /// ILSpyX shows "Applied To" only for a type whose base classes reach
    /// <c>System.Attribute</c> in the type system the symbol comes from. ILens's own holds no
    /// referenced assembly, so there the walk ends at a base class another assembly defines and
    /// the analyzer would be passed over. It is named instead, and <c>analyze</c> checks for an
    /// attribute itself, through <see cref="AssemblyHost.DerivesFromAttribute"/>.
    /// </remarks>
    public static string AnalyzerFor(AnalysisKind kind) => kind switch
    {
        AnalysisKind.AppliedTo => AppliedToAnalyzer,
        _ => null,
    };

    /// <summary>What the header of an answer to <paramref name="kind"/> counts.</summary>
    public static Noun CountedAs(AnalysisKind kind) => _table[kind].Counted;

    /// <summary>
    /// What the header of an answer to <paramref name="kind"/> says after the count:
    /// "of X.Y()" in "12 users of X.Y():".
    /// </summary>
    public static string About(AnalysisKind kind, string subject) => $"{_table[kind].Relation} {subject}";

    /// <summary>The set of symbol categories an analysis kind accepts.</summary>
    public static IReadOnlySet<SymbolCategory> AppliesTo(AnalysisKind kind) => _table[kind].Applies;

    /// <summary>
    /// True if the kind asks which code uses a symbol, so its results are attributed to the
    /// member each use was written in (see <see cref="AssemblyHost.AttributeToSource"/>).
    /// </summary>
    public static bool AsksWhoUses(AnalysisKind kind) => _table[kind].AsksWhoUses;

    /// <summary>True if the answer to the kind is read from method bodies, which a reference assembly does not hold.</summary>
    public static bool ReadsCode(AnalysisKind kind) => _table[kind].ReadsCode;

    /// <summary>The analysis kinds whose answer is read from declarations alone, in declaration order.</summary>
    public static IReadOnlyList<AnalysisKind> ReadingDeclarations() =>
        Enum.GetValues<AnalysisKind>().Where(kind => !ReadsCode(kind)).ToList();

    /// <summary>Analysis kinds that accept a given symbol category, in declaration order.</summary>
    public static IReadOnlyList<AnalysisKind> KindsFor(SymbolCategory category) =>
        _kindsByCategory[category];

    /// <summary>
    /// Verify every <see cref="AnalysisKind"/> resolves to an analyzer ILSpyX actually
    /// exports, and every analyzer <see cref="AnalyzerFor"/> names exists under its header.
    /// Catches silent drift if the upstream library renames a header or an analyzer.
    /// Throws <see cref="InvalidOperationException"/> on mismatch — meant for startup.
    /// </summary>
    public static void SelfCheck()
    {
        var analyzers = ExportAnalyzerAttribute.GetAnnotatedAnalyzers().ToList();
        var exported = analyzers
            .Select(a => a.AttributeData.Header)
            .ToHashSet(StringComparer.Ordinal);

        var missing = _table
            .Where(kv => kv.Value.Header != null && !exported.Contains(kv.Value.Header))
            .Select(kv => $"{kv.Key} -> '{kv.Value.Header}'")
            .ToList();
        foreach (var (kind, analyzer) in _namedAnalyzers)
        {
            var header = HeaderFor(kind);
            if (!analyzers.Any(a => a.AttributeData.Header == header && a.AnalyzerType.Name == analyzer))
                missing.Add($"{kind} -> '{header}' analyzer {analyzer}");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "AnalysisKind/ILSpyX header mismatch — these analyzers are no longer exported: "
                + string.Join(", ", missing) + ". ILSpyX currently exports: "
                + string.Join(", ", exported.OrderBy(h => h)) + ".");
        }
    }

    private static IReadOnlySet<SymbolCategory> Set(params SymbolCategory[] values) =>
        new HashSet<SymbolCategory>(values);
}
