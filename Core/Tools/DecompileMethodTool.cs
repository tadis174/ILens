using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileMethodTool
{
    [McpServerTool(Name = "decompile_method", ReadOnly = true),
     Description("Decompile a single method to C# source code. " +
        "Faster and more focused than decompile_type when you only need one method. " +
        "Property and event accessors are also reachable by their IL name (get_X, set_X, add_X, remove_X) " +
        "even though find_methods hides them from generic browsing — for the whole property or event use " +
        "decompile_property / decompile_event instead, which take the unprefixed name.")]
    public static string DecompileMethod(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.IO.File'.")] string typeName,
        [Description("Method name, e.g. 'ReadAllText'.")] string methodName,
        [Description("Number of parameters, to disambiguate overloads. If parameterTypes is also given, the two must agree.")] int? parameterCount = null,
        [Description("Ordered parameter-type patterns to disambiguate same-arity overloads, e.g. ['string'] or ['int','bool']. Same loose matching as find_methods: short, full, or C# keyword name (e.g. 'bool', 'int', 'string'), generics erased, '[]' for arrays.")] string[] parameterTypes = null)
    {
        var host = registry.GetOrLoad(assembly);
        var resolver = host.Resolver;
        var type = resolver.ResolveType(typeName);
        var (method, origin) = resolver.ResolveMethod(type, methodName, parameterCount, parameterTypes);

        var header = origin.Kind == "declared"
            ? $"// {type.FullName}.{methodName}"
            : $"// {type.FullName}.{methodName} {origin.Format()}";

        var source = host.DecompileMethod(method);
        return $"{header}\n{source}";
    }
}
