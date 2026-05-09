using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class SearchTypesTool
{
    private const int MaxResults = 50;

    [McpServerTool(Name = "search_types", ReadOnly = true),
     Description("Search for types by name pattern. Matching is a case-insensitive substring " +
        "applied to each type's short name (the namespace is not part of the match). " +
        "Results are returned as fully qualified names so they can be fed into other tools verbatim. " +
        "Returns up to 50 matches.")]
    public static string SearchTypes(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Search pattern matched as a case-insensitive substring against each type's short name. " +
            "E.g. 'Plant' finds PlantProperties, PlantUtility, etc.")] string pattern)
    {
        var (host, _) = registry.GetOrLoad(assembly);
        var matches = host.TypeSystem.MainModule.TypeDefinitions
            .Where(t => t.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName)
            .Take(MaxResults + 1)
            .ToList();

        if (matches.Count == 0)
            return $"No types matching '{pattern}'";

        var truncated = matches.Count > MaxResults;
        var results = matches.Take(MaxResults).Select(t => t.FullName);
        var suffix = truncated ? $"\n... (truncated, more than {MaxResults} matches)" : "";

        return $"{Math.Min(matches.Count, MaxResults)} matches:\n{string.Join("\n", results)}{suffix}";
    }
}
