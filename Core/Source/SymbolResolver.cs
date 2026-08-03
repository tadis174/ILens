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

        // The ordinary C# event declaration compiles to an event *and* a private field of
        // the same name holding the subscriber list, so the cross-kind probe reports every
        // one of them as a collision. That is not the collision this check exists for: the
        // pair has a fixed compiler-generated shape, and only the event was ever written in
        // source. Resolve to the event — otherwise no field-like event is addressable by
        // name at all, which silently rules out the common case.
        var collapsed = CollapseFieldLikeEvent(matches);
        if (collapsed.HasValue)
            return collapsed.Value;

        // More than one kind matched — the name collides across member kinds.
        var kinds = string.Join(", ", matches.Select(m => m.Category));
        throw new ArgumentException(
            $"Member '{memberName}' on {type.FullName} is ambiguous — it exists as " +
            $"more than one member kind ({kinds}). Re-query with a kind-specific tool, " +
            "or pass a parameterCount / parameterTypes hint if you mean the method.");
    }

    /// <summary>
    /// Collapse a field/event pair to the event when the field is that event's backing
    /// store, or <c>null</c> for anything else — so a name genuinely reused across metadata
    /// tables still reports as ambiguous.
    /// </summary>
    private static (ISymbol, MemberOrigin, SymbolCategory)? CollapseFieldLikeEvent(
        List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)> matches)
    {
        if (matches.Count != 2)
            return null;

        var evt = matches.FirstOrDefault(m => m.Category == SymbolCategory.Event);
        var field = matches.FirstOrDefault(m => m.Category == SymbolCategory.Field);
        if (evt.Symbol is not IEvent e || field.Symbol is not IField f)
            return null;

        return IsBackingFieldOf(f, e) ? evt : null;
    }

    /// <summary>
    /// The field holding a field-like event's subscriber list, or <c>null</c> when the event
    /// declares its accessors explicitly and so has no such field.
    /// </summary>
    public static IField BackingFieldOf(IEvent evt) =>
        evt.DeclaringTypeDefinition?.Fields.FirstOrDefault(f => IsBackingFieldOf(f, evt));

    /// <summary>
    /// True if <paramref name="field"/> is the subscriber list the compiler emitted for
    /// <paramref name="evt"/>: same declaring type and name, private, static exactly when the
    /// event is, and typed as the event's delegate.
    /// </summary>
    /// <remarks>
    /// Matching the whole shape is what keeps this from claiming an unrelated same-named
    /// field — an obfuscator would have to reproduce every part of it, at which point the two
    /// are indistinguishable anyway. Requiring the field to carry the event's own name is
    /// deliberate: VB.NET emits it as <c>XEvent</c>, so only the C# form is recognized, which
    /// is what ILens targets.
    ///
    /// Type identity goes through <c>ReflectionName</c> rather than <c>IType.Equals</c>.
    /// ILens is routinely pointed at an assembly whose references cannot be resolved — that
    /// is the normal case for a decompiled game or mod DLL — and a type from an unresolvable
    /// reference comes back as a distinct unknown-type instance per resolution site, so the
    /// field's type and the event's delegate type compare unequal despite naming the same
    /// type. The name is what survives that.
    /// </remarks>
    private static bool IsBackingFieldOf(IField field, IEvent evt)
    {
        var declaringType = evt.DeclaringTypeDefinition;
        return declaringType != null
            && field.DeclaringTypeDefinition?.FullName == declaringType.FullName
            && field.Name == evt.Name
            && field.Accessibility == Accessibility.Private
            && field.IsStatic == evt.IsStatic
            && field.Type.ReflectionName == evt.ReturnType.ReflectionName;
    }

    /// <summary>
    /// Guard for method-overload disambiguation hints: when both a parameter count and an
    /// ordered parameter-type list are given, they must agree on arity. Used by every tool
    /// that accepts both hints (decompile_method, analyze, find_methods) so the contradiction
    /// error message is identical across the surface.
    /// </summary>
    internal static void ValidateDisambiguationArgs(int? parameterCount, string[] parameterTypes)
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
        var hinted = parameterCount.HasValue || parameterTypes != null;

        // 1-2. Walk the requested type, then its base-class chain (most-derived first).
        // C# overload resolution spans the inheritance chain: a base method of a different
        // signature stays a candidate even when the requested type declares a same-named one,
        // so `foo.Bar("x")` can bind to a base `Bar(string)` past a derived `Bar(int)`. A hint
        // must therefore keep walking past a level whose overloads don't satisfy it, rather
        // than stopping at the first level that merely has the name (TASK-24). The most-derived
        // satisfying level wins, so an override/new still resolves to the derived declaration.
        // `named` accumulates every same-named overload across the chain so an ultimately
        // unsatisfied hint can report the whole set instead of just the requested type's.
        var levels = new List<(ITypeDefinition Level, MemberOrigin Origin)>
        {
            (type, MemberOrigin.Declared)
        };
        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
            levels.Add((baseType, MemberOrigin.InheritedFrom(baseType.FullName)));

        var named = new List<IMethod>();
        foreach (var (level, origin) in levels)
        {
            var here = level.Methods.Where(m => m.Name == methodName).ToList();
            if (here.Count == 0)
                continue;

            // No hint: the most-derived level that declares the name wins, exactly as before —
            // a single method resolves, several report a same-level ambiguity.
            if (!hinted)
                return (DisambiguateMethod(here, level.FullName, methodName, null, null), origin);

            named.AddRange(here);
            var satisfying = NarrowByHint(here, parameterCount, parameterTypes);
            if (satisfying.Count == 1)
                return (satisfying[0], origin);
            if (satisfying.Count > 1)
                // Genuinely ambiguous at this level; a base level cannot override that, so
                // hand off to DisambiguateMethod for the "N matching overloads" error.
                return (DisambiguateMethod(here, level.FullName, methodName, parameterCount, parameterTypes),
                    origin);
            // satisfying.Count == 0: a base level may still declare a matching overload — walk on.
        }

        // 3. Extension methods, tried only after no instance overload satisfied the request —
        // the order C# uses, where extensions are a fallback for unresolved instance calls.
        var extensions = FindExtensionMethodsByName(type, methodName);
        if (extensions.Count > 0)
        {
            if (!hinted || NarrowByHint(extensions, parameterCount, parameterTypes).Count > 0)
            {
                var method = DisambiguateMethod(extensions, "(extension methods)",
                    methodName, parameterCount, parameterTypes);
                return (method, MemberOrigin.ExtensionOn(method.DeclaringType.FullName));
            }
            named.AddRange(extensions);
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

        // A hint was given and the name exists somewhere on the chain (or as an extension),
        // but nothing satisfies it. Report the whole candidate set — the overload the caller
        // wanted may be declared on a base type, and naming it is the actionable result.
        if (hinted && named.Count > 0)
            throw new ArgumentException(
                $"No overload of '{methodName}' on {type.FullName} matches " +
                $"{DescribeHint(parameterCount, parameterTypes)}. " +
                $"Available: {ReferenceFormatter.FormatMethodList(named)}.");

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
    /// Pick a single method from a same-name candidate set. The set is narrowed by the
    /// most specific hint the caller gave — <paramref name="parameterTypes"/> (matched
    /// per position via <see cref="TypeMatcher"/>), else <paramref name="parameterCount"/>
    /// (by arity). If the narrowed set is not exactly one, an error lists the candidates
    /// rather than silently returning the first.
    /// </summary>
    /// <remarks>
    /// A hint is a filter that must be satisfied, not a tie-breaker consulted only when
    /// more than one candidate survives. A lone candidate is checked against the hint too:
    /// short-circuiting on <c>methods.Count == 1</c> answers a question the caller did not
    /// ask — handing back <c>Equals(Thing)</c> to someone who explicitly asked for
    /// <c>Equals(object)</c>, with nothing in the output to reveal the substitution. An
    /// accurate "no overload matches" is a first-class result, and often the very fact the
    /// caller was establishing; callers use ILens precisely because they cannot read the IL
    /// themselves, so they have no independent way to notice a silent swap.
    /// </remarks>
    private IMethod DisambiguateMethod(List<IMethod> methods, string context,
        string methodName, int? parameterCount, string[] parameterTypes)
    {
        var candidates = NarrowByHint(methods, parameterCount, parameterTypes);

        if (candidates.Count == 1)
            return candidates[0];

        // Control reaches here only with more than one candidate: every caller passes a set
        // the hint is known to leave non-empty — the no-hint set is returned whole, and the
        // hinted callers pre-check for at least one match before delegating here. A hint that
        // matches nothing is handled upstream by TryFindMethod's cross-chain error, which can
        // name the whole inheritance chain rather than just this one level's candidates.
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
    /// Narrow a same-name candidate set by the most specific hint supplied —
    /// <paramref name="parameterTypes"/> (matched per position via <see cref="TypeMatcher"/>),
    /// else <paramref name="parameterCount"/> (by arity), else the set unchanged. A pure
    /// filter: the 0 / 1 / many outcomes are interpreted by the caller, which is what lets
    /// <see cref="TryFindMethod"/> keep walking the base chain when a level yields zero.
    /// </summary>
    private static List<IMethod> NarrowByHint(
        List<IMethod> methods, int? parameterCount, string[] parameterTypes)
    {
        if (parameterTypes != null)
            return methods.Where(m => MatchesParameterTypes(m, parameterTypes)).ToList();
        if (parameterCount.HasValue)
            return methods.Where(m => m.Parameters.Count == parameterCount.Value).ToList();
        return methods;
    }

    /// <summary>
    /// Describe the disambiguation hint for an error message, e.g. <c>parameter types
    /// (string, int)</c> or <c>2 parameter(s)</c>. Only called when a hint is present — a
    /// no-hint filter never removes a candidate, so the "nothing matches" paths never reach it.
    /// </summary>
    private static string DescribeHint(int? parameterCount, string[] parameterTypes) =>
        parameterTypes != null
            ? $"parameter types ({string.Join(", ", parameterTypes)})"
            : $"{parameterCount.Value} parameter(s)";

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
    /// Resolve a property by name, searching declared → inherited. Throws if a name
    /// matches more than one property on a single type — the only case in C# is an
    /// indexer with multiple overloads, where silently returning the first match
    /// would hand back the wrong accessor body. The error names the candidates and
    /// points at <c>decompile_method</c> on <c>get_Item</c> / <c>set_Item</c> with
    /// <c>parameterTypes</c> as the disambiguation path.
    /// </summary>
    public (IProperty Property, MemberOrigin Origin) ResolveProperty(
        ITypeDefinition type, string propertyName)
    {
        var declared = type.Properties.Where(p => p.Name == propertyName).ToList();
        if (declared.Count > 1)
            throw AmbiguousPropertyError(declared, type.FullName, propertyName);
        if (declared.Count == 1)
            return (declared[0], MemberOrigin.Declared);

        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            var inherited = baseType.Properties.Where(p => p.Name == propertyName).ToList();
            if (inherited.Count > 1)
                throw AmbiguousPropertyError(inherited, baseType.FullName, propertyName);
            if (inherited.Count == 1)
                return (inherited[0], MemberOrigin.InheritedFrom(baseType.FullName));
        }

        throw new ArgumentException(
            $"Property '{propertyName}' not found on {type.FullName} or its base types.");
    }

    private static ArgumentException AmbiguousPropertyError(
        List<IProperty> candidates, string context, string propertyName)
    {
        var rendered = string.Join(", ", candidates.Select(FormatPropertyCandidate));
        return new ArgumentException(
            $"Property '{propertyName}' on {context} has {candidates.Count} overloads — " +
            "in C# this is always an indexer, but non-C# metadata or obfuscation can " +
            "produce non-indexer collisions too. decompile_property does not disambiguate; " +
            $"use decompile_method on get_{propertyName} / set_{propertyName} with " +
            $"parameterTypes. Candidates: {rendered}.");
    }

    private static string FormatPropertyCandidate(IProperty property)
    {
        if (property.IsIndexer)
        {
            var args = string.Join(", ",
                property.Parameters.Select(p => ReferenceFormatter.FormatTypeRef(p.Type)));
            return $"{property.DeclaringType.FullName}.{property.Name}[{args}]";
        }
        return $"{property.DeclaringType.FullName}.{property.Name}";
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
    /// Resolve an event by name, searching declared → inherited. Throws on the
    /// same multi-match condition as <see cref="ResolveProperty"/> for symmetry —
    /// C# does not let events overload, so the branch is defensive and fires only
    /// on metadata produced by other source languages or obfuscators.
    /// </summary>
    public (IEvent Event, MemberOrigin Origin) ResolveEvent(
        ITypeDefinition type, string eventName)
    {
        var declared = type.Events.Where(e => e.Name == eventName).ToList();
        if (declared.Count > 1)
            throw AmbiguousEventError(declared, type.FullName, eventName);
        if (declared.Count == 1)
            return (declared[0], MemberOrigin.Declared);

        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
        {
            var inherited = baseType.Events.Where(e => e.Name == eventName).ToList();
            if (inherited.Count > 1)
                throw AmbiguousEventError(inherited, baseType.FullName, eventName);
            if (inherited.Count == 1)
                return (inherited[0], MemberOrigin.InheritedFrom(baseType.FullName));
        }

        throw new ArgumentException(
            $"Event '{eventName}' not found on {type.FullName} or its base types.");
    }

    private static ArgumentException AmbiguousEventError(
        List<IEvent> candidates, string context, string eventName)
    {
        var rendered = string.Join(", ",
            candidates.Select(e => $"{e.DeclaringType.FullName}.{e.Name}"));
        return new ArgumentException(
            $"Event '{eventName}' on {context} has {candidates.Count} entries with " +
            "the same name — non-C# metadata. Candidates: " + rendered + ".");
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
