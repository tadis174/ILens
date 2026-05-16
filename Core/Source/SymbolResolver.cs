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
    /// Resolve a fully-qualified type name (e.g., "System.String").
    /// Nested types use '+' separator (e.g., "System.Environment+SpecialFolder").
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

        throw new ArgumentException($"Type not found: {typeName}.");
    }

    // --- Member umbrella -------------------------------------------------------

    /// <summary>
    /// Resolve a member by name. With a method-overload hint (<paramref name="parameterCount"/>
    /// or <paramref name="parameterTypes"/>) the name is resolved as a method. Without a hint,
    /// every member kind is probed: a single match is returned with its origin and category;
    /// a name that exists as more than one kind is reported as an error rather than silently
    /// resolved by a fixed priority. Throws if no member by that name exists in any kind.
    /// </summary>
    /// <remarks>
    /// C# guarantees a method, property, field, and event within one type cannot share a
    /// name, but IL does not — and obfuscated assemblies deliberately reuse names across
    /// metadata tables. Probing every kind turns such a collision into an actionable error
    /// instead of a silent first-match-wins answer.
    /// </remarks>
    public (ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category) ResolveMember(
        ITypeDefinition type, string memberName, int? parameterCount = null,
        string[] parameterTypes = null)
    {
        ValidateDisambiguationArgs(parameterCount, parameterTypes);

        // A method-overload hint means the caller is asking for a method specifically.
        if (parameterCount.HasValue || parameterTypes != null)
        {
            var hinted = TryFindMethod(type, memberName, parameterCount, parameterTypes)
                ?? throw new ArgumentException(
                    $"Method '{memberName}' not found on {type.FullName}, " +
                    "its base types, or as an extension method.");
            return (hinted.Method, hinted.Origin, SymbolCategory.Method);
        }

        // No hint: probe every kind so a cross-kind name collision is caught.
        var matches = new List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)>();

        var method = TryFindMethod(type, memberName, null, null);
        if (method.HasValue)
            matches.Add((method.Value.Method, method.Value.Origin, SymbolCategory.Method));

        var prop = TryFindProperty(type, memberName);
        if (prop.HasValue)
            matches.Add((prop.Value.Property, prop.Value.Origin, SymbolCategory.Property));

        var field = TryFindField(type, memberName);
        if (field.HasValue)
            matches.Add((field.Value.Field, field.Value.Origin, SymbolCategory.Field));

        var evt = TryFindEvent(type, memberName);
        if (evt.HasValue)
            matches.Add((evt.Value.Event, evt.Value.Origin, SymbolCategory.Event));

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
            throw new ArgumentException(
                $"Member '{memberName}' not found on {type.FullName}, its base types, " +
                "or as an extension method.");

        // More than one kind matched — the name collides across member kinds.
        var kinds = string.Join(", ", matches.Select(m => m.Category));
        throw new ArgumentException(
            $"Member '{memberName}' on {type.FullName} is ambiguous — it exists as " +
            $"more than one member kind ({kinds}). Re-query with a kind-specific tool, " +
            "or pass a parameterCount / parameterTypes hint if you mean the method.");
    }

    /// <summary>
    /// Guard mirroring <c>find_methods</c>: when both a parameter count and an ordered
    /// parameter-type list are given, they must agree on arity.
    /// </summary>
    private static void ValidateDisambiguationArgs(int? parameterCount, string[] parameterTypes)
    {
        if (parameterTypes != null && parameterCount.HasValue
            && parameterTypes.Length != parameterCount.Value)
        {
            throw new ArgumentException(
                $"parameterCount ({parameterCount.Value}) contradicts " +
                $"parameterTypes.Length ({parameterTypes.Length}).");
        }
    }

    // --- Method resolution -----------------------------------------------------

    /// <summary>
    /// Resolve a method by name, searching declared → inherited → extension methods.
    /// Returns the method and its origin relative to the requested type.
    /// </summary>
    public (IMethod Method, MemberOrigin Origin) ResolveMethod(
        ITypeDefinition type, string methodName, int? parameterCount = null,
        string[] parameterTypes = null)
    {
        ValidateDisambiguationArgs(parameterCount, parameterTypes);
        return TryFindMethod(type, methodName, parameterCount, parameterTypes)
            ?? throw new ArgumentException(
                $"Method '{methodName}' not found on {type.FullName}, " +
                "its base types, or as an extension method.");
    }

    private (IMethod Method, MemberOrigin Origin)? TryFindMethod(
        ITypeDefinition type, string methodName, int? parameterCount, string[] parameterTypes)
    {
        // 1. Declared on the requested type
        var methods = type.Methods.Where(m => m.Name == methodName).ToList();
        if (methods.Count > 0)
            return (DisambiguateMethod(methods, type.FullName, methodName, parameterCount, parameterTypes),
                MemberOrigin.Declared);

        // 2. Walk base class chain
        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            methods = baseType.Methods.Where(m => m.Name == methodName).ToList();
            if (methods.Count > 0)
                return (DisambiguateMethod(methods, baseType.FullName, methodName, parameterCount, parameterTypes),
                    MemberOrigin.InheritedFrom(baseType.FullName));
        }

        // 3. Extension methods
        var extensions = FindExtensionMethodsByName(type, methodName);
        if (extensions.Count > 0)
        {
            var method = DisambiguateMethod(extensions, "(extension methods)",
                methodName, parameterCount, parameterTypes);
            return (method, MemberOrigin.ExtensionOn(method.DeclaringType.FullName));
        }

        // 4. Accessor-name fallback. find_methods hides accessors so generic browsing
        // isn't noisy with compiler-generated members; this fallback is the direct
        // route back to a known accessor body. Disambiguation hints don't apply —
        // accessors are unique per property/event.
        var (prefix, baseName) = SplitAccessorPrefix(methodName);
        if (prefix is "get_" or "set_")
        {
            var found = TryFindProperty(type, baseName);
            if (found.HasValue)
            {
                var (property, origin) = found.Value;
                var accessor = prefix == "get_" ? property.Getter : property.Setter;
                if (accessor != null)
                    return (accessor, origin);
                throw new ArgumentException(
                    $"Property '{baseName}' on {property.DeclaringType.FullName} has no " +
                    $"{(prefix == "get_" ? "getter" : "setter")}.");
            }
        }
        else if (prefix is "add_" or "remove_")
        {
            var found = TryFindEvent(type, baseName);
            if (found.HasValue)
            {
                var (evt, origin) = found.Value;
                var accessor = prefix == "add_" ? evt.AddAccessor : evt.RemoveAccessor;
                if (accessor != null)
                    return (accessor, origin);
                throw new ArgumentException(
                    $"Event '{baseName}' on {evt.DeclaringType.FullName} has no " +
                    $"{(prefix == "add_" ? "add" : "remove")} accessor.");
            }
        }

        return null;
    }

    /// <summary>
    /// Split an IL accessor name into its prefix and the property/event name. Returns
    /// <c>(null, name)</c> if the name doesn't begin with one of the four C#-emitted
    /// accessor prefixes (<c>get_</c>, <c>set_</c>, <c>add_</c>, <c>remove_</c>).
    /// </summary>
    private static (string Prefix, string BaseName) SplitAccessorPrefix(string methodName)
    {
        foreach (var prefix in new[] { "get_", "set_", "add_", "remove_" })
        {
            if (methodName.Length > prefix.Length &&
                methodName.StartsWith(prefix, StringComparison.Ordinal))
                return (prefix, methodName.Substring(prefix.Length));
        }
        return (null, methodName);
    }

    /// <summary>
    /// Pick a single method from a same-name candidate set. A single candidate is
    /// returned directly. Otherwise the set is narrowed by the most specific hint the
    /// caller gave — <paramref name="parameterTypes"/> (matched per position via
    /// <see cref="TypeMatcher"/>), else <paramref name="parameterCount"/> (by arity).
    /// If the narrowed set is not exactly one, an error lists the candidates rather
    /// than silently returning the first.
    /// </summary>
    private IMethod DisambiguateMethod(List<IMethod> methods, string context,
        string methodName, int? parameterCount, string[] parameterTypes)
    {
        if (methods.Count == 1)
            return methods[0];

        List<IMethod> candidates;
        string filter;
        if (parameterTypes != null)
        {
            candidates = methods.Where(m => MatchesParameterTypes(m, parameterTypes)).ToList();
            filter = $"parameter types ({string.Join(", ", parameterTypes)})";
        }
        else if (parameterCount.HasValue)
        {
            candidates = methods.Where(m => m.Parameters.Count == parameterCount.Value).ToList();
            filter = $"{parameterCount.Value} parameter(s)";
        }
        else
        {
            candidates = methods;
            filter = null;
        }

        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count == 0)
            throw new ArgumentException(
                $"No overload of '{methodName}' on {context} matches {filter}. " +
                $"Available: {ReferenceFormatter.FormatMethodList(methods)}.");

        // More than one candidate still matches.
        var hint = parameterTypes != null
            ? "the candidates share these parameter types and cannot be narrowed further"
            : parameterCount.HasValue
                ? "they are same-arity overloads — specify parameterTypes to disambiguate"
                : "specify parameterCount or parameterTypes to disambiguate";
        throw new ArgumentException(
            $"'{methodName}' on {context} has {candidates.Count} matching overloads; {hint}: " +
            $"{ReferenceFormatter.FormatMethodList(candidates)}.");
    }

    /// <summary>
    /// True if the method's parameters match the ordered <paramref name="patterns"/>
    /// position for position (arity included). Same loose matching as <c>find_methods</c>.
    /// </summary>
    private static bool MatchesParameterTypes(IMethod method, string[] patterns)
    {
        if (method.Parameters.Count != patterns.Length)
            return false;
        for (int i = 0; i < patterns.Length; i++)
            if (!TypeMatcher.Matches(method.Parameters[i].Type, patterns[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Find extension methods for a type by name. Scans all types in the assembly
    /// for static methods with [Extension] attribute whose first parameter matches
    /// the target type or any of its supertypes (base classes, implemented interfaces,
    /// and <c>System.Object</c>).
    /// </summary>
    private List<IMethod> FindExtensionMethodsByName(ITypeDefinition targetType, string methodName)
    {
        // GetAllBaseTypeDefinitions includes the type itself plus every supertype —
        // classes, interfaces, and System.Object — so 'this IEnumerable<T>' extensions
        // resolve on a concrete List<T> and 'this object' extensions resolve on any
        // reference type. TypeWalker.WalkBaseTypes (used for instance-member resolution)
        // intentionally stops before Object and skips interfaces; extension-method
        // discovery needs the wider scope.
        var targetTypes = new HashSet<string>(
            targetType.GetAllBaseTypeDefinitions().Select(t => t.FullName));

        var results = new List<IMethod>();

        foreach (var typeDef in _typeSystem.MainModule.TypeDefinitions)
        {
            if (!TypeWalker.IsStaticClass(typeDef))
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
                $"Field '{fieldName}' not found on {type.FullName} or its base types.");
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
                $"Property '{propertyName}' not found on {type.FullName} or its base types.");
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
                $"Event '{eventName}' not found on {type.FullName} or its base types.");
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

}
