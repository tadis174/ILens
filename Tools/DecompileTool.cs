using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileTool
{
    [McpServerTool(Name = "decompile_type", ReadOnly = true),
     Description("Decompile a type to full C# source code. " +
        "Use this to understand implementation details, method bodies, and internal logic.")]
    public static string Decompile(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'RimWorld.PlantUtility' or 'Verse.Thing'.")] string typeName)
    {
        var (host, resolver) = registry.GetOrLoad(assembly);
        var type = resolver.ResolveType(typeName);
        return host.DecompileType(type);
    }
}
