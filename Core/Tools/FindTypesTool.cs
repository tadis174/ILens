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

        // The question's own filters come first, so that what the two default filters leave
        // out of its matches can be counted.
        var leftOut = new LeftOut();
        var types = new List<string>();
        foreach (var t in host.TypeSystem.MainModule.TypeDefinitions)
        {
            if (byNamespace && t.Namespace != @namespace)
                continue;
            if (!string.IsNullOrEmpty(name_contains)
                && !t.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase))
                continue;
            if (leftOut.Admits(accessibility.Permits(t.Accessibility),
                    !include_compiler_generated && CompilerGeneratedFilter.IsCompilerGenerated(t)))
                types.Add(ReferenceFormatter.FormatTypeName(t));
        }
        types.Sort(StringComparer.Ordinal);

        return ListOutput.Format(types, limit, Noun.Regular("type"),
            notShown: ListOutput.NotShown(leftOut, accessibility, types.Count == 0));
    }
}
