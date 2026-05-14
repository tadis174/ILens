using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileTypeTool
{
    [McpServerTool(Name = "decompile_type", ReadOnly = true),
     Description("Decompile a type to full C# source code. " +
        "Use this to understand implementation details, method bodies, and internal logic.")]
    public static string DecompileType(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.String' or 'System.IO.File'.")] string typeName)
    {
        var host = registry.GetOrLoad(assembly);
        var type = host.Resolver.ResolveType(typeName);
        return host.DecompileType(type);
    }
}
