using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class CompareMethodTool
{
    [McpServerTool(Name = "compare_method", ReadOnly = true),
     Description("Compare a single method across two assemblies and emit each body labeled by source. " +
        "Format is 'csharp' (decompiled C#, default) or 'il' (normalized IL disassembly — the same form " +
        "list_changed_types and compare_type use to decide body equality). When the two bodies are " +
        "identical the tool emits a single copy with a note instead of duplicating the text.")]
    public static string CompareMethod(
        AssemblyHostRegistry registry,
        [Description("Path to the first assembly (must be under an allowed root).")] string assemblyA,
        [Description("Path to the second assembly (must be under an allowed root).")] string assemblyB,
        [Description("Fully qualified type name, e.g. 'System.IO.File'.")] string typeName,
        [Description("Method name, e.g. 'ReadAllText'.")] string methodName,
        [Description("Body format: 'csharp' (default) or 'il'.")] string format = "csharp",
        [Description("Number of parameters, to disambiguate overloads. If parameterTypes is also given, the two must agree.")] int? parameterCount = null,
        [Description("Ordered parameter-type patterns to disambiguate same-arity overloads (loose match, like find_methods).")] string[] parameterTypes = null)
    {
        var hostA = registry.GetOrLoad(assemblyA);
        var hostB = registry.GetOrLoad(assemblyB);
        var comparer = new AssemblyComparer(hostA, hostB);
        var (aMethod, bMethod) =
            comparer.ResolveMethodPair(typeName, methodName, parameterCount, parameterTypes);

        string aBody, bBody;
        switch ((format ?? "csharp").ToLowerInvariant())
        {
            case "il":
                aBody = hostA.DisassembleMethodBody(aMethod) ?? "(no body)";
                bBody = hostB.DisassembleMethodBody(bMethod) ?? "(no body)";
                break;
            case "csharp":
                aBody = hostA.DecompileMethod(aMethod);
                bBody = hostB.DecompileMethod(bMethod);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown format '{format}'. Use 'csharp' or 'il'.");
        }

        if (aBody == bBody)
            return $"// {typeName}.{methodName} — bodies are identical in both assemblies.\n\n{aBody}";

        return $"// === A: {hostA.AssemblyPath} ===\n{aBody}\n\n" +
            $"// === B: {hostB.AssemblyPath} ===\n{bBody}";
    }
}
