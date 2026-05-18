using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileEventTool
{
    [McpServerTool(Name = "decompile_event", ReadOnly = true),
     Description("Decompile an event by name and return the full C# event " +
        "declaration with add/remove accessor bodies. Shorthand for the IL-prefixed " +
        "path through decompile_method (add_X, remove_X) — use this when you have " +
        "the event name and want everything about it.")]
    public static string DecompileEvent(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.IO.FileSystemWatcher'.")] string typeName,
        [Description("Event name, e.g. 'Changed'.")] string eventName)
    {
        var host = registry.GetOrLoad(assembly);
        var resolver = host.Resolver;
        var type = resolver.ResolveType(typeName);
        var (@event, origin) = resolver.ResolveEvent(type, eventName);

        var header = origin.Kind == "declared"
            ? $"// {type.FullName}.{eventName} (event)"
            : $"// {type.FullName}.{eventName} (event) {origin.Format()}";

        var source = host.DecompileEvent(@event);
        return $"{header}\n{source}";
    }
}
