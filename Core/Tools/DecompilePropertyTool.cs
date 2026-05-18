using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompilePropertyTool
{
    [McpServerTool(Name = "decompile_property", ReadOnly = true),
     Description("Decompile a property by name and return the full C# property " +
        "declaration with its accessor bodies. Shorthand for the IL-prefixed path " +
        "through decompile_method (get_X, set_X) — use this when you have the " +
        "property name and want everything about it. For indexer overloads, use " +
        "decompile_method on get_Item / set_Item with parameterTypes; this tool " +
        "rejects ambiguous indexer names with the candidates listed.")]
    public static string DecompileProperty(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.IO.FileInfo'.")] string typeName,
        [Description("Property name, e.g. 'Length'.")] string propertyName)
    {
        var host = registry.GetOrLoad(assembly);
        var resolver = host.Resolver;
        var type = resolver.ResolveType(typeName);
        var (property, origin) = resolver.ResolveProperty(type, propertyName);

        var header = origin.Kind == "declared"
            ? $"// {type.FullName}.{propertyName} (property)"
            : $"// {type.FullName}.{propertyName} (property) {origin.Format()}";

        var source = host.DecompileProperty(property);
        return $"{header}\n{source}";
    }
}
