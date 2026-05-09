using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Resolves user-provided fully-qualified names to ILSpy type system symbols.
/// Searches declared members, then base types, then extension methods (methods only).
/// </summary>
public sealed class SymbolResolver
{
    private readonly ICompilation _typeSystem;

    public SymbolResolver(ICompilation typeSystem)
    {
        _typeSystem = typeSystem;
    }

    /// <summary>
    /// Describes where a resolved member was found relative to the requested type.
    /// </summary>
    public readonly record struct MemberOrigin(string Kind, string DeclaringType = "")
    {
        public static MemberOrigin Declared => new("declared");

        public static MemberOrigin InheritedFrom(string typeName) =>
            new("inherited", typeName);

        public static MemberOrigin ExtensionOn(string typeName) =>
            new("extension", typeName);

        public string Format() => Kind switch
        {
            "inherited" => $"[inherited from {DeclaringType}]",
            "extension" => $"[extension method on {DeclaringType}]",
            _ => "[declared]"
        };
    }

    /// <summary>
    /// Resolve a fully-qualified type name (e.g., "RimWorld.PlantProperties").
    /// Nested types use '+' separator (e.g., "Verse.Thing+CompareByDrawAltitude").
    /// </summary>
    public ITypeDefinition ResolveType(string typeName)
    {
        // Try direct lookup first
        var type = _typeSystem.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.FullName == typeName);

        if (type != null)
            return type;

        // Try with '+' → '.' replacement for nested types specified with dots
        if (typeName.Contains('.'))
        {
            var parts = typeName.Split('.');
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var candidate = string.Join(".", parts.Take(i)) + "+" +
                    string.Join("+", parts.Skip(i));
                type = _typeSystem.MainModule.TypeDefinitions
                    .FirstOrDefault(t => t.FullName == candidate);
                if (type != null)
                    return type;
            }
        }

        throw new ArgumentException($"Type not found: {typeName}");
    }

    // --- Member umbrella -------------------------------------------------------

    /// <summary>
    /// Resolve a member by name, trying method → property → field → event in turn.
    /// Returns the first match together with its origin and category.
    /// Throws if a matching method exists but its overloads are ambiguous;
    /// also throws if no member by that name exists in any kind.
    /// </summary>
    /// <remarks>
    /// Per CLAUDE.md scope, this assumes C# member-name disjointness within a type:
    /// the four kinds cannot share a name in C#, so the search order is unambiguous.
    /// </remarks>
    public (ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category) ResolveMember(
        ITypeDefinition type, string memberName, int? parameterCount = null)
    {
        var method = TryFindMethod(type, memberName, parameterCount);
        if (method.HasValue)
            return (method.Value.Method, method.Value.Origin, SymbolCategory.Method);

        var prop = TryFindProperty(type, memberName);
        if (prop.HasValue)
            return (prop.Value.Property, prop.Value.Origin, SymbolCategory.Property);

        var field = TryFindField(type, memberName);
        if (field.HasValue)
            return (field.Value.Field, field.Value.Origin, SymbolCategory.Field);

        var evt = TryFindEvent(type, memberName);
        if (evt.HasValue)
            return (evt.Value.Event, evt.Value.Origin, SymbolCategory.Event);

        throw new ArgumentException(
            $"Member '{memberName}' not found on {type.FullName}, its base types, " +
            "or as an extension method");
    }

    // --- Method resolution -----------------------------------------------------

    /// <summary>
    /// Resolve a method by name, searching declared → inherited → extension methods.
    /// Returns the method and its origin relative to the requested type.
    /// </summary>
    public (IMethod Method, MemberOrigin Origin) ResolveMethod(
        ITypeDefinition type, string methodName, int? parameterCount = null)
    {
        return TryFindMethod(type, methodName, parameterCount)
            ?? throw new ArgumentException(
                $"Method '{methodName}' not found on {type.FullName}, " +
                "its base types, or as an extension method");
    }

    private (IMethod Method, MemberOrigin Origin)? TryFindMethod(
        ITypeDefinition type, string methodName, int? parameterCount)
    {
        // 1. Declared on the requested type
        var methods = type.Methods.Where(m => m.Name == methodName).ToList();
        if (methods.Count > 0)
            return (DisambiguateMethod(methods, type.FullName, methodName, parameterCount),
                MemberOrigin.Declared);

        // 2. Walk base class chain
        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            methods = baseType.Methods.Where(m => m.Name == methodName).ToList();
            if (methods.Count > 0)
                return (DisambiguateMethod(methods, baseType.FullName, methodName, parameterCount),
                    MemberOrigin.InheritedFrom(baseType.FullName));
        }

        // 3. Extension methods
        var extensions = FindExtensionMethods(type, methodName);
        if (extensions.Count > 0)
        {
            var method = DisambiguateMethod(extensions, "(extension methods)",
                methodName, parameterCount);
            return (method, MemberOrigin.ExtensionOn(method.DeclaringType.FullName));
        }

        return null;
    }

    private IMethod DisambiguateMethod(List<IMethod> methods, string context,
        string methodName, int? parameterCount)
    {
        if (methods.Count == 1)
            return methods[0];

        if (parameterCount.HasValue)
        {
            var match = methods
                .FirstOrDefault(m => m.Parameters.Count == parameterCount.Value);
            if (match != null)
                return match;

            throw new ArgumentException(
                $"No overload of '{methodName}' on {context} " +
                $"with {parameterCount.Value} parameters. " +
                $"Available: {FormatOverloads(methods)}");
        }

        throw new ArgumentException(
            $"'{methodName}' on {context} has {methods.Count} overloads. " +
            $"Specify parameterCount to disambiguate: {FormatOverloads(methods)}");
    }

    /// <summary>
    /// Find extension methods for a type by name. Scans all types in the assembly
    /// for static methods with [Extension] attribute whose first parameter matches
    /// the target type or any of its base types.
    /// </summary>
    private List<IMethod> FindExtensionMethods(ITypeDefinition targetType, string methodName)
    {
        var targetTypes = new HashSet<string> { targetType.FullName };
        foreach (var baseType in TypeWalker.WalkBaseTypes(targetType))
            targetTypes.Add(baseType.FullName);

        var results = new List<IMethod>();

        foreach (var typeDef in _typeSystem.MainModule.TypeDefinitions)
        {
            if (!IsStatic(typeDef))
                continue;

            foreach (var method in typeDef.Methods)
            {
                if (method.Name != methodName || !method.IsExtensionMethod)
                    continue;

                if (method.Parameters.Count == 0)
                    continue;

                var firstParamType = method.Parameters[0].Type.GetDefinition();
                if (firstParamType != null && targetTypes.Contains(firstParamType.FullName))
                    results.Add(method);
            }
        }

        return results;
    }

    // --- Field resolution ------------------------------------------------------

    /// <summary>
    /// Resolve a field by name, searching declared → inherited.
    /// </summary>
    public (IField Field, MemberOrigin Origin) ResolveField(
        ITypeDefinition type, string fieldName)
    {
        return TryFindField(type, fieldName)
            ?? throw new ArgumentException(
                $"Field '{fieldName}' not found on {type.FullName} or its base types");
    }

    private (IField Field, MemberOrigin Origin)? TryFindField(
        ITypeDefinition type, string fieldName)
    {
        var field = type.Fields.FirstOrDefault(f => f.Name == fieldName);
        if (field != null)
            return (field, MemberOrigin.Declared);

        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            field = baseType.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field != null)
                return (field, MemberOrigin.InheritedFrom(baseType.FullName));
        }

        return null;
    }

    // --- Property resolution ---------------------------------------------------

    /// <summary>
    /// Resolve a property by name, searching declared → inherited.
    /// </summary>
    public (IProperty Property, MemberOrigin Origin) ResolveProperty(
        ITypeDefinition type, string propertyName)
    {
        return TryFindProperty(type, propertyName)
            ?? throw new ArgumentException(
                $"Property '{propertyName}' not found on {type.FullName} or its base types");
    }

    private (IProperty Property, MemberOrigin Origin)? TryFindProperty(
        ITypeDefinition type, string propertyName)
    {
        var prop = type.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (prop != null)
            return (prop, MemberOrigin.Declared);

        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            prop = baseType.Properties.FirstOrDefault(p => p.Name == propertyName);
            if (prop != null)
                return (prop, MemberOrigin.InheritedFrom(baseType.FullName));
        }

        return null;
    }

    // --- Event resolution ------------------------------------------------------

    /// <summary>
    /// Resolve an event by name, searching declared → inherited.
    /// </summary>
    public (IEvent Event, MemberOrigin Origin) ResolveEvent(
        ITypeDefinition type, string eventName)
    {
        return TryFindEvent(type, eventName)
            ?? throw new ArgumentException(
                $"Event '{eventName}' not found on {type.FullName} or its base types");
    }

    private (IEvent Event, MemberOrigin Origin)? TryFindEvent(
        ITypeDefinition type, string eventName)
    {
        var evt = type.Events.FirstOrDefault(e => e.Name == eventName);
        if (evt != null)
            return (evt, MemberOrigin.Declared);

        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            evt = baseType.Events.FirstOrDefault(e => e.Name == eventName);
            if (evt != null)
                return (evt, MemberOrigin.InheritedFrom(baseType.FullName));
        }

        return null;
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>
    /// Check if a type is static (abstract + sealed in IL).
    /// </summary>
    private static bool IsStatic(ITypeDefinition type)
    {
        return type.IsAbstract && type.IsSealed;
    }

    private static string FormatOverloads(IReadOnlyList<IMethod> methods)
    {
        return string.Join(", ", methods.Select(m =>
            $"{m.DeclaringType.FullName}.{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.Name))})"));
    }
}
