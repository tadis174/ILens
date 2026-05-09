using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileMethodTool
{
    [McpServerTool(Name = "decompile_method", ReadOnly = true),
     Description("Decompile a single method to C# source code. " +
        "Faster and more focused than decompile_type when you only need one method.")]
    public static string DecompileMethod(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'RimWorld.MainTabWindow_Research'.")] string typeName,
        [Description("Method name, e.g. 'ListProjects'.")] string methodName,
        [Description("Number of parameters, to disambiguate overloads.")] int? parameterCount = null)
    {
        var (host, resolver) = registry.GetOrLoad(assembly);
        var type = resolver.ResolveType(typeName);
        var (method, origin) = resolver.ResolveMethod(type, methodName, parameterCount);

        var header = origin.Kind == "declared"
            ? $"// {type.FullName}.{methodName}"
            : $"// {type.FullName}.{methodName} {origin.Format()}";

        var source = host.DecompileMethod(method);
        return $"{header}\n{source}";
    }
}
