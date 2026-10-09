using System.ComponentModel;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class DecompileTool
{
    [McpServerTool(Name = "decompile", ReadOnly = true),
     Description("Decompile a type, or one of its members, to C#. Omit member_name for the whole type. " +
        "A member's code opens with a comment naming it, with the return type of a method and, when " +
        "the member is not declared on the type asked about, where it comes from. A property or event " +
        "comes with its accessors, and a field with the initializer the constructors assign it.")]
    public static string Decompile(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.TypeName)] string type_name,
        [Description(ParameterDescriptions.MemberName)] string member_name = null,
        [Description(ParameterDescriptions.ParameterCount)] int? parameter_count = null,
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null)
    {
        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;
        var resolver = host.Resolver;
        var type = resolver.ResolveType(type_name);

        // IsNullOrEmpty rather than null, so an LLM passing "" for "omit" gets the type.
        if (string.IsNullOrEmpty(member_name))
        {
            if (parameter_count.HasValue || parameter_types != null)
                throw new ArgumentException(
                    $"{nameof(parameter_count)} and {nameof(parameter_types)} select a method overload, " +
                    $"so they need {nameof(member_name)}.");
            return host.DecompileType(type);
        }

        var (symbol, origin, _) = resolver.ResolveMember(type, member_name, parameter_count, parameter_types);

        // The comment names what resolution picked rather than what was asked for, so a caller
        // can see which overload came back. It is anchored on the type asked about, and an
        // inherited or extension origin is reported beside it.
        var typeDisplay = ReferenceFormatter.FormatTypeName(type);
        var (reference, source) = symbol switch
        {
            IMethod method => (ReferenceFormatter.FormatResolvedSignature(method, typeDisplay), host.DecompileMethod(method)),
            IProperty property => ($"{typeDisplay}.{property.Name}{ReferenceFormatter.FormatIndices(property)}",
                host.DecompileProperty(property)),
            IEvent @event => ($"{typeDisplay}.{@event.Name}", host.DecompileEvent(@event)),
            IField field => ($"{typeDisplay}.{field.Name}", host.DecompileField(field)),
            _ => throw new InvalidOperationException($"No decompilation for {symbol.SymbolKind}."),
        };
        var header = origin.Kind == "declared" ? $"// {reference}" : $"// {reference} {origin.Format()}";
        return $"{header}\n{source}";
    }
}
