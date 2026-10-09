using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindTypesTool
{
    [McpServerTool(Name = "find_types", ReadOnly = true),
     Description("Find the types the assembly defines; with no filter, every type. " +
        "One fully qualified name per line, sorted, in the form type_name takes.")]
    public static string FindTypes(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.Namespace)] string @namespace = null,
        [Description(ParameterDescriptions.NameContains)] string name_contains = null,
        [Description(ParameterDescriptions.Accessibility)] AccessibilityFilter accessibility = AccessibilityFilter.PublicProtected,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;

        // IsNullOrEmpty rather than null, so an LLM passing "" for "omit" gets no filter.
        var byNamespace = !string.IsNullOrEmpty(@namespace);
        if (byNamespace)
            host.Resolver.RequireNamespace(@namespace);

        var types = host.TypeSystem.MainModule.TypeDefinitions
            .Where(t => !byNamespace || t.Namespace == @namespace)
            .Where(t => string.IsNullOrEmpty(name_contains)
                || t.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase))
            .Where(t => accessibility.Permits(t.Accessibility))
            .Where(t => include_compiler_generated || !CompilerGeneratedFilter.IsCompilerGenerated(t))
            .Select(ReferenceFormatter.FormatTypeName)
            .Order(StringComparer.Ordinal)
            .ToList();

        return ListOutput.Format(types, limit, Noun.Regular("type"));
    }
}
