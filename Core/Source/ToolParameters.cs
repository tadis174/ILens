namespace ILens;

/// <summary>
/// The description of each parameter several tools take, so that it reads alike in every
/// tool: what it means, how it matches, and at most one example. A default is left to the
/// schema, which carries it, and what omitting a parameter means to one tool belongs in that
/// tool's description.
/// </summary>
public static class ParameterDescriptions
{
    public const string Assembly = "Path to the .NET assembly to inspect (must be under an allowed root).";
    public const string AssemblyA = "Path to the first assembly (must be under an allowed root).";
    public const string AssemblyB = "Path to the second assembly (must be under an allowed root).";
    public const string TypeName = "Fully qualified type name, e.g. 'System.IO.File'.";
    public const string MemberName =
        "Member name, e.g. 'ReadAllText'; '.ctor' names a constructor, and get_X, set_X, add_X or remove_X an accessor.";
    public const string ParameterCount =
        $"Number of parameters the method takes. If {ParameterNames.ParameterTypes} is also given, the two must agree.";
    public const string ParameterTypes =
        "Parameter-type patterns, in order; the method takes exactly these. A pattern is a type's name, " +
        "full name or C# keyword, and matches the declared type only, not a base or derived one. 'List' " +
        "matches any List<T> and 'List<int>' only that one, a trailing '[]' an array, 'int' also int?, " +
        "and a ref, out or in prefix only a parameter passed by reference.";
    public const string Namespace = "Exact namespace, e.g. 'System.IO'; its sub-namespaces are not included.";
    public const string NameContains = "Case-insensitive substring of the name, without namespace or declaring type.";
    public const string Accessibility =
        "public_protected also covers protected internal and private protected; all adds internal and private.";
    public const string IncludeCompilerGenerated =
        "Include what the compiler or a source generator created, such as names starting with '<'.";
    public const string Limit = "Maximum number of results to list.";
}

/// <summary>
/// The names of parameters that code outside a tool writes into its messages. A tool itself
/// names its parameters with nameof, which yields the schema name because the C# parameter
/// carries it.
/// </summary>
public static class ParameterNames
{
    public const string ParameterCount = "parameter_count";
    public const string ParameterTypes = "parameter_types";
    public const string ParameterName = "parameter_name";
    public const string NonDefaultOnly = "non_default_only";
}
