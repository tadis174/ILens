using System.ComponentModel;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class AnalyzeTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "analyze", ReadOnly = true),
     Description("Run cross-reference analysis on a type or member. " +
        "Omit memberName to analyze the type itself.\n" +
        "Valid kinds by symbol type:\n" +
        "  Type:     UsedBy, InstantiatedBy, ExposedBy, ExtensionMethods, AppliedTo\n" +
        "  Method:   UsedBy, OverriddenBy, ImplementedBy, Uses, Implements\n" +
        "  Property: OverriddenBy, ImplementedBy\n" +
        "  Field:    ReadBy, AssignedBy\n" +
        "  Event:    OverriddenBy, ImplementedBy\n" +
        "AppliedTo is meaningful only on Attribute-derived types — non-attributes return empty.")]
    public static string Analyze(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'RimWorld.PlantProperties'.")] string typeName,
        [Description("Analysis kind. See tool description for which kinds apply to which symbol type.")] AnalysisKind kind,
        [Description("Member name. Omit to analyze the type itself.")] string memberName = null,
        [Description("Method overload disambiguator (only relevant when memberName resolves to a method).")] int? parameterCount = null,
        [Description("Cap on result lines. Default 50.")] int? limit = null)
    {
        var (host, resolver) = registry.GetOrLoad(assembly);
        var type = resolver.ResolveType(typeName);

        // Default to type-level analysis; member resolution overrides if memberName is set.
        // IsNullOrEmpty (not just `is null`) so an LLM passing "" for "omit" works as expected.
        ISymbol symbol = type;
        var category = SymbolCategory.Type;
        var originSuffix = "";
        if (!string.IsNullOrEmpty(memberName))
        {
            var (resolved, origin, resolvedCategory) =
                resolver.ResolveMember(type, memberName, parameterCount);
            symbol = resolved;
            category = resolvedCategory;
            originSuffix = " " + origin.Format();
        }

        ValidateKind(kind, category);

        var header = AnalysisDispatch.HeaderFor(kind);
        var results = host.RunAnalyzer(header, symbol);
        var display = Formatter.FormatSymbol(symbol);

        return $"{header} — {display}{originSuffix}:\n" +
            Formatter.FormatSymbolList(results, limit ?? DefaultLimit);
    }

    private static void ValidateKind(AnalysisKind kind, SymbolCategory category)
    {
        if (AnalysisDispatch.AppliesTo(kind).Contains(category))
            return;

        var valid = string.Join(", ", AnalysisDispatch.KindsFor(category));
        throw new ArgumentException(
            $"Analysis kind '{kind}' is not valid for {category}. " +
            $"Valid kinds for {category}: {valid}");
    }
}
