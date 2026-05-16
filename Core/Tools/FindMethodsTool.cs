using System.ComponentModel;
using System.Text;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindMethodsTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "find_methods", ReadOnly = true),
     Description("Search the assembly for methods matching a signature. " +
        "Combine any of: name pattern, return type, parameter types, parameter count, " +
        "declaring namespace, declaring-type pattern, accessibility.\n" +
        "Type patterns match by short name OR full name (e.g., 'String' or 'System.String'). " +
        "Generics are erased at the top level — 'List' matches List<T> for any T. " +
        "Nullable<T> is unwrapped — 'int' matches both 'int' and 'int?'. " +
        "Arrays are matched with '[]' suffix (e.g., 'int[]', 'String[]'). " +
        "Matching is exact (no inheritance walk); use analyze with UsedBy/Uses for assignability-aware exploration. " +
        "Constructors and property/event accessors are excluded; operator methods (op_*) are included. " +
        "To decompile an accessor body, call decompile_method with the accessor's IL name (get_X, set_X, add_X, remove_X). " +
        "Results are sorted by declaring type, then method name; capped at limit (default 50).")]
    public static string FindMethods(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Optional case-insensitive substring filter on method name.")] string namePattern = null,
        [Description("Optional return-type pattern (e.g., 'bool', 'IEnumerable', 'String').")] string returns = null,
        [Description("Optional ordered list of parameter-type patterns. The method must have exactly this many parameters, each matching the pattern at its position.")] string[] parameterTypes = null,
        [Description("Optional exact parameter count. If parameterTypes is also set, the two must agree.")] int? parameterCount = null,
        [Description("Optional exact declaring namespace, e.g. 'System.IO' or 'System.Collections.Generic'.")] string declaringNamespace = null,
        [Description("Optional case-insensitive substring filter on declaring type's short name.")] string declaringTypePattern = null,
        [Description("Accessibility filter. Default: PublicProtected.")] AccessibilityFilter accessibility = AccessibilityFilter.PublicProtected,
        [Description("Cap on result lines. Default 50.")] int? limit = null)
    {
        if (parameterTypes != null && parameterCount.HasValue
            && parameterTypes.Length != parameterCount.Value)
        {
            throw new ArgumentException(
                $"parameterCount ({parameterCount.Value}) contradicts " +
                $"parameterTypes.Length ({parameterTypes.Length}).");
        }

        var host = registry.GetOrLoad(assembly);
        var matches = ScanAssembly(host, namePattern, returns, parameterTypes,
            parameterCount, declaringNamespace, declaringTypePattern, accessibility);

        matches.Sort((a, b) =>
        {
            var c = string.Compare(a.DeclaringType.FullName, b.DeclaringType.FullName,
                StringComparison.Ordinal);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return FormatResults(matches, limit ?? DefaultLimit);
    }

    private static List<IMethod> ScanAssembly(
        AssemblyHost host,
        string namePattern,
        string returns,
        string[] parameterTypes,
        int? parameterCount,
        string declaringNamespace,
        string declaringTypePattern,
        AccessibilityFilter accessibility)
    {
        var matches = new List<IMethod>();

        foreach (var typeDef in host.TypeSystem.MainModule.TypeDefinitions)
        {
            if (declaringNamespace != null && typeDef.Namespace != declaringNamespace)
                continue;
            if (!string.IsNullOrEmpty(declaringTypePattern) &&
                !typeDef.Name.Contains(declaringTypePattern, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var method in typeDef.Methods)
            {
                if (method.IsConstructor || method.IsAccessor)
                    continue;
                if (!accessibility.Permits(method.Accessibility))
                    continue;
                if (!string.IsNullOrEmpty(namePattern) &&
                    !method.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (parameterCount.HasValue && method.Parameters.Count != parameterCount.Value)
                    continue;

                if (parameterTypes != null)
                {
                    if (method.Parameters.Count != parameterTypes.Length) continue;
                    var paramsOk = true;
                    for (int i = 0; i < parameterTypes.Length; i++)
                    {
                        if (!TypeMatcher.Matches(method.Parameters[i].Type, parameterTypes[i]))
                        {
                            paramsOk = false;
                            break;
                        }
                    }
                    if (!paramsOk) continue;
                }

                if (!string.IsNullOrEmpty(returns) && !TypeMatcher.Matches(method.ReturnType, returns))
                    continue;

                matches.Add(method);
            }
        }

        return matches;
    }

    private static string FormatResults(List<IMethod> matches, int limit)
    {
        if (matches.Count == 0)
            return "(no methods match)";

        var emitting = Math.Min(matches.Count, limit);
        var sb = new StringBuilder();
        sb.Append($"{matches.Count} match{(matches.Count == 1 ? "" : "es")}");
        if (matches.Count > limit) sb.Append($" (showing {limit})");
        sb.Append(":\n");

        for (int i = 0; i < emitting; i++)
        {
            var m = matches[i];
            var parameters = string.Join(", ", m.Parameters.Select(SignatureFormatter.FormatParameter));
            var modifier = m.IsStatic ? "static " : "";
            sb.Append($"  {modifier}{m.DeclaringType.FullName}.{m.Name}" +
                      $"({parameters}) → {ReferenceFormatter.FormatTypeRef(m.ReturnType)}\n");
        }

        if (matches.Count > limit)
            sb.Append($"... ({matches.Count - limit} more matches; raise limit to see all)");

        return sb.ToString().TrimEnd();
    }
}
