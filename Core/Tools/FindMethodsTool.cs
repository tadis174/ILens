using System.ComponentModel;
using System.Text;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindMethodsTool
{
    [McpServerTool(Name = "find_methods", ReadOnly = true),
     Description("Find methods by signature across the assembly, or list one type's methods with type_name. " +
        "One line per method, sorted: static when it is, then Type.Method(parameter types) → return type, " +
        "the form decompile and analyze take. Constructors are named .ctor and operators op_*. Property and " +
        "event accessors are left out; decompile and analyze reach one by its IL name, such as get_Count.")]
    public static string FindMethods(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.NameContains)] string name_contains = null,
        [Description($"Return-type pattern, matched by the rules of {nameof(parameter_types)}.")] string return_type = null,
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null,
        [Description(ParameterDescriptions.ParameterCount)] int? parameter_count = null,
        [Description(ParameterDescriptions.Namespace)] string @namespace = null,
        [Description(ParameterDescriptions.TypeName)] string type_name = null,
        [Description("Case-insensitive substring of the declaring type's name, without namespace; cannot be combined with type_name.")] string type_name_contains = null,
        [Description(ParameterDescriptions.Accessibility)] AccessibilityFilter accessibility = AccessibilityFilter.PublicProtected,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        SymbolResolver.ValidateDisambiguationArgs(parameter_count, parameter_types);

        if (!string.IsNullOrEmpty(type_name) && !string.IsNullOrEmpty(type_name_contains))
            throw new ArgumentException(
                $"{nameof(type_name)} and {nameof(type_name_contains)} are mutually exclusive — " +
                $"{nameof(type_name)} is an exact fully qualified name, {nameof(type_name_contains)} is a " +
                "substring of the short name. Pass whichever one you mean.");

        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;

        // Resolve up front so a name that matches no type or namespace is an error, not an
        // empty result. An exact name asserts the type or namespace exists, so answering a
        // misspelling with "No methods" would read as "there are no such methods" — a wrong
        // answer wearing the shape of a right one. A substring matching nothing is a
        // legitimate empty result, so type_name_contains gets no such check.
        var resolvedDeclaringType = string.IsNullOrEmpty(type_name)
            ? null
            : host.Resolver.ResolveType(type_name);
        if (!string.IsNullOrEmpty(@namespace))
            host.Resolver.RequireNamespace(@namespace);

        var matches = ScanAssembly(host, name_contains, return_type, parameter_types,
            parameter_count, @namespace, resolvedDeclaringType, type_name_contains,
            accessibility, include_compiler_generated);

        return FormatResults(matches, limit);
    }

    private static List<IMethod> ScanAssembly(
        AssemblyHost host,
        string name_contains,
        string return_type,
        string[] parameter_types,
        int? parameter_count,
        string @namespace,
        ITypeDefinition resolvedDeclaringType,
        string type_name_contains,
        AccessibilityFilter accessibility,
        bool include_compiler_generated)
    {
        var matches = new List<IMethod>();

        foreach (var typeDef in host.TypeSystem.MainModule.TypeDefinitions)
        {
            if (!string.IsNullOrEmpty(@namespace) && typeDef.Namespace != @namespace)
                continue;
            // By identity, not FullName, which same-named generic types share.
            if (resolvedDeclaringType != null && typeDef.MetadataToken != resolvedDeclaringType.MetadataToken)
                continue;
            if (!string.IsNullOrEmpty(type_name_contains) &&
                !typeDef.Name.Contains(type_name_contains, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var method in typeDef.Methods)
            {
                if (method.IsAccessor)
                    continue;
                if (!include_compiler_generated && CompilerGeneratedFilter.IsCompilerGenerated(method))
                    continue;
                if (!accessibility.Permits(method.Accessibility))
                    continue;
                if (!string.IsNullOrEmpty(name_contains) &&
                    !method.Name.Contains(name_contains, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (parameter_count.HasValue && method.Parameters.Count != parameter_count.Value)
                    continue;

                if (parameter_types != null)
                {
                    if (method.Parameters.Count != parameter_types.Length) continue;
                    var paramsOk = true;
                    for (int i = 0; i < parameter_types.Length; i++)
                    {
                        if (!TypeMatcher.Matches(method.Parameters[i].Type, parameter_types[i]))
                        {
                            paramsOk = false;
                            break;
                        }
                    }
                    if (!paramsOk) continue;
                }

                if (!string.IsNullOrEmpty(return_type) && !TypeMatcher.Matches(method.ReturnType, return_type))
                    continue;

                matches.Add(method);
            }
        }

        return matches;
    }

    /// <summary>
    /// One line per method in the reference form the other tools print and take, without
    /// parameter names, so a line copied into parameter_types matches; static is kept, and so is
    /// the return type, since a search by signature asks about both. Sorted by the reference.
    /// </summary>
    private static string FormatResults(List<IMethod> matches, int limit)
    {
        var lines = matches
            .Select(m => (Reference: ReferenceFormatter.FormatMethodReference(m), Method: m))
            .OrderBy(entry => entry.Reference, StringComparer.Ordinal)
            .Select(entry => $"{(entry.Method.IsStatic ? "static " : "")}{entry.Reference} → " +
                ReferenceFormatter.FormatReturnType(entry.Method))
            .ToList();
        return ListOutput.Format(lines, limit, Noun.Regular("method"));
    }
}
