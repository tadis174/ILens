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
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null,
        [Description(ParameterDescriptions.ReturnType)] string return_type = null)
    {
        var hint = new SymbolResolver.MethodHint(parameter_count, TypeMatcher.SplitPatterns(parameter_types), return_type);
        // A name with the '@' C# writes before a keyword, as a declaration line writes it, means the keyword.
        member_name = SymbolResolver.ReadName(member_name);

        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;
        var resolver = host.Resolver;

        // An array has no code of its own, but an extension method this assembly declares for it
        // does. A member of System.Array or System.Object that comes first is the question asked
        // through the class that declares it.
        if (resolver.ReadArray(type_name) is { } array)
        {
            if (string.IsNullOrEmpty(member_name))
                throw SymbolResolver.ArrayNotTaken(type_name);
            var found = resolver.FindExtensionThroughArray(array, member_name, hint);
            if (found.Extension is { } throughArray)
                return DecompileExtension(host, throughArray);
            if (found.MemberOwner is { } owner)
            {
                // An assembly whose code uses arrays through their own instructions alone names
                // neither class, and a question through the class by name would not find it.
                if (resolver.UnnamedOwnerOrigin(owner) is { } ownerOrigin)
                    throw new ArgumentException($"{owner} is not defined in this assembly; it comes from {ownerOrigin}.");
                return Decompile(registry, assembly, owner, member_name, parameter_count, parameter_types, return_type);
            }
            throw resolver.ArrayMemberNotFound(array, member_name, hint);
        }

        var (type, external) = resolver.ResolveTypeOrExternal(type_name);
        if (external is not null)
        {
            // A type another assembly defines holds no code here, but an extension method this
            // assembly declares for it does. Any other question gets the error that names where
            // the type comes from.
            if (!string.IsNullOrEmpty(member_name)
                && resolver.FindExtensionThrough(external, member_name, hint) is { } extension)
                return DecompileExtension(host, extension);
            type = resolver.ResolveType(type_name);
        }

        // IsNullOrEmpty rather than null, so an LLM passing "" for "omit" gets the type.
        if (string.IsNullOrEmpty(member_name))
        {
            if (hint.IsGiven)
                throw new ArgumentException(
                    $"{nameof(parameter_count)}, {nameof(parameter_types)} and {nameof(return_type)} select a " +
                    $"method overload, so they need {nameof(member_name)}.");
            return host.DecompileType(type);
        }

        var (symbol, origin, _) = resolver.ResolveMember(type, member_name, hint);

        // The comment names what resolution picked rather than what was asked for, so a caller
        // can see which overload came back. It is anchored on the type asked about, and an
        // inherited or extension origin is reported beside it; an extension method is named
        // under the class that declares it, with the type it extends in the mark. Where another
        // member of the name would read alike, it is written in full, since it is all that shows
        // which came back.
        var typeDisplay = ReferenceFormatter.FormatTypeName(type);
        IReadOnlyList<IMember> namesakes = symbol is IMember member ? resolver.Namesakes(type, member) : [];
        var (reference, source) = symbol switch
        {
            IMethod method => (ReferenceFormatter.NameAmong(method, namesakes.OfType<IMethod>(),
                    (m, full) => ReferenceFormatter.FormatResolvedSignature(m, type, full)),
                host.DecompileMethod(method)),
            IProperty property => (ReferenceFormatter.NameAmong(property, namesakes.OfType<IProperty>(),
                    (p, full) => $"{typeDisplay}.{p.Name}{ReferenceFormatter.FormatIndices(p, full)}"),
                host.DecompileProperty(property)),
            IEvent @event => ($"{typeDisplay}.{@event.Name}", host.DecompileEvent(@event)),
            IField field => ($"{typeDisplay}.{field.Name}", host.DecompileField(field)),
            _ => throw new InvalidOperationException($"No decompilation for {symbol.SymbolKind}."),
        };
        var header = origin.Kind == "declared" ? $"// {reference}" : $"// {reference} {origin.Format()}";
        // An extension method found while a base class of the type was not read may stand where
        // a member of that class would answer, which the line under the header says.
        if (symbol is IMethod foundMethod && resolver.ExtensionNotChecked(type, foundMethod) is { } notChecked)
            header += $"\n// {notChecked}";
        return $"{header}\n{source}";
    }

    /// <summary>
    /// An extension method found through a type another assembly defines or an array type, as the
    /// code of one found through a type of this assembly is shown, with the line that says what
    /// the lookup could not read under the header.
    /// </summary>
    private static string DecompileExtension(AssemblyHost host, SymbolResolver.ExtensionThrough extension)
    {
        var reference = ReferenceFormatter.NameAmong(extension.Method, extension.Namesakes,
            (method, full) => ReferenceFormatter.FormatResolvedSignature(method, extension.Type, full));
        var notChecked = extension.NotChecked is null ? "" : $"\n// {extension.NotChecked}";
        return $"// {reference} {extension.Origin.Format()}{notChecked}\n{host.DecompileMethod(extension.Method)}";
    }
}
