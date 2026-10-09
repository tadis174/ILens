using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX.Analyzers;

namespace ILens;

/// <summary>
/// Single source of truth mapping <see cref="AnalysisKind"/> values to the ILSpyX
/// analyzer header they trigger, the words their answer is counted in, and the symbol
/// categories they accept.
/// </summary>
/// <remarks>
/// <see cref="HeaderFor"/> names the ILSpyX analyzer that produces the result for most
/// (kind, category) pairs; <see cref="AnalysisKind.CallSites"/> has none, since
/// <see cref="CallSiteReport"/> answers it, and neither have <see cref="AnalysisKind.ReadBy"/>
/// and <see cref="AnalysisKind.AssignedBy"/>, which <see cref="FieldAccessScanner"/> answers
/// for a field. Two families
/// are synthesized by <c>analyze</c> instead, because ILSpyX exports no analyzer for
/// them: <see cref="AnalysisKind.ImplementedBy"/> on a type, and every usage kind on a
/// property or event — those route to the accessors, and for a field-like event also to
/// the field holding its subscriber list. A type the assembly does not define is answered by
/// <see cref="ReferencedTypeScanner"/>, since no ILSpyX analyzer can match it, and on a member
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
    private sealed record Spec(
        string Header, Noun Counted, string Relation, IReadOnlySet<SymbolCategory> Applies, bool AsksWhoUses);

    private static readonly IReadOnlyDictionary<AnalysisKind, Spec> _table =
        new Dictionary<AnalysisKind, Spec>
        {
            [AnalysisKind.UsedBy]           = new("Used By",           Noun.Regular("user"),             "of",             Set(SymbolCategory.Type, SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: true),
            [AnalysisKind.InstantiatedBy]   = new("Instantiated By",   Noun.Regular("symbol"),           "instantiating",  Set(SymbolCategory.Type), AsksWhoUses: true),
            [AnalysisKind.ExposedBy]        = new("Exposed By",        Noun.Regular("member"),           "exposing",       Set(SymbolCategory.Type), AsksWhoUses: false),
            [AnalysisKind.ExtensionMethods] = new("Extension Methods", Noun.Regular("extension method"), "for",            Set(SymbolCategory.Type), AsksWhoUses: false),
            [AnalysisKind.AppliedTo]        = new("Applied To",        Noun.Regular("symbol"),           "marked with",    Set(SymbolCategory.Type), AsksWhoUses: false),
            [AnalysisKind.OverriddenBy]     = new("Overridden By",     Noun.Regular("override"),         "of",             Set(SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: false),
            [AnalysisKind.ImplementedBy]    = new("Implemented By",    Noun.Regular("implementation"),   "of",             Set(SymbolCategory.Type, SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event), AsksWhoUses: false),
            [AnalysisKind.Uses]             = new("Uses",              Noun.Regular("symbol"),           "used by",        Set(SymbolCategory.Method, SymbolCategory.Property), AsksWhoUses: false),
            [AnalysisKind.Implements]       = new("Implements",        Noun.Regular("interface member"), "implemented by", Set(SymbolCategory.Method), AsksWhoUses: false),
            [AnalysisKind.ReadBy]           = new(null,                Noun.Regular("reader"),           "of",             Set(SymbolCategory.Field, SymbolCategory.Property), AsksWhoUses: true),
            [AnalysisKind.AssignedBy]       = new(null,                Noun.Regular("writer"),           "of",             Set(SymbolCategory.Field, SymbolCategory.Property), AsksWhoUses: true),
            [AnalysisKind.CallSites]        = new(null,                Noun.Regular("call"),             "to",             Set(SymbolCategory.Method), AsksWhoUses: true),
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
    /// The "Used By" analyzer ILSpyX applies to a virtual method, which matches a method by its
    /// own metadata token alone.
    /// </summary>
    private const string TokenUsedByAnalyzer = "MethodVirtualUsedByAnalyzer";

    /// <summary>
    /// The type name of the analyzer that answers <paramref name="kind"/> about
    /// <paramref name="symbol"/>, or null when the header's analyzer that shows the symbol does.
    /// </summary>
    /// <remarks>
    /// A call to an override names the declaration that introduced its slot, except a call
    /// through <c>base.</c>, which names the override itself. The analyzer ILSpyX applies to an
    /// override also counts every virtual call of the declaration it overrides directly, so it
    /// would answer an override one level below the slot's declaration with every call to that
    /// declaration, on any receiver, and an override further down with none. An override
    /// therefore goes to the analyzer that matches its own token, which answers alike at every
    /// depth, and the answer names the declaration code calls instead
    /// (<see cref="ReferenceFormatter.FormatOverrideRoute"/>).
    /// </remarks>
    public static string AnalyzerFor(AnalysisKind kind, ISymbol symbol) =>
        kind == AnalysisKind.UsedBy && symbol is IMethod { IsOverride: true } ? TokenUsedByAnalyzer : null;

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
        var usedBy = HeaderFor(AnalysisKind.UsedBy);
        if (!analyzers.Any(a => a.AttributeData.Header == usedBy && a.AnalyzerType.Name == TokenUsedByAnalyzer))
            missing.Add($"{AnalysisKind.UsedBy} -> '{usedBy}' analyzer {TokenUsedByAnalyzer}");

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
