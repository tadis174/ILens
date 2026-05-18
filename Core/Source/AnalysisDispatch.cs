using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX.Analyzers;

namespace ILens;

/// <summary>
/// Single source of truth mapping <see cref="AnalysisKind"/> values to the ILSpyX
/// analyzer header they trigger and the symbol categories they accept.
/// </summary>
public static class AnalysisDispatch
{
    private sealed record Spec(string Header, IReadOnlySet<SymbolCategory> Applies);

    private static readonly IReadOnlyDictionary<AnalysisKind, Spec> _table =
        new Dictionary<AnalysisKind, Spec>
        {
            [AnalysisKind.UsedBy]           = new("Used By",           Set(SymbolCategory.Type, SymbolCategory.Method)),
            [AnalysisKind.InstantiatedBy]   = new("Instantiated By",   Set(SymbolCategory.Type)),
            [AnalysisKind.ExposedBy]        = new("Exposed By",        Set(SymbolCategory.Type)),
            [AnalysisKind.ExtensionMethods] = new("Extension Methods", Set(SymbolCategory.Type)),
            [AnalysisKind.AppliedTo]        = new("Applied To",        Set(SymbolCategory.Type)),
            [AnalysisKind.OverriddenBy]     = new("Overridden By",     Set(SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event)),
            [AnalysisKind.ImplementedBy]    = new("Implemented By",    Set(SymbolCategory.Type, SymbolCategory.Method, SymbolCategory.Property, SymbolCategory.Event)),
            [AnalysisKind.Uses]             = new("Uses",              Set(SymbolCategory.Method)),
            [AnalysisKind.Implements]       = new("Implements",        Set(SymbolCategory.Method)),
            [AnalysisKind.ReadBy]           = new("Read By",           Set(SymbolCategory.Field)),
            [AnalysisKind.AssignedBy]       = new("Assigned By",       Set(SymbolCategory.Field)),
        };

    private static readonly IReadOnlyDictionary<SymbolCategory, IReadOnlyList<AnalysisKind>>
        _kindsByCategory = Enum.GetValues<SymbolCategory>().ToDictionary(
            cat => cat,
            cat => (IReadOnlyList<AnalysisKind>)_table
                .Where(kv => kv.Value.Applies.Contains(cat))
                .Select(kv => kv.Key)
                .ToList());

    /// <summary>The ILSpyX analyzer header for an analysis kind.</summary>
    public static string HeaderFor(AnalysisKind kind) => _table[kind].Header;

    /// <summary>The set of symbol categories an analysis kind accepts.</summary>
    public static IReadOnlySet<SymbolCategory> AppliesTo(AnalysisKind kind) => _table[kind].Applies;

    /// <summary>Analysis kinds that accept a given symbol category, in declaration order.</summary>
    public static IReadOnlyList<AnalysisKind> KindsFor(SymbolCategory category) =>
        _kindsByCategory[category];

    /// <summary>
    /// Verify every <see cref="AnalysisKind"/> resolves to an analyzer ILSpyX actually
    /// exports. Catches silent drift if the upstream library renames a header.
    /// Throws <see cref="InvalidOperationException"/> on mismatch — meant for startup.
    /// </summary>
    public static void SelfCheck()
    {
        var exported = ExportAnalyzerAttribute.GetAnnotatedAnalyzers()
            .Select(a => a.AttributeData.Header)
            .ToHashSet(StringComparer.Ordinal);

        var missing = _table
            .Where(kv => !exported.Contains(kv.Value.Header))
            .Select(kv => $"{kv.Key} -> '{kv.Value.Header}'")
            .ToList();

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
