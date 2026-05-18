using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class ListTypesTool
{
    [McpServerTool(Name = "list_types", ReadOnly = true),
     Description("List all types in a namespace. Returns fully qualified type names.")]
    public static string ListTypes(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Namespace to list types from, e.g. 'System.IO' or 'System.Collections.Generic'.")] string namespaceName)
    {
        var host = registry.GetOrLoad(assembly);
        var types = host.TypeSystem.MainModule.TypeDefinitions
            .Where(t => t.Namespace == namespaceName)
            .OrderBy(t => t.Name)
            .Select(t => t.FullName)
            .ToList();

        if (types.Count == 0)
            return $"No types in namespace '{namespaceName}'";

        return $"{types.Count} types:\n{string.Join("\n", types)}";
    }
}
