using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Resolves user-provided fully-qualified names to ILSpy type system symbols, and names a
/// type the assembly only references by the assembly it comes from.
/// Searches declared members, then base types, then extension methods (methods only), and
/// last the members that implement an interface member of that name explicitly. A member found
/// on a base class another assembly defines is an error naming it and where it comes from.
/// </summary>
public sealed class SymbolResolver
{
    /// <summary>
    /// How deep a chain of nested type references may run before it is taken for a cycle.
    /// Real nesting stays in single digits.
    /// </summary>
    private const int MaxNesting = 64;

    private readonly ICompilation _typeSystem;

    /// <summary>
    /// The base classes of a type that another assembly defines, which <see cref="_typeSystem"/>
    /// does not hold; see <see cref="AssemblyHost.ReadExternalBases"/>.
    /// </summary>
    private readonly Func<ITypeDefinition, ExternalBases> _readExternalBases;

    /// <summary>The assembly's types by <c>FullName</c>, which same-named types share.</summary>
    private readonly Lazy<ILookup<string, ITypeDefinition>> _typesByFullName;

    /// <summary>
    /// The types the assembly references, keyed like <see cref="_typesByFullName"/>. Only
    /// consulted for a name the assembly defines no type by.
    /// </summary>
    private readonly Lazy<ILookup<string, ReferencedType>> _referencedTypesByFullName;

    public SymbolResolver(ICompilation typeSystem, Func<ITypeDefinition, ExternalBases> readExternalBases)
    {
        _typeSystem = typeSystem;
        _readExternalBases = readExternalBases;
        _typesByFullName = new(() => typeSystem.MainModule.TypeDefinitions
            .OrderBy(t => t.TypeParameterCount)
            .ToLookup(t => t.FullName, StringComparer.Ordinal));
        _referencedTypesByFullName = new(() => ReadReferencedTypes(
            ((MetadataModule)typeSystem.MainModule).MetadataFile.Metadata));
    }

    /// <summary>
    /// A type the assembly uses and another assembly defines, known only by the assembly's
    /// references to it. <paramref name="Assemblies"/> are the assemblies those references
    /// name, as <c>Name, Version=…</c>. There is more than one only when the references
    /// disagree, and none when they name a module rather than an assembly, or an assembly row
    /// the metadata cannot read.
    /// </summary>
    public sealed record ReferencedType(FullTypeName Name, IReadOnlyList<string> Assemblies);

    /// <summary>
    /// A type an analyze question names that the assembly does not define: how to print it,
    /// and which of the names the assembly's metadata uses for types denote it.
    /// </summary>
    public sealed record ExternalTypeName(string Display, Func<FullTypeName, bool> Matches);

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
    /// Throws unless <see cref="HasNamespace"/>. An exact namespace asserts that it exists, so
    /// answering a misspelling with an empty list would read as an empty namespace.
    /// </summary>
    public void RequireNamespace(string @namespace)
    {
        if (!HasNamespace(@namespace))
            throw new ArgumentException($"Namespace not found: {@namespace}.");
    }

    /// <summary>
    /// True if a type the assembly defines lies in <paramref name="namespace"/> or in one of its
    /// sub-namespaces.
    /// </summary>
    public bool HasNamespace(string @namespace)
    {
        var inside = @namespace + ".";
        return _typeSystem.MainModule.TypeDefinitions.Any(t =>
            t.Namespace == @namespace || t.Namespace.StartsWith(inside, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolve a fully-qualified type name (e.g., "System.String"). A nested type takes
    /// either separator: "System.Environment.SpecialFolder", the form ILens prints, or
    /// "System.Environment+SpecialFolder", the form .NET reflection prints. A generic type is
    /// named with its arity in any form <see cref="ParsedTypeName"/> reads: "System.Func&lt;,&gt;"
    /// as ILens prints it, "System.Func`2", or "System.Func&lt;int, bool&gt;". How a name
    /// without arity resolves is <see cref="FindTypes"/>'s rule; a name that still leaves
    /// several types is an error listing them. A type the assembly only references is an
    /// error naming the assembly it comes from.
    /// </summary>
    public ITypeDefinition ResolveType(string typeName)
    {
        var (matches, sameName) = FindTypes(typeName);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw NotDefined(typeName, sameName),
            _ => throw AmbiguousType(typeName, matches),
        };
    }

    /// <summary>
    /// <see cref="ResolveType"/> for a question the assembly can answer about a type it does
    /// not define, from its own references to it. Returns the definition when the assembly
    /// defines the type. Otherwise returns the one referenced type of that name, or, when the
    /// assembly neither defines nor references a type of that name in any arity, the name as
    /// written, since a question about it still has an answer: nothing here uses it. Throws
    /// where <see cref="ResolveType"/> lists candidates instead.
    /// </summary>
    public (ITypeDefinition Definition, ExternalTypeName External) ResolveTypeOrExternal(string typeName)
    {
        var (matches, sameName) = FindTypes(typeName);
        if (matches.Count == 1)
            return (matches[0], null);
        if (matches.Count > 1)
            throw AmbiguousType(typeName, matches);

        var (referenced, sameNameReferenced) = FindReferencedTypes(typeName);
        if (referenced.Count == 1)
        {
            var name = referenced[0].Name;
            return (null, new ExternalTypeName(ReferenceFormatter.FormatTypeName(name),
                candidate => candidate.ReflectionName == name.ReflectionName));
        }
        if (referenced.Count == 0 && sameName.Count == 0 && sameNameReferenced.Count == 0
            && ParsedTypeName.Parse(typeName) is { } parsed)
        {
            // The same rule FindTypes applies, for a name the scan meets outside the
            // reference table: a typeof argument, or a core type written as a type code.
            return (null, new ExternalTypeName(typeName.Trim(),
                candidate => FullNameOf(candidate) == parsed.BareName
                    && parsed.ArityAgrees(ParsedTypeName.SegmentArities(candidate))));
        }
        throw NotDefined(typeName, sameName);
    }

    /// <summary>
    /// The error for a name the assembly defines no type by. When the assembly references a
    /// type of that name, the error says where it comes from, which is where its definition
    /// can be read. Otherwise it lists the types sharing the name apart from arity.
    /// </summary>
    private ArgumentException NotDefined(string typeName, IReadOnlyList<ITypeDefinition> sameName)
    {
        var (referenced, sameNameReferenced) = FindReferencedTypes(typeName);
        if (referenced.Count == 1)
        {
            var name = ReferenceFormatter.FormatTypeName(referenced[0].Name);
            return OriginOf(referenced[0].Assemblies) is { } origin
                ? new($"{name} is not defined in this assembly; it comes from {origin}.")
                : new($"{name} is not defined in this assembly, which only references it.");
        }
        if (referenced.Count > 1)
            return new($"Type name '{typeName}' matches {referenced.Count} types this assembly " +
                $"references but does not define: {FormatReferencedList(referenced)}. Name one as listed.");

        var names = sameName.Select(ReferenceFormatter.FormatTypeName)
            .Concat(sameNameReferenced.Select(r => ReferenceFormatter.FormatTypeName(r.Name)))
            .ToList();
        return names.Count > 0
            ? new($"Type not found: {typeName}. Types of that name: {string.Join(", ", names)}.")
            : new($"Type not found: {typeName}.");
    }

    /// <summary>
    /// Where a referenced type comes from, as its references name it: one assembly, or each
    /// they name when they disagree. Null when they name no assembly.
    /// </summary>
    public static string OriginOf(IReadOnlyList<string> assemblies) =>
        assemblies.Count == 0 ? null : string.Join(" or ", assemblies);

    private static string FormatReferencedList(IEnumerable<ReferencedType> types) =>
        string.Join(", ", types.Select(t => ReferenceFormatter.FormatTypeName(t.Name)));

    /// <summary>
    /// <see cref="ResolveType"/> for a lookup where a missing type is a result rather than
    /// an error. Returns <c>null</c> when the assembly defines no type by that name. A name
    /// shared by several types still throws, because no single answer would be right.
    /// </summary>
    public ITypeDefinition TryResolveType(string typeName)
    {
        var (matches, _) = FindTypes(typeName);
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw AmbiguousType(typeName, matches),
        };
    }

    /// <summary>
    /// The types <paramref name="typeName"/> names, and every type sharing its name apart
    /// from arity. Where the name states no arity it prefers a non-generic type, the way C#
    /// and reflection read a name without type parameters: when several types match and
    /// exactly one is non-generic in every such segment, that one is the only match. So
    /// "System.Threading.Tasks.Task" is the plain Task, while "System.Func", which only
    /// generic types bear, matches all of them.
    /// </summary>
    public (IReadOnlyList<ITypeDefinition> Matches, IReadOnlyList<ITypeDefinition> SameName)
        FindTypes(string typeName) =>
        Match(typeName, _typesByFullName.Value, t => t.FullTypeName);

    /// <summary>
    /// <see cref="FindTypes"/> for the types the assembly references, by the same rule. Only
    /// meaningful for a name the assembly defines no type by, since a definition always wins.
    /// </summary>
    public (IReadOnlyList<ReferencedType> Matches, IReadOnlyList<ReferencedType> SameName)
        FindReferencedTypes(string typeName) =>
        Match(typeName, _referencedTypesByFullName.Value, r => r.Name);

    private static (IReadOnlyList<T> Matches, IReadOnlyList<T> SameName) Match<T>(
        string typeName, ILookup<string, T> byFullName, Func<T, FullTypeName> nameOf)
    {
        var parsed = ParsedTypeName.Parse(typeName);
        if (parsed == null)
        {
            // Not a name the parser reads; match it verbatim, as a FullName.
            var verbatim = byFullName[typeName].ToList();
            return (verbatim, verbatim);
        }

        var sameName = byFullName[parsed.BareName].ToList();
        var matches = sameName
            .Where(t => parsed.ArityAgrees(ParsedTypeName.SegmentArities(nameOf(t))))
            .ToList();
        if (matches.Count > 1)
        {
            var nonGeneric = matches
                .Where(t => parsed.NonGenericWhereUnstated(ParsedTypeName.SegmentArities(nameOf(t))))
                .ToList();
            if (nonGeneric.Count == 1)
                matches = nonGeneric;
        }
        return (matches, sameName);
    }

    /// <summary>
    /// Every type the assembly's reference rows name, one entry per name, skipping the rows
    /// <see cref="TryReadReference"/> rejects.
    /// </summary>
    private static ILookup<string, ReferencedType> ReadReferencedTypes(MetadataReader metadata)
    {
        var rows = new List<(FullTypeName Name, string Assembly)>();
        foreach (var handle in metadata.TypeReferences)
        {
            if (TryReadReference(metadata, handle, out var name, out var scope))
                rows.Add((name, AssemblyNameOf(metadata, scope)));
        }

        return rows
            .GroupBy(row => row.Name.ReflectionName, StringComparer.Ordinal)
            .Select(group => new ReferencedType(
                group.First().Name,
                group.Select(row => row.Assembly).OfType<string>().Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(type => type.Name.TypeParameterCount)
            .ToLookup(type => FullNameOf(type.Name), StringComparer.Ordinal);
    }

    /// <summary>
    /// The type a reference row names and the scope it resolves in. False for a row the
    /// metadata reader cannot decode, or whose chain of enclosing types never reaches a
    /// top-level reference: it names no type anyone could ask for, and is skipped, as the
    /// ILSpyX analyzers skip what they cannot decode.
    /// </summary>
    internal static bool TryReadReference(MetadataReader metadata, TypeReferenceHandle handle,
        out FullTypeName name, out EntityHandle scope)
    {
        name = default;
        scope = default;
        try
        {
            if (ResolutionScopeOf(metadata, handle) is not { } found)
                return false;
            name = handle.GetFullTypeName(metadata);
            scope = found;
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The scope a reference row resolves in, reached through the references to the types
    /// enclosing it, or null for a chain that does not end within <see cref="MaxNesting"/>
    /// steps. Valid metadata has no such chain, but an obfuscator can plant a cycle, which
    /// would send <c>GetFullTypeName</c> into unbounded recursion.
    /// </summary>
    private static EntityHandle? ResolutionScopeOf(MetadataReader metadata, TypeReferenceHandle handle)
    {
        EntityHandle scope = handle;
        for (var depth = 0; depth <= MaxNesting && scope.Kind == HandleKind.TypeReference && !scope.IsNil; depth++)
            scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        // Still a type reference: the chain ran too deep, or broke off at a null reference.
        return scope.Kind == HandleKind.TypeReference ? null : scope;
    }

    /// <summary>
    /// The assembly a reference scope names, as <c>Name, Version=…</c>, or null for a scope
    /// that names a module rather than an assembly, or an assembly row the metadata cannot
    /// read. Some builds of Harmony's own DLL from 2.3 on, 2.4.1's among them, have references
    /// that name rows past the end of their assembly table, left behind by the libraries merged
    /// into them.
    /// </summary>
    private static string AssemblyNameOf(MetadataReader metadata, EntityHandle scope)
    {
        if (scope.Kind != HandleKind.AssemblyReference || scope.IsNil)
            return null;
        try
        {
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
            return $"{metadata.GetString(reference.Name)}, Version={reference.Version}";
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the reference rows of <paramref name="metadata"/> say the type
    /// <paramref name="name"/> comes from, as <see cref="OriginOf"/> writes it. Null when no
    /// row names the type or none names an assembly.
    /// </summary>
    internal static string OriginOfReference(MetadataReader metadata, FullTypeName name)
    {
        var assemblies = new List<string>();
        foreach (var handle in metadata.TypeReferences)
        {
            if (TryReadReference(metadata, handle, out var referenced, out var scope)
                && referenced.ReflectionName == name.ReflectionName
                && AssemblyNameOf(metadata, scope) is { } assembly
                && !assemblies.Contains(assembly))
                assemblies.Add(assembly);
        }
        return OriginOf(assemblies);
    }

    /// <summary>
    /// A type's name as <c>ITypeDefinition.FullName</c> writes it, dotted and without arity.
    /// <c>FullTypeName.FullName</c> writes the same, except that it starts a type in the
    /// global namespace with a dot.
    /// </summary>
    private static string FullNameOf(FullTypeName name) =>
        string.IsNullOrEmpty(name.TopLevelTypeName.Namespace) ? name.FullName[1..] : name.FullName;

    private static ArgumentException AmbiguousType(
        string typeName, IReadOnlyList<ITypeDefinition> matches) =>
        new($"Type name '{typeName}' matches {matches.Count} types: {FormatTypeList(matches)}. " +
            "Name one as listed.");

    private static string FormatTypeList(IEnumerable<ITypeDefinition> types) =>
        string.Join(", ", types.Select(ReferenceFormatter.FormatTypeName));

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
            var hinted = TryFindMethod(type, memberName, parameterCount, parameterTypes, includeExplicit: true)
                ?? throw NotFound("Method", memberName, type, searchesExtensions: true);
            RequireOwn(type, hinted.Method, parameterCount, parameterTypes);
            return (hinted.Method, hinted.Origin, SymbolCategory.Method);
        }

        // No hint: probe every kind so a cross-kind name collision is caught. Explicit interface
        // implementations are probed only when no ordinary member of any kind has the name, as
        // C# binds the plain name to the ordinary member, so the two never collide.
        var matches = Probe(includeExplicit: false);
        if (matches.Count == 0)
            matches = Probe(includeExplicit: true);

        if (matches.Count == 1)
        {
            RequireOwn(type, (IEntity)matches[0].Symbol);
            return matches[0];
        }

        if (matches.Count == 0)
            throw NotFound("Member", memberName, type, searchesExtensions: true);

        // The ordinary C# event declaration compiles to an event *and* a private field of
        // the same name holding the subscriber list, so the cross-kind probe reports every
        // one of them as a collision. That is not the collision this check exists for: the
        // pair has a fixed compiler-generated shape, and only the event was ever written in
        // source. Resolve to the event — otherwise no field-like event is addressable by
        // name at all, which silently rules out the common case.
        var collapsed = CollapseFieldLikeEvent(matches);
        if (collapsed.HasValue)
        {
            RequireOwn(type, (IEntity)collapsed.Value.Item1);
            return collapsed.Value;
        }

        // More than one kind matched — the name collides across member kinds.
        var kinds = string.Join(", ", matches.Select(m => m.Category));
        throw new ArgumentException(
            $"Member '{memberName}' on {ReferenceFormatter.FormatTypeName(type)} is ambiguous — it exists as " +
            $"more than one member kind ({kinds}). Pass a {ParameterNames.ParameterCount} / " +
            $"{ParameterNames.ParameterTypes} hint if you mean the method; the type's decompiled code shows them all.");

        List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)> Probe(bool includeExplicit)
        {
            var found = new List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)>();

            var method = TryFindMethod(type, memberName, null, null, includeExplicit);
            if (method.HasValue)
                found.Add((method.Value.Method, method.Value.Origin, SymbolCategory.Method));

            var prop = TryFindProperty(type, memberName, includeExplicit);
            if (prop.HasValue)
                found.Add((prop.Value.Property, prop.Value.Origin, SymbolCategory.Property));

            var field = TryFindField(type, memberName);
            if (field.HasValue)
                found.Add((field.Value.Field, field.Value.Origin, SymbolCategory.Field));

            var evt = TryFindEvent(type, memberName, includeExplicit);
            if (evt.HasValue)
                found.Add((evt.Value.Event, evt.Value.Origin, SymbolCategory.Event));

            // A base class another assembly defines lies further from the type than any class
            // of the assembly's own, and nothing found on it can be answered here, so a name the
            // assembly declares as one kind is not ambiguous with another kind found there.
            var own = found.Where(match => !IsExternal((IEntity)match.Symbol)).ToList();
            return own.Count > 0 ? own : found;
        }
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
    /// ILens's type system never loads a referenced assembly, since ILSpyX builds it from the
    /// inspected assembly and a minimal core library, so a type from another assembly, unless
    /// it is one of the core types that library provides, comes back as a distinct unknown-type
    /// instance per resolution site, and the field's type and the event's delegate type compare
    /// unequal despite naming the same type. The name is what survives that, and it has to be
    /// <c>ReflectionName</c>: unlike
    /// <c>FullName</c> it carries the generic arity, which tells <c>Task</c> from
    /// <c>Task&lt;TResult&gt;</c>.
    /// </remarks>
    private static bool IsBackingFieldOf(IField field, IEvent evt)
    {
        var declaringType = evt.DeclaringTypeDefinition;
        return declaringType != null
            && field.DeclaringTypeDefinition?.ReflectionName == declaringType.ReflectionName
            && field.Name == evt.Name
            && field.Accessibility == Accessibility.Private
            && field.IsStatic == evt.IsStatic
            && field.Type.ReflectionName == evt.ReturnType.ReflectionName;
    }

    /// <summary>
    /// The declaration that introduced <paramref name="member"/>'s virtual slot: an override
    /// followed back through the declarations it overrides, and any other member itself. C#
    /// compiles a call to a virtual member as a call to that declaration, so <c>dog.Speak()</c>
    /// names <c>Animal.Speak</c> in the IL. The one exception is a call on a value of a core
    /// library type such as <c>int</c> to that type's own override, which names the override;
    /// call_sites on such an override, asked of the core library itself, lists the call and also
    /// reports it in a not-shown and a check line. When the result is itself an override, the
    /// type system could not resolve the declaration it overrides, as for one in an assembly
    /// ILens does not load.
    /// </summary>
    public static T SlotDeclarationOf<T>(T member) where T : class, IMember
    {
        var current = member.MemberDefinition;
        while (current.IsOverride && InheritanceHelper.GetBaseMember(current) is { } overridden)
            current = overridden.MemberDefinition;
        return (T)current;
    }

    /// <summary>
    /// Guard for method-overload disambiguation hints: when both a parameter count and an
    /// ordered parameter-type list are given, they must agree on arity. Used by every tool
    /// that accepts both hints, so the contradiction error message is identical across the
    /// surface.
    /// </summary>
    internal static void ValidateDisambiguationArgs(int? parameterCount, string[] parameterTypes)
    {
        if (parameterTypes != null && parameterCount.HasValue
            && parameterTypes.Length != parameterCount.Value)
        {
            throw new ArgumentException(
                $"{ParameterNames.ParameterCount} ({parameterCount.Value}) contradicts " +
                $"{ParameterNames.ParameterTypes}.Length ({parameterTypes.Length}).");
        }
    }

    // --- Method resolution -----------------------------------------------------

    /// <summary>
    /// Resolve a method by name, searching declared → inherited → extension methods, then
    /// explicit interface implementations. Returns the method and its origin relative to the
    /// requested type.
    /// </summary>
    public (IMethod Method, MemberOrigin Origin) ResolveMethod(
        ITypeDefinition type, string methodName, int? parameterCount = null,
        string[] parameterTypes = null)
    {
        ValidateDisambiguationArgs(parameterCount, parameterTypes);
        var found = TryFindMethod(type, methodName, parameterCount, parameterTypes, includeExplicit: true)
            ?? throw NotFound("Method", methodName, type, searchesExtensions: true);
        RequireOwn(type, found.Method, parameterCount, parameterTypes);
        return found;
    }

    /// <summary>
    /// <see cref="ResolveMethod"/> for a method the type may lack, as one of two builds of an
    /// assembly may: null where ResolveMethod throws because no method satisfies the request,
    /// including a hint that matches none of the overloads and an accessor its property or event
    /// does not have. Still throws where several methods satisfy it, and, as ResolveMethod does,
    /// for one inherited from a base class another assembly defines.
    /// </summary>
    public IMethod TryResolveMethod(
        ITypeDefinition type, string methodName, int? parameterCount, string[] parameterTypes)
    {
        ValidateDisambiguationArgs(parameterCount, parameterTypes);
        if (TryFindMethod(type, methodName, parameterCount, parameterTypes, includeExplicit: true,
                explainAbsence: false) is not { } found)
            return null;
        RequireOwn(type, found.Method, parameterCount, parameterTypes);
        return found.Method;
    }

    /// <summary>
    /// The overloads a question about every call to <paramref name="methodName"/> covers. With
    /// a hint, the one overload it selects, exactly as <see cref="ResolveMethod"/> resolves it.
    /// Without one, every overload on the most-derived type of the declared → base walk that
    /// declares the name, the same level the no-hint path of <see cref="ResolveMethod"/> picks;
    /// constructors and accessors are reachable by their IL names, <c>.ctor</c> and
    /// <c>get_X</c>. A name found at no level falls through to <see cref="ResolveMethod"/>,
    /// which finds an extension method or an explicit interface implementation of that name,
    /// or throws.
    /// </summary>
    public IReadOnlyList<IMethod> ResolveMethodOverloads(
        ITypeDefinition type, string methodName, int? parameterCount, string[] parameterTypes)
    {
        ValidateDisambiguationArgs(parameterCount, parameterTypes);
        if (parameterCount.HasValue || parameterTypes != null)
            return [ResolveMethod(type, methodName, parameterCount, parameterTypes).Method];

        foreach (var (level, _) in Levels(type))
        {
            var here = MethodsAndAccessors(level).Where(m => m.Name == methodName).ToList();
            if (here.Count > 0)
            {
                RequireOwn(type, here[0]);
                return here;
            }
        }

        return [ResolveMethod(type, methodName).Method];
    }

    /// <summary>
    /// The method a name and hint select, or null when none does. With
    /// <paramref name="explainAbsence"/>, a name the walk finds only with other overloads, or an
    /// accessor name whose property or event lacks that accessor, throws saying so instead.
    /// </summary>
    private (IMethod Method, MemberOrigin Origin)? TryFindMethod(
        ITypeDefinition type, string methodName, int? parameterCount, string[] parameterTypes,
        bool includeExplicit, bool explainAbsence = true)
    {
        var hinted = parameterCount.HasValue || parameterTypes != null;

        // 1-2. Walk the requested type, then its base-class chain (most-derived first).
        // C# overload resolution spans the inheritance chain: a base method of a different
        // signature stays a candidate even when the requested type declares a same-named one,
        // so `foo.Bar("x")` can bind to a base `Bar(string)` past a derived `Bar(int)`. A hint
        // must therefore keep walking past a level whose overloads don't satisfy it, rather
        // than stopping at the first level that merely has the name. The most-derived
        // satisfying level wins, so an override/new still resolves to the derived declaration.
        // Without a hint, the most-derived level that declares the name wins. `named`
        // accumulates every candidate across the chain so an ultimately unsatisfied hint can
        // report the whole set instead of just the requested type's. An accessor is a candidate
        // like any other method, so the hint tells an indexer's get_Item overloads apart as it
        // does any others.
        var levels = Levels(type).ToList();
        var named = new List<IMethod>();
        var ordinary = WalkLevels(m => m.Name == methodName, isExplicit: false);
        if (ordinary.HasValue)
            return ordinary;

        // 3. Extension methods, tried only after no instance overload satisfied the request —
        // the order C# uses, where extensions are a fallback for unresolved instance calls.
        var extensions = FindExtensionMethodsByName(type, methodName);
        if (extensions.Count > 0)
        {
            if (!hinted || NarrowByHint(extensions, parameterCount, parameterTypes).Count > 0)
            {
                var method = DisambiguateMethod(extensions, "(extension methods)",
                    methodName, parameterCount, parameterTypes);
                return (method, MemberOrigin.ExtensionOn(ReferenceFormatter.FormatTypeName(method.DeclaringType)));
            }
            named.AddRange(extensions);
        }

        // 4. Explicit interface implementations, found by the name of the interface member they
        // implement: System.IDisposable.Dispose by Dispose, and an accessor IShape.get_Name by
        // get_Name. C# never binds the plain name to one, so they come after everything above,
        // which keeps an ordinary GetEnumerator() reachable beside an explicit
        // IEnumerable.GetEnumerator() with the same parameters.
        if (includeExplicit)
        {
            var implementation = WalkLevels(m => ImplementsExplicitly(m, methodName), isExplicit: true);
            if (implementation.HasValue)
                return implementation;
        }

        if (!explainAbsence)
            return null;

        // A hint was given and the name exists somewhere on the chain (or as an extension),
        // but nothing satisfies it. Report the whole candidate set — the overload the caller
        // wanted may be declared on a base type, and naming it is the actionable result.
        if (hinted && named.Count > 0)
            throw new ArgumentException(
                $"No overload of '{methodName}' on {ReferenceFormatter.FormatTypeName(type)} matches " +
                $"{DescribeHint(parameterCount, parameterTypes)}. " +
                $"Available: {ReferenceFormatter.FormatMethodList(named)}{UnreadClause(_readExternalBases(type).Unread)}.");

        var missingAccessor = MissingAccessorError(type, methodName, includeExplicit);
        if (missingAccessor != null)
            throw missingAccessor;

        return null;

        // The candidates at the most-derived level that has any, or with a hint, at the
        // most-derived level where one satisfies it. Several satisfying candidates at one level
        // are an ambiguity that no base level can settle.
        (IMethod Method, MemberOrigin Origin)? WalkLevels(Func<IMethod, bool> isCandidate, bool isExplicit)
        {
            foreach (var (level, origin) in levels)
            {
                var here = MethodsAndAccessors(level).Where(isCandidate).ToList();
                if (here.Count == 0)
                    continue;

                named.AddRange(here);
                var satisfying = NarrowByHint(here, parameterCount, parameterTypes);
                // A level another assembly defines answers with its first candidate, and
                // RequireOwn names them all in its error.
                if (satisfying.Count > 0 && IsExternal(level))
                    return (satisfying[0], origin);
                if (satisfying.Count == 1)
                    return (satisfying[0], origin);
                if (satisfying.Count > 1)
                    return isExplicit
                        ? throw AmbiguousExplicitImplementationError(satisfying, level, methodName)
                        : (DisambiguateMethod(here, ReferenceFormatter.FormatTypeName(level), methodName,
                            parameterCount, parameterTypes), origin);
                // No candidate here satisfies the hint; a base level may still declare one.
            }
            return null;
        }
    }

    /// <summary>
    /// The requested type, then its base-class chain, most-derived first, each with the origin
    /// a member found there has relative to the requested type. The chain goes on past the base
    /// classes the assembly defines into those another assembly defines, as far as the
    /// assembly's references could be read. This assembly holds no code of a member found
    /// there, so <see cref="RequireOwn"/> turns it into an error naming where it comes from.
    /// Such a member still ends the search, as one on the assembly's own classes does, so an
    /// extension method or explicit implementation of the name is not taken in its place.
    /// </summary>
    private IEnumerable<(ITypeDefinition Level, MemberOrigin Origin)> Levels(ITypeDefinition type)
    {
        yield return (type, MemberOrigin.Declared);
        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
            yield return (baseType, MemberOrigin.InheritedFrom(ReferenceFormatter.FormatTypeName(baseType)));
        foreach (var level in _readExternalBases(type).Levels.Select(baseType => baseType.GetDefinition()))
            yield return (level, MemberOrigin.InheritedFrom(ReferenceFormatter.FormatTypeName(level)));
    }

    /// <summary>
    /// True for a member or type of an assembly other than the one this resolver reads, which
    /// <see cref="Levels"/> reaches on a base class another assembly defines.
    /// </summary>
    private bool IsExternal(IEntity entity) => entity.ParentModule != _typeSystem.MainModule;

    /// <summary>
    /// Throws when <paramref name="found"/> lies on a base class another assembly defines. This
    /// assembly holds neither its code nor anything about it beyond the calls it makes, so the
    /// error names it, every member of that name the hint leaves on its class, and the file the
    /// class comes from, where its code can be read.
    /// </summary>
    private void RequireOwn(ITypeDefinition type, IEntity found, int? parameterCount = null, string[] parameterTypes = null)
    {
        if (!IsExternal(found))
            return;

        var declaringType = found.DeclaringTypeDefinition;
        IEnumerable<IEntity> named = found is IMethod
            ? NarrowByHint(MethodsAndAccessors(declaringType).Where(m => m.Name == found.Name).ToList(),
                parameterCount, parameterTypes)
            : [found];
        var file = (declaringType.ParentModule as MetadataModule)?.MetadataFile.FileName;
        throw new ArgumentException(
            $"{ReferenceFormatter.FormatTypeName(type)} inherits " +
            $"{string.Join(", ", named.Select(ReferenceFormatter.FormatSymbol))} from a base class this " +
            $"assembly does not define; {ReferenceFormatter.FormatTypeName(declaringType)} comes from {file}.");
    }

    /// <summary>
    /// The error for a name the search did not find. When the walk up the base classes stopped
    /// at one ILens could not read, the error says so, since that class may declare the name.
    /// </summary>
    private ArgumentException NotFound(string kind, string name, ITypeDefinition type, bool searchesExtensions)
    {
        var unread = _readExternalBases(type).Unread;
        var bases = unread is null ? "its base types" : "the base types ILens could load";
        var searched = searchesExtensions
            ? $"{ReferenceFormatter.FormatTypeName(type)}, {bases}, or as an extension method"
            : $"{ReferenceFormatter.FormatTypeName(type)} or {bases}";
        return new ArgumentException($"{kind} '{name}' not found on {searched}{UnreadClause(unread)}.");
    }

    /// <summary>
    /// The clause that ends an error when the walk up the base classes stopped at
    /// <paramref name="unread"/>, which ILens could not read, and nothing when it did not stop.
    /// </summary>
    private static string UnreadClause(UnreadBase unread) =>
        unread is null
            ? ""
            : $"; {unread.TypeName} and its base types were not searched, since ILens could not load {unread.Assembly}";

    /// <summary>
    /// The methods a type declares, together with the accessors of its properties and events,
    /// which ILSpy does not list among its methods. An accessor is named and resolved like any
    /// other method, <c>get_Item</c> for an indexer. find_methods hides accessors, so resolving
    /// one by name is the direct route to a single accessor body.
    /// </summary>
    /// <remarks>
    /// Built here rather than with ILSpy's <c>GetAccessors</c>, which yields an event's remove
    /// accessor a second time in place of its raise accessor.
    /// </remarks>
    private static IEnumerable<IMethod> MethodsAndAccessors(ITypeDefinition type) =>
        type.Methods
            .Concat(type.Properties.SelectMany(p => new[] { p.Getter, p.Setter }))
            .Concat(type.Events.SelectMany(e => new[] { e.AddAccessor, e.RemoveAccessor, e.InvokeAccessor }))
            .Where(method => method != null);

    /// <summary>
    /// The error for an accessor name whose property or event exists without that accessor,
    /// such as <c>set_Count</c> on a read-only <c>Count</c>, which says more than that no such
    /// method exists. <c>null</c> for any other name. An explicit implementation's accessor
    /// carries the interface in front of the prefix, <c>IShape.set_Name</c>, as its property
    /// carries it in front of the name, <c>IShape.Name</c>. It is asked only once no method of
    /// that name exists, so every property or event of the name lacks the accessor, and the
    /// first one found names the case.
    /// </summary>
    private ArgumentException MissingAccessorError(ITypeDefinition type, string methodName, bool includeExplicit)
    {
        var qualifier = methodName[..(methodName.LastIndexOf('.') + 1)];
        var (prefix, baseName) = SplitAccessorPrefix(methodName[qualifier.Length..]);
        var owner = qualifier + baseName;
        if (prefix is "get_" or "set_" && FirstNamed(type, level => level.Properties, owner, includeExplicit) is { } property)
            return new ArgumentException(
                $"Property '{property.Name}' on {ReferenceFormatter.FormatTypeName(property.DeclaringType)} " +
                $"has no {(prefix == "get_" ? "getter" : "setter")}.");
        if (prefix is "add_" or "remove_" && FirstNamed(type, level => level.Events, owner, includeExplicit) is { } evt)
            return new ArgumentException(
                $"Event '{evt.Name}' on {ReferenceFormatter.FormatTypeName(evt.DeclaringType)} " +
                $"has no {(prefix == "add_" ? "add" : "remove")} accessor.");
        return null;
    }

    /// <summary>
    /// The first member named <paramref name="name"/> on the type or its base types, or, with
    /// <paramref name="includeExplicit"/> and failing that, the first that implements an
    /// interface member of that name explicitly.
    /// </summary>
    private T FirstNamed<T>(ITypeDefinition type, Func<ITypeDefinition, IEnumerable<T>> membersOf,
        string name, bool includeExplicit) where T : class, IMember
    {
        var members = Levels(type).SelectMany(level => membersOf(level.Level)).ToList();
        return members.FirstOrDefault(member => member.Name == name)
            ?? (includeExplicit ? members.FirstOrDefault(member => ImplementsExplicitly(member, name)) : null);
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
                ? $"they are same-arity overloads — specify {ParameterNames.ParameterTypes} to disambiguate"
                : $"specify {ParameterNames.ParameterCount} or {ParameterNames.ParameterTypes} to disambiguate";
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
    /// True if the member's parameters, a method's or an indexer's, match the ordered
    /// <paramref name="patterns"/> position for position (arity included). Same loose matching
    /// as <c>find_methods</c>.
    /// </summary>
    public static bool MatchesParameterTypes(IParameterizedMember member, IReadOnlyList<string> patterns)
    {
        if (member.Parameters.Count != patterns.Count)
            return false;
        for (int i = 0; i < patterns.Count; i++)
            if (!TypeMatcher.Matches(member.Parameters[i].Type, patterns[i]))
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
        // GetAllBaseTypes includes the type itself plus every supertype — classes,
        // interfaces, and System.Object — so 'this IEnumerable<T>' extensions resolve on a
        // concrete List<T> and 'this object' extensions resolve on any reference type.
        // TypeWalker.WalkBaseTypes (used for instance-member resolution) intentionally stops
        // before Object and skips interfaces; extension-method discovery needs the wider scope.
        // A type another assembly defines has no definition here, only the name its reference
        // gives it, and the base classes such a type has are read from the assembly's
        // references. Types compare by ReflectionName, which unlike FullName tells 'this Task'
        // from 'this Task<TResult>'.
        var targetTypes = new HashSet<string>(targetType.GetAllBaseTypes()
            .Concat(_readExternalBases(targetType).Levels.SelectMany(level => level.GetAllBaseTypes()))
            .Select(t => t.GetDefinitionOrUnknown()?.ReflectionName)
            .OfType<string>());

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

                var firstParamType = method.Parameters[0].Type.GetDefinitionOrUnknown();
                if (firstParamType != null && targetTypes.Contains(firstParamType.ReflectionName))
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
        var found = TryFindField(type, fieldName)
            ?? throw NotFound("Field", fieldName, type, searchesExtensions: false);
        RequireOwn(type, found.Field);
        return found;
    }

    private (IField Field, MemberOrigin Origin)? TryFindField(
        ITypeDefinition type, string fieldName)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var field = level.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field != null)
                return (field, origin);
        }

        return null;
    }

    // --- Property resolution ---------------------------------------------------

    /// <summary>
    /// Resolve a property by name, searching declared → inherited, then explicit interface
    /// implementations. Throws when none has the name, and when the level that has it has
    /// several, see <see cref="AmbiguousPropertyError"/>.
    /// </summary>
    public (IProperty Property, MemberOrigin Origin) ResolveProperty(
        ITypeDefinition type, string propertyName)
    {
        var found = TryFindProperty(type, propertyName, includeExplicit: true)
            ?? throw NotFound("Property", propertyName, type, searchesExtensions: false);
        RequireOwn(type, found.Property);
        return found;
    }

    /// <summary>
    /// The error for a level with more than one property of the name. The only case in C# is
    /// an indexer with several overloads, which all share one name, <c>Item</c> unless
    /// <c>[IndexerName]</c> gives another, and taking the first would answer for the wrong one.
    /// Their accessors tell them apart by parameter types.
    /// </summary>
    private static ArgumentException AmbiguousPropertyError(
        List<IProperty> candidates, string context, string propertyName)
    {
        var rendered = string.Join(", ", candidates.Select(ReferenceFormatter.FormatSymbol));
        return new ArgumentException(
            $"Property '{propertyName}' on {context} has {candidates.Count} overloads — " +
            "in C# this is always an indexer, but non-C# metadata or obfuscation can " +
            "produce non-indexer collisions too. To choose one, name its accessor instead, " +
            $"get_{propertyName} or set_{propertyName}, with {ParameterNames.ParameterTypes}. Candidates: {rendered}.");
    }

    /// <summary>
    /// The property of that name at the most-derived level of the declared → base walk that
    /// has one, then, with <paramref name="includeExplicit"/>, an explicit implementation of
    /// that name. Throws when that level has several.
    /// </summary>
    private (IProperty Property, MemberOrigin Origin)? TryFindProperty(
        ITypeDefinition type, string propertyName, bool includeExplicit)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = level.Properties.Where(p => p.Name == propertyName).ToList();
            if (here.Count > 0 && IsExternal(level))
                return (here[0], origin);
            if (here.Count > 1)
                throw AmbiguousPropertyError(here, ReferenceFormatter.FormatTypeName(level), propertyName);
            if (here.Count == 1)
                return (here[0], origin);
        }

        return includeExplicit ? FindExplicitImplementation(type, level => level.Properties, propertyName) : null;
    }

    // --- Event resolution ------------------------------------------------------

    /// <summary>
    /// Resolve an event by name, searching declared → inherited, then explicit interface
    /// implementations. Throws when none has the name, and when the level that has it has
    /// several, see <see cref="AmbiguousEventError"/>.
    /// </summary>
    public (IEvent Event, MemberOrigin Origin) ResolveEvent(
        ITypeDefinition type, string eventName)
    {
        var found = TryFindEvent(type, eventName, includeExplicit: true)
            ?? throw NotFound("Event", eventName, type, searchesExtensions: false);
        RequireOwn(type, found.Event);
        return found;
    }

    /// <summary>
    /// The error for a level with more than one event of the name, the counterpart of
    /// <see cref="AmbiguousPropertyError"/>. C# does not let events overload, so it fires only
    /// on metadata produced by other source languages or obfuscators.
    /// </summary>
    private static ArgumentException AmbiguousEventError(
        List<IEvent> candidates, string context, string eventName)
    {
        var rendered = string.Join(", ",
            candidates.Select(e => $"{ReferenceFormatter.FormatTypeName(e.DeclaringType)}.{e.Name}"));
        return new ArgumentException(
            $"Event '{eventName}' on {context} has {candidates.Count} entries with " +
            "the same name — non-C# metadata. Candidates: " + rendered + ".");
    }

    /// <summary>
    /// The event of that name at the most-derived level of the declared → base walk that has
    /// one, then, with <paramref name="includeExplicit"/>, an explicit implementation of that
    /// name. Throws when that level has several.
    /// </summary>
    private (IEvent Event, MemberOrigin Origin)? TryFindEvent(
        ITypeDefinition type, string eventName, bool includeExplicit)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = level.Events.Where(e => e.Name == eventName).ToList();
            if (here.Count > 0 && IsExternal(level))
                return (here[0], origin);
            if (here.Count > 1)
                throw AmbiguousEventError(here, ReferenceFormatter.FormatTypeName(level), eventName);
            if (here.Count == 1)
                return (here[0], origin);
        }

        return includeExplicit ? FindExplicitImplementation(type, level => level.Events, eventName) : null;
    }

    // --- Explicit interface implementations ------------------------------------

    /// <summary>
    /// True if <paramref name="member"/> implements an interface member named
    /// <paramref name="name"/> explicitly. C# names such a member after the interface as well,
    /// <c>System.IDisposable.Dispose</c>, so a lookup by the plain name never reaches it.
    /// </summary>
    private static bool ImplementsExplicitly(IMember member, string name) =>
        member.IsExplicitInterfaceImplementation
        && member.Name.EndsWith("." + name, StringComparison.Ordinal);

    /// <summary>
    /// The member the most-derived level of the declared → base walk implements explicitly for
    /// an interface member named <paramref name="name"/>, with its origin, or <c>null</c> when
    /// no level has one.
    /// </summary>
    private (T Member, MemberOrigin Origin)? FindExplicitImplementation<T>(
        ITypeDefinition type, Func<ITypeDefinition, IEnumerable<T>> membersOf, string name)
        where T : IMember
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = membersOf(level).Where(m => ImplementsExplicitly(m, name)).ToList();
            if (here.Count > 0 && IsExternal(level))
                return (here[0], origin);
            if (here.Count == 1)
                return (here[0], origin);
            if (here.Count > 1)
                throw AmbiguousExplicitImplementationError(here.Cast<IMember>(), level, name);
        }
        return null;
    }

    /// <summary>
    /// The error for a level that implements several interface members of one name
    /// explicitly, such as a collection implementing both <c>GetEnumerator</c> methods that
    /// way. Only their full names tell them apart.
    /// </summary>
    private static ArgumentException AmbiguousExplicitImplementationError(
        IEnumerable<IMember> candidates, ITypeDefinition level, string name)
    {
        var listed = candidates.Select(member => member is IMethod method
            ? $"{method.Name}({string.Join(", ", method.Parameters.Select(ReferenceFormatter.FormatParameterType))})"
            : member.Name).ToList();
        return new ArgumentException(
            $"'{name}' on {ReferenceFormatter.FormatTypeName(level)} names {listed.Count} explicit interface " +
            $"implementations: {string.Join(", ", listed)}. Pass the full name of one instead.");
    }
}
