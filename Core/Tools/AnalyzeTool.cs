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
        "Valid kinds by symbol category:\n" +
        "  Type:     UsedBy, InstantiatedBy, ExposedBy, ExtensionMethods, AppliedTo\n" +
        "  Method:   UsedBy, OverriddenBy, ImplementedBy, Uses, Implements\n" +
        "  Property: OverriddenBy, ImplementedBy\n" +
        "  Field:    ReadBy, AssignedBy\n" +
        "  Event:    OverriddenBy, ImplementedBy\n" +
        "AppliedTo requires an Attribute-derived type — it errors on a non-attribute type.")]
    public static string Analyze(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.String' or 'System.IO.File'.")] string typeName,
        [Description("Analysis kind. See tool description for which kinds apply to which symbol category.")] AnalysisKind kind,
        [Description("Member name. Omit to analyze the type itself.")] string memberName = null,
        [Description("Method overload disambiguator (only relevant when memberName resolves to a method). If parameterTypes is also given, the two must agree.")] int? parameterCount = null,
        [Description("Ordered parameter-type patterns to disambiguate same-arity method overloads (only relevant when memberName resolves to a method). Same loose matching as find_methods.")] string[] parameterTypes = null,
        [Description("Cap on result lines. Default 50.")] int? limit = null)
    {
        var host = registry.GetOrLoad(assembly);
        var resolver = host.Resolver;
        var type = resolver.ResolveType(typeName);

        // Default to type-level analysis; member resolution overrides if memberName is set.
        // IsNullOrEmpty (not just `is null`) so an LLM passing "" for "omit" works as expected.
        ISymbol symbol = type;
        var category = SymbolCategory.Type;
        var originSuffix = "";
        if (!string.IsNullOrEmpty(memberName))
        {
            var (resolved, origin, resolvedCategory) =
                resolver.ResolveMember(type, memberName, parameterCount, parameterTypes);
            symbol = resolved;
            category = resolvedCategory;
            originSuffix = " " + origin.Format();
        }

        ValidateKind(kind, category);

        // AppliedTo asks "what is this attribute applied to?" — only meaningful for an
        // Attribute-derived type. ValidateKind checks only the symbol category (Type),
        // not attribute-ness; without this check a non-attribute type would yield an
        // empty result indistinguishable from "applied nowhere". This guard is specific
        // to AppliedTo: its name presupposes the input kind and the precondition is a
        // crisp base-type check, unlike the murkier preconditions of the other kinds.
        if (kind == AnalysisKind.AppliedTo && !IsAttribute(type))
            throw new ArgumentException(
                $"Analysis kind 'AppliedTo' requires an attribute type; " +
                $"{type.FullName} does not derive from System.Attribute.");

        var header = AnalysisDispatch.HeaderFor(kind);
        var results = host.RunAnalyzer(header, symbol);
        var display = ReferenceFormatter.FormatSymbol(symbol);

        return $"{header} — {display}{originSuffix}:\n" +
            ReferenceFormatter.FormatSymbolList(results, limit ?? DefaultLimit);
    }

    private static void ValidateKind(AnalysisKind kind, SymbolCategory category)
    {
        if (AnalysisDispatch.AppliesTo(kind).Contains(category))
            return;

        var valid = string.Join(", ", AnalysisDispatch.KindsFor(category));
        throw new ArgumentException(
            $"Analysis kind '{kind}' is not valid for {category}. " +
            $"Valid kinds for {category}: {valid}.");
    }

    /// <summary>
    /// True if <paramref name="type"/> is <c>System.Attribute</c> or derives from it.
    /// <c>GetAllBaseTypeDefinitions</c> includes the type itself plus every supertype.
    /// </summary>
    private static bool IsAttribute(ITypeDefinition type) =>
        type.GetAllBaseTypeDefinitions().Any(t => t.FullName == "System.Attribute");
}
