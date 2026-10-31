using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Resolves user-provided fully-qualified names to ILSpy type system symbols, and names a
/// type the assembly only references by the assembly it comes from, and one it forwards by the
/// assembly it goes to.
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

    /// <summary>
    /// A type another assembly defines, as a type system holding the assembly's references reads
    /// it, or null where none does; see <see cref="AssemblyHost.FindExternalType"/>.
    /// </summary>
    private readonly Func<IReadOnlyList<FullTypeName>, ITypeDefinition> _readExternalType;

    /// <summary>
    /// The libraries the assembly's references or its forward name for a type that was not read,
    /// as the reference search left them; see <see cref="AssemblyHost.UnreadLibrariesOf"/>.
    /// </summary>
    private readonly Func<FullTypeName, IReadOnlyList<UnreadLibrary>> _unreadLibrariesOf;

    /// <summary>
    /// The members of a type the assembly only references, as its references name them; see
    /// <see cref="AssemblyHost.ReferencedMembersOf"/>.
    /// </summary>
    private readonly Func<ExternalTypeName, (IReadOnlyList<TypeUsageScanner.ReferencedMember> Members,
        IReadOnlyList<string> SetByName)> _referencedMembersOf;

    /// <summary>
    /// Whether code can call an extension method on a type, by C#'s rule; see
    /// <see cref="AssemblyHost.FitsExtension"/>.
    /// </summary>
    private readonly Func<IType, IMethod, bool> _fitsExtension;

    /// <summary>
    /// The decompiler's type system, which holds the libraries the reference search found and in
    /// which an array type a question names is built (<see cref="ReadArray"/>).
    /// </summary>
    private readonly Func<ICompilation> _decompilerTypeSystem;

    /// <summary>The assembly's types by <c>FullName</c>, which same-named types share.</summary>
    private readonly Lazy<ILookup<string, ITypeDefinition>> _typesByFullName;

    /// <summary>
    /// The types the assembly references, keyed like <see cref="_typesByFullName"/>, and read
    /// beside the types the assembly defines by <see cref="Lookup"/>.
    /// </summary>
    private readonly Lazy<ILookup<string, ReferencedType>> _referencedTypesByFullName;

    /// <summary>
    /// The types the assembly forwards to another assembly, by reflection name; see
    /// <see cref="ReadForwardedTypes"/>.
    /// </summary>
    private readonly Lazy<IReadOnlyDictionary<string, ForwardedType>> _forwardedTypes;

    /// <summary>
    /// <see cref="_forwardedTypes"/> keyed like <see cref="_typesByFullName"/>, and read beside the
    /// types the assembly defines and references by <see cref="Lookup"/>.
    /// </summary>
    private readonly Lazy<ILookup<string, ForwardedType>> _forwardedTypesByFullName;

    /// <summary>
    /// The types the assembly defines by their own name, the last segment of their name without
    /// its count of type parameters, read beside <see cref="_referencedTypesByOwnName"/> and
    /// <see cref="_forwardedTypesByOwnName"/> for the types a name that finds none may mean
    /// (<see cref="NamedLike"/>).
    /// </summary>
    private readonly Lazy<ILookup<string, ITypeDefinition>> _typesByOwnName;

    /// <summary>The types the assembly references, keyed like <see cref="_typesByOwnName"/>.</summary>
    private readonly Lazy<ILookup<string, ReferencedType>> _referencedTypesByOwnName;

    /// <summary>The types the assembly forwards, keyed like <see cref="_typesByOwnName"/>.</summary>
    private readonly Lazy<ILookup<string, ForwardedType>> _forwardedTypesByOwnName;

    /// <summary>The rows of the assembly's extension methods; see <see cref="ReadExtensionMethods"/>.</summary>
    private readonly Lazy<IReadOnlyList<MethodDefinitionHandle>> _extensionMethods;

    /// <summary><see cref="_extensionMethods"/> by name.</summary>
    private readonly Lazy<ILookup<string, MethodDefinitionHandle>> _extensionMethodsByName;

    public SymbolResolver(ICompilation typeSystem, Func<ITypeDefinition, ExternalBases> readExternalBases,
        Func<IReadOnlyList<FullTypeName>, ITypeDefinition> readExternalType,
        Func<FullTypeName, IReadOnlyList<UnreadLibrary>> unreadLibrariesOf,
        Func<ExternalTypeName, (IReadOnlyList<TypeUsageScanner.ReferencedMember>, IReadOnlyList<string>)> referencedMembersOf,
        Func<IType, IMethod, bool> fitsExtension, Func<ICompilation> decompilerTypeSystem)
    {
        _typeSystem = typeSystem;
        _readExternalBases = readExternalBases;
        _readExternalType = readExternalType;
        _unreadLibrariesOf = unreadLibrariesOf;
        _referencedMembersOf = referencedMembersOf;
        _fitsExtension = fitsExtension;
        _decompilerTypeSystem = decompilerTypeSystem;
        _typesByFullName = new(() => typeSystem.MainModule.TypeDefinitions
            .OrderBy(t => t.TypeParameterCount)
            .ToLookup(t => t.FullName, StringComparer.Ordinal));
        _referencedTypesByFullName = new(() => ReadReferencedTypes(
            ((MetadataModule)typeSystem.MainModule).MetadataFile.Metadata));
        _forwardedTypes = new(() => ReadForwardedTypes(
            ((MetadataModule)typeSystem.MainModule).MetadataFile.Metadata));
        _forwardedTypesByFullName = new(() => _forwardedTypes.Value.Values
            .OrderBy(type => type.Name.TypeParameterCount)
            .ToLookup(type => FullNameOf(type.Name), StringComparer.Ordinal));
        _typesByOwnName = new(() => typeSystem.MainModule.TypeDefinitions
            .ToLookup(t => t.Name, StringComparer.Ordinal));
        _referencedTypesByOwnName = new(() => _referencedTypesByFullName.Value
            .SelectMany(types => types)
            .ToLookup(type => type.Name.Name, StringComparer.Ordinal));
        _forwardedTypesByOwnName = new(() => _forwardedTypes.Value.Values
            .ToLookup(type => type.Name.Name, StringComparer.Ordinal));
        _extensionMethods = new(() => ReadExtensionMethods((MetadataModule)typeSystem.MainModule));
        _extensionMethodsByName = new(() =>
        {
            var metadata = ((MetadataModule)typeSystem.MainModule).MetadataFile.Metadata;
            return _extensionMethods.Value.ToLookup(
                handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name), StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// The rows of the assembly's extension methods, in the order of the types and their
    /// methods; see <see cref="ReadExtensionMethods"/>.
    /// </summary>
    public IReadOnlyList<MethodDefinitionHandle> ExtensionMethods => _extensionMethods.Value;

    /// <summary>
    /// A type the assembly uses and another assembly defines, known only by the assembly's
    /// references to it. <paramref name="Assemblies"/> are the assemblies those references
    /// name, as <c>Name, Version=…</c>. There is more than one only when the references
    /// disagree, and none when they name a module rather than an assembly, or an assembly row
    /// the metadata cannot read.
    /// </summary>
    public sealed record ReferencedType(FullTypeName Name, IReadOnlyList<string> Assemblies);

    /// <summary>
    /// A type the assembly forwards to another assembly, known only by its exported-type row.
    /// <paramref name="Target"/> is that assembly, as <c>Name, Version=…</c>.
    /// </summary>
    private sealed record ForwardedType(FullTypeName Name, string Target);

    /// <summary>
    /// Where the assembly's metadata says a type it does not define lies (<see cref="WhereElse"/>):
    /// <paramref name="Assembly"/>, as <c>Name, Version=…</c>, is the assembly it forwards the type
    /// to where <paramref name="Forwarded"/> is true, and otherwise those its references say the
    /// type comes from, as <see cref="OriginOf"/> writes them.
    /// </summary>
    public sealed record Elsewhere(string Assembly, bool Forwarded);

    /// <summary>
    /// A type an analyze question names that the assembly does not define: how to print it,
    /// which of the names the assembly's metadata uses for types denote it, the names to look
    /// it up by in the assemblies the decompiler loads, and where the assembly's references say
    /// it comes from, as <see cref="OriginOf"/> writes it. <paramref name="LookupNames"/> holds
    /// the name a reference or a forward gives the type, or for a type nothing names, each
    /// reading of the name as written. <paramref name="Origin"/> is null when no reference names
    /// the type or none names an assembly. <paramref name="Forward"/> is the assembly the
    /// assembly forwards the type to, as <c>Name, Version=…</c>, where it forwards a type it does
    /// not reference; null otherwise.
    /// </summary>
    public sealed record ExternalTypeName(
        string Display, Func<FullTypeName, bool> Matches, IReadOnlyList<FullTypeName> LookupNames, string Origin,
        string Forward = null);

    /// <summary>
    /// Describes where a resolved member was found relative to the requested type, with the type
    /// its mark names: the base class that declares an inherited member, and the type an extension
    /// method extends, as C# means an extension method on a type, or a type parameter with its
    /// constraints (<see cref="ReferenceFormatter.FormatExtendedType"/>).
    /// </summary>
    public readonly record struct MemberOrigin(string Kind, string TypeName = "")
    {
        public static MemberOrigin Declared => new("declared");

        public static MemberOrigin InheritedFrom(string typeName) =>
            new("inherited", typeName);

        public static MemberOrigin ExtensionOn(string typeName) =>
            new("extension", typeName);

        public string Format() => Kind switch
        {
            "inherited" => $"[inherited from {TypeName}]",
            "extension" => $"[extension method on {TypeName}]",
            _ => "[declared]"
        };
    }

    /// <summary>
    /// What a caller gave to select one method among those of its name: the number of its
    /// parameters, patterns for their types in order, and a pattern for its return type, each
    /// of which may be left out. Patterns match by <see cref="TypeMatcher"/>'s rules, and a
    /// search holds them as an exact fit first.
    /// </summary>
    public sealed record MethodHint(int? ParameterCount, string[] ParameterTypes, string ReturnType)
    {
        /// <summary>No hint: every method of the name is a candidate.</summary>
        public static MethodHint None { get; } = new(null, null, null);

        /// <summary>True when the hint holds anything to select by.</summary>
        public bool IsGiven => ParameterCount.HasValue || ParameterTypes != null || !string.IsNullOrEmpty(ReturnType);

        /// <summary>
        /// True when the hint holds a pattern, which an exact fit can read otherwise than a loose
        /// one; a count of parameters reads alike in both.
        /// </summary>
        public bool HasPatterns => ParameterTypes != null || !string.IsNullOrEmpty(ReturnType);

        /// <summary>
        /// True if a method with these parameter types and this return type satisfies the hint:
        /// its parameters match the type patterns position for position, or else it has the
        /// stated number of them, and its return type matches the return-type pattern. A return
        /// type that is not known, as for a row that could not be read, satisfies no pattern.
        /// </summary>
        public bool Fits(IReadOnlyList<IType> parameterTypes, IType returnType, bool exact) =>
            (ParameterTypes != null
                ? parameterTypes.Count == ParameterTypes.Length
                  && parameterTypes.Zip(ParameterTypes).All(pair => TypeMatcher.Matches(pair.First, pair.Second, exact))
                : !ParameterCount.HasValue || parameterTypes.Count == ParameterCount.Value)
            && (string.IsNullOrEmpty(ReturnType)
                || (returnType != null && TypeMatcher.Matches(returnType, ReturnType, exact)));

        /// <inheritdoc cref="Fits(IReadOnlyList{IType}, IType, bool)"/>
        public bool Fits(IMethod method, bool exact) =>
            Fits(method.Parameters.Select(parameter => parameter.Type).ToList(), method.ReturnType, exact);

        /// <summary>
        /// The hint as an error names it: <c>parameter types (string, int)</c> or
        /// <c>2 parameter(s)</c>, and <c>return type int</c>, joined by "and".
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>(2);
            if (ParameterTypes != null)
                parts.Add($"parameter types ({string.Join(", ", ParameterTypes)})");
            else if (ParameterCount.HasValue)
                parts.Add($"{ParameterCount.Value} parameter(s)");
            if (!string.IsNullOrEmpty(ReturnType))
                parts.Add($"return type {ReturnType}");
            return string.Join(" and ", parts);
        }
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
    /// without arity resolves is <see cref="Lookup"/>'s rule; a name that still leaves
    /// several types is an error listing them. A type the assembly only references is an
    /// error naming the assembly it comes from, and one it forwards an error naming the assembly
    /// it forwards the type to.
    /// </summary>
    public ITypeDefinition ResolveType(string typeName)
    {
        var lookup = Lookup(typeName);
        if (lookup.Count > 1)
            throw AmbiguousType(typeName, lookup);
        return lookup.Defined.Count == 1 ? lookup.Defined[0] : throw NotDefined(typeName, lookup);
    }

    /// <summary>
    /// <see cref="ResolveType"/> for a question the assembly can answer about a type it does
    /// not define, from its own references to it. Returns the definition when the name means a
    /// type the assembly defines. Otherwise returns the one referenced or forwarded type it means,
    /// or, when the assembly neither defines, references nor forwards a type of that name in any
    /// arity, the name as written, since a question about it still has an answer: nothing here
    /// names it. Throws where <see cref="ResolveType"/> lists candidates instead.
    /// </summary>
    public (ITypeDefinition Definition, ExternalTypeName External) ResolveTypeOrExternal(string typeName)
    {
        var lookup = Lookup(typeName);
        if (lookup.Count > 1)
            throw AmbiguousType(typeName, lookup);
        if (lookup.Defined.Count == 1)
            return (lookup.Defined[0], null);
        return (null, ExternalNamed(typeName, lookup) ?? throw NotDefined(typeName, lookup));
    }

    /// <summary>
    /// The type <see cref="ResolveTypeOrExternal"/> finds for a name that means no type the
    /// assembly defines, or null where it means one or that method throws, for a lookup where
    /// finding nothing is a result.
    /// </summary>
    public ExternalTypeName TryResolveExternal(string typeName)
    {
        var lookup = Lookup(typeName);
        return lookup.Defined.Count == 0 ? ExternalNamed(typeName, lookup) : null;
    }

    /// <summary>
    /// The one referenced or forwarded type <paramref name="lookup"/> found, or, where the
    /// assembly neither defines, references nor forwards a type of that name in any arity, the
    /// name as written; null otherwise.
    /// </summary>
    private ExternalTypeName ExternalNamed(string typeName, TypeLookup lookup)
    {
        if (lookup.Count == 1 && lookup.Referenced.Count == 1)
        {
            var name = lookup.Referenced[0].Name;
            return new ExternalTypeName(ReferenceFormatter.FormatTypeName(name),
                candidate => candidate.ReflectionName == name.ReflectionName,
                [name], OriginOf(lookup.Referenced[0].Assemblies));
        }
        if (lookup.Count == 1 && lookup.Forwarded.Count == 1)
        {
            var (name, target) = lookup.Forwarded[0];
            return new ExternalTypeName(ReferenceFormatter.FormatTypeName(name),
                candidate => candidate.ReflectionName == name.ReflectionName, [name], Origin: null, Forward: target);
        }
        // An array type is no name the scan meets, and only some questions take one, see NamesArray.
        if (lookup.Count == 0 && lookup.SameNameDefined.Count == 0 && lookup.SameNameReferenced.Count == 0
            && lookup.SameNameForwarded.Count == 0 && lookup.Parsed is { } parsed && !EndsInArrayBrackets(typeName))
        {
            // The same rule Lookup applies, for a name the scan meets outside the
            // reference table: a typeof argument, or a core type written as a type code.
            return new ExternalTypeName(typeName.Trim(),
                candidate => FullNameOf(candidate) == parsed.BareName
                    && parsed.ArityAgrees(ParsedTypeName.SegmentArities(candidate)),
                Readings(parsed), null);
        }
        return null;
    }

    /// <summary>
    /// The error for a name that means no type the assembly defines. When it means a type the
    /// assembly forwards, the error says where the assembly forwards it, and when it means one the
    /// assembly only references, where that comes from: either is where its definition can be
    /// read. An array type gets the error that names the questions taking one
    /// (<see cref="ArrayNotTaken"/>). Otherwise it names the types the caller may have meant
    /// (<see cref="Meant(TypeLookup, string)"/>).
    /// </summary>
    private ArgumentException NotDefined(string typeName, TypeLookup lookup)
    {
        if (lookup.Count == 0 && EndsInArrayBrackets(typeName))
            return ArrayNotTaken(typeName);
        if (lookup.Referenced.Count == 1 || lookup.Forwarded.Count == 1)
        {
            var type = lookup.Referenced.Count == 1 ? lookup.Referenced[0].Name : lookup.Forwarded[0].Name;
            var name = ReferenceFormatter.FormatTypeName(type);
            // A forward is the assembly's own record of where the type went, so it comes before
            // what a reference row to the type says.
            var message = _forwardedTypes.Value.TryGetValue(type.ReflectionName, out var forward)
                ? $"{name} is not defined in this assembly, which forwards it to {forward.Target}."
                : OriginOf(lookup.Referenced[0].Assemblies) is { } origin
                    ? $"{name} is not defined in this assembly; it comes from {origin}."
                    : $"{name} is not defined in this assembly, which only references it.";
            // The assembly's own types of the name are all generic, or one of them would be
            // meant, and a caller may have left out the type parameters of the one they meant.
            return lookup.Parsed is { HasArity: false } && lookup.SameNameDefined.Count > 0
                ? new($"{message} This assembly defines {FormatTypeList(lookup.SameNameDefined)}.")
                : new(message);
        }

        return Meant(lookup, ThisAssembly) is { } meant
            ? new($"Type not found: {typeName}. Types {meant}.")
            : new($"Type not found: {typeName}.");
    }

    /// <summary>What an answer about one assembly calls it where it names types in groups.</summary>
    private const string ThisAssembly = "this assembly";

    /// <summary>
    /// How many types <see cref="NamedLike"/> names at most. Every group of types sharing an own
    /// name the compiler did not generate, counted in a game's main library, its three largest
    /// mods and five libraries of .NET 10, fits whole, the largest being the 31 classes named X64
    /// nested in .NET 10's core library. The largest generated group, the game library's 1,286
    /// classes named <c>&lt;&gt;c</c>, would take about 47,000 characters, its first 40 about
    /// 1,300.
    /// </summary>
    private const int NamedLikeShown = 40;

    /// <summary>
    /// <see cref="Meant(TypeLookup, string)"/> for <paramref name="typeName"/>, for an answer
    /// written outside this class about a name that finds no type: analyze's answer about a
    /// type nothing in the assembly names, and compare's error for a type neither build has.
    /// </summary>
    public string Meant(string typeName, string subject = ThisAssembly) => Meant(Lookup(typeName), subject);

    /// <summary>
    /// The types a name that finds no type may have meant, as an answer writes them after
    /// "Types": those that share its full name apart from arity, as <c>of that name:
    /// Shop.Crate&lt;&gt;</c>, and where none does, those of its own name, as
    /// <c>named Basket: Shop.Basket</c> for <c>Mall.Basket</c> (<see cref="NamedLike"/>). Null
    /// where the name finds a type or no type fits. <paramref name="subject"/> names the assembly
    /// where the types are written in groups (<see cref="FormatGroups"/>).
    /// </summary>
    private string Meant(TypeLookup lookup, string subject)
    {
        if (lookup.Count > 0)
            return null;
        var (defined, referenced, forwarded) = (lookup.SameNameDefined, lookup.SameNameReferenced, lookup.SameNameForwarded);
        if (new[] { defined.Count, referenced.Count, forwarded.Count }.Count(count => count > 0) > 1)
            return $"of that name: {FormatGroups(defined, referenced, forwarded, subject)}";
        var names = defined.Select(ReferenceFormatter.FormatTypeName)
            .Concat(referenced.Select(r => ReferenceFormatter.FormatTypeName(r.Name)))
            .Concat(forwarded.Select(f => ReferenceFormatter.FormatTypeName(f.Name)))
            .ToList();
        if (names.Count > 0)
            return $"of that name: {string.Join(", ", names)}";
        return NamedLike(lookup, subject) is { } named ? $"named {named}" : null;
    }

    /// <summary>
    /// The types of the own name of a name that finds no type, where no type shares its full
    /// name apart from arity either, as <see cref="Meant(TypeLookup, string)"/> writes them after
    /// "named": the own name, the name's last segment, then the types, as in
    /// <c>Basket: Shop.Basket</c>. Null where no type fits.
    /// </summary>
    /// <remarks>
    /// A caller who wrote a type's name under the wrong namespace, or left out the class it is
    /// nested in, gets the name to ask by. The candidates are the types the assembly defines,
    /// references and forwards whose own name is the name's last segment, each once as
    /// <see cref="Lookup"/> counts them, in two stages. Where some differ from the name only in
    /// the namespace (<see cref="EndsName"/>), those alone are named, as the one class named X64
    /// nested in Avx2 is for <c>System.Runtime.Intrinsics.Avx2.X64</c>, of the 31 classes named
    /// X64 in .NET 10's core library. Otherwise every candidate is named, one nested in another
    /// class included. A type whose name, or an enclosing class's, follows a pattern the
    /// compiler names its own types by is a candidate only where the asked name has a segment of
    /// that kind, such as <c>&lt;&gt;c</c>, as the answers that list types leave generated ones
    /// out unless asked for them. The name alone decides, for a type the assembly defines as for
    /// one it knows by name only, so a class the compiler embeds under an ordinary name, such as
    /// NullableAttribute, is named. The first <see cref="NamedLikeShown"/> by their printed names
    /// are named, in a group for each kind where any is referenced or forwarded, and the rest is
    /// counted.
    /// </remarks>
    private string NamedLike(TypeLookup lookup, string subject)
    {
        if (lookup.Parsed is not { } parsed)
            return null;

        var own = parsed.Segments[^1].Name;
        var defined = _typesByOwnName.Value[own].ToList();
        var definedNames = defined.Select(type => type.FullTypeName.ReflectionName).ToHashSet(StringComparer.Ordinal);
        var referenced = _referencedTypesByOwnName.Value[own]
            .Where(type => !definedNames.Contains(type.Name.ReflectionName))
            .ToList();
        var known = definedNames.Concat(referenced.Select(type => type.Name.ReflectionName))
            .ToHashSet(StringComparer.Ordinal);
        var forwarded = _forwardedTypesByOwnName.Value[own].Where(type => !known.Contains(type.Name.ReflectionName));

        var withGenerated = parsed.Segments.Any(segment => CompilerGeneratedFilter.IsGeneratedName(segment.Name));
        var candidates = defined.Select(type => (Name: type.FullTypeName, Type: (object)type))
            .Concat(referenced.Select(type => (type.Name, Type: (object)type)))
            .Concat(forwarded.Select(type => (type.Name, Type: (object)type)))
            .Where(candidate => withGenerated || !CompilerGeneratedFilter.IsCompilerGenerated(candidate.Name))
            .ToList();
        var onlyNamespace = candidates.Where(candidate => EndsName(parsed, candidate.Name)).ToList();
        // A definition prints as its name does, so the names sort the types as the groups write them.
        var named = (onlyNamespace.Count > 0 ? onlyNamespace : candidates)
            .OrderBy(candidate => ReferenceFormatter.FormatTypeName(candidate.Name), StringComparer.Ordinal)
            .ToList();
        if (named.Count == 0)
            return null;

        var shown = named.Take(NamedLikeShown).Select(candidate => candidate.Type).ToList();
        var (shownDefined, shownReferenced, shownForwarded) = (shown.OfType<ITypeDefinition>().ToList(),
            shown.OfType<ReferencedType>().ToList(), shown.OfType<ForwardedType>().ToList());
        var grouped = shownReferenced.Count + shownForwarded.Count > 0;
        var list = grouped
            ? FormatGroups(shownDefined, shownReferenced, shownForwarded, subject)
            : FormatTypeList(shownDefined);
        var more = named.Count - shown.Count;
        return more == 0 ? $"{own}: {list}" : $"{own}: {list}{(grouped ? ";" : "")} and {more} more";
    }

    /// <summary>
    /// True where the classes of <paramref name="name"/>, from the outermost to the type itself,
    /// are the end of <paramref name="parsed"/>, each with the count of type parameters its
    /// segment states where it states one: the name differs from the type's only in what comes
    /// before, the namespace.
    /// </summary>
    private static bool EndsName(ParsedTypeName parsed, FullTypeName name)
    {
        var classes = new List<(string Name, int Arity)>
        {
            (name.TopLevelTypeName.Name, name.TopLevelTypeName.TypeParameterCount),
        };
        for (var level = 0; level < name.NestingLevel; level++)
            classes.Add((name.GetNestedTypeName(level), name.GetNestedTypeAdditionalTypeParameterCount(level)));
        var offset = parsed.Segments.Count - classes.Count;
        return offset >= 0 && classes.Select((type, index) => (Type: type, Segment: parsed.Segments[offset + index]))
            .All(pair => pair.Segment.Name == pair.Type.Name
                && (pair.Segment.Arity is not { } stated || stated == pair.Type.Arity));
    }

    /// <summary>
    /// Where a referenced type comes from, as its references name it: one assembly, or each
    /// they name when they disagree. Null when they name no assembly.
    /// </summary>
    public static string OriginOf(IReadOnlyList<string> assemblies) =>
        assemblies.Count == 0 ? null : string.Join(" or ", assemblies);

    private static string FormatNames(IEnumerable<FullTypeName> names) =>
        string.Join(", ", names.Select(name => ReferenceFormatter.FormatTypeName(name)));

    /// <summary>
    /// <see cref="ResolveType"/> for a lookup where a missing type is a result rather than
    /// an error. Returns <c>null</c> when the name means no type the assembly defines, a type it
    /// only references or forwards included. A name a defined type shares with other types still
    /// throws, because no single answer would be right.
    /// </summary>
    public ITypeDefinition TryResolveType(string typeName)
    {
        var lookup = Lookup(typeName);
        if (lookup.Defined.Count == 0)
            return null;
        return lookup.Count == 1 ? lookup.Defined[0] : throw AmbiguousType(typeName, lookup);
    }

    /// <summary>
    /// The type the assembly defines that <paramref name="typeName"/> means, or null where it
    /// means a type the assembly only references or forwards, several types, or none.
    /// </summary>
    public ITypeDefinition DefinitionOf(string typeName)
    {
        var lookup = Lookup(typeName);
        return lookup.Count == 1 && lookup.Defined.Count == 1 ? lookup.Defined[0] : null;
    }

    /// <summary>
    /// The type the assembly defines that <paramref name="typeName"/> names exactly as written:
    /// each segment written with a count has that many type parameters, and each written without
    /// one has none, as in every name ILens prints. Null where the assembly defines no such type,
    /// where two such types share the name in a different split into namespace and nested types,
    /// and for a name the parser does not read. <see cref="Lookup"/>'s rule can find a generic type
    /// for a name that states no count; this reading cannot.
    /// </summary>
    public ITypeDefinition DefinitionNamedExactly(string typeName)
    {
        var lookup = Lookup(typeName);
        if (lookup.Parsed is not { } parsed)
            return null;
        var exact = lookup.SameNameDefined
            .Where(type => ParsedTypeName.SegmentArities(type.FullTypeName) is var arities
                && parsed.ArityAgrees(arities) && parsed.NonGenericWhereUnstated(arities))
            .ToList();
        return exact.Count == 1 ? exact[0] : null;
    }

    /// <summary>
    /// Where the types <paramref name="typeName"/> means by <see cref="Lookup"/>'s rule lie, as
    /// the assembly's metadata records it: <paramref name="ComesFrom"/> holds the assemblies its
    /// reference rows name for those it does not forward, and <paramref name="ForwardsTo"/> the
    /// assemblies it forwards the others to, each as <c>Name, Version=…</c> and named once. Both
    /// are empty where the name means a type the assembly defines.
    /// </summary>
    public (IReadOnlyList<string> ComesFrom, IReadOnlyList<string> ForwardsTo) WhereNamed(string typeName)
    {
        var lookup = Lookup(typeName);
        var forwards = _forwardedTypes.Value;
        var comesFrom = lookup.Referenced
            .Where(type => !forwards.ContainsKey(type.Name.ReflectionName))
            .SelectMany(type => type.Assemblies);
        var forwardsTo = lookup.Forwarded
            .Concat(lookup.Referenced.Select(type => forwards.GetValueOrDefault(type.Name.ReflectionName)).OfType<ForwardedType>())
            .Select(type => type.Target);
        return ([.. comesFrom.Distinct(StringComparer.Ordinal)], [.. forwardsTo.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Where <paramref name="name"/>, a type the assembly does not define, lies as the assembly's
    /// metadata records it: the assembly an exported-type row forwards it to, or else the
    /// assemblies its reference rows say it comes from. A forward comes first, since it is the
    /// assembly's own record of where the type went. A nested type that no row names lies where
    /// the type enclosing it lies, as a private class nested in a forwarded one does, which the
    /// forward leaves out. Null where neither names an assembly.
    /// </summary>
    public Elsewhere WhereElse(FullTypeName name)
    {
        for (var type = name; ; type = type.GetDeclaringType())
        {
            if (_forwardedTypes.Value.TryGetValue(type.ReflectionName, out var forwarded))
                return new Elsewhere(forwarded.Target, Forwarded: true);
            if (_referencedTypesByFullName.Value[FullNameOf(type)]
                    .FirstOrDefault(referenced => referenced.Name.ReflectionName == type.ReflectionName) is { } reference
                && OriginOf(reference.Assemblies) is { } origin)
                return new Elsewhere(origin, Forwarded: false);
            if (!type.IsNested)
                return null;
        }
    }

    /// <summary>
    /// What a type name finds: the types it matches among those the assembly defines, those it
    /// references and those it forwards, and every type of any of the three kinds that bears its
    /// name apart from arity. <paramref name="Parsed"/> is null for a name the parser does not
    /// read, which is matched verbatim.
    /// </summary>
    private sealed record TypeLookup(ParsedTypeName Parsed,
        IReadOnlyList<ITypeDefinition> Defined, IReadOnlyList<ReferencedType> Referenced,
        IReadOnlyList<ForwardedType> Forwarded,
        IReadOnlyList<ITypeDefinition> SameNameDefined, IReadOnlyList<ReferencedType> SameNameReferenced,
        IReadOnlyList<ForwardedType> SameNameForwarded)
    {
        public int Count => Defined.Count + Referenced.Count + Forwarded.Count;
    }

    /// <summary>
    /// The types <paramref name="typeName"/> names, among those the assembly defines, those it
    /// references and those it forwards together, and every type sharing its name apart from
    /// arity. Where the name states no arity it prefers a non-generic type, the way C# and
    /// reflection read a name without type parameters: when several types match and exactly one is
    /// non-generic in every such segment, that one is the only match. So
    /// "System.Threading.Tasks.Task" is the plain Task, while "System.Func", which only generic
    /// types bear, matches all of them; and a name the assembly gives only generic types of its
    /// own means the non-generic type of that name it references or forwards, as the C# compiler
    /// reads the name in the assembly's source.
    /// </summary>
    /// <remarks>
    /// A reference to a type of the reflection name of a type the assembly defines takes no part:
    /// the definition is what the name means there, as for a polyfill such as IsExternalInit that
    /// the assembly declares beside the one a library it calls declares, or a reference row an
    /// IL rewriter wrote to the assembly's own type. A forward of a type the assembly defines or
    /// references takes no part either, so a type both referenced and forwarded is a referenced
    /// type, whose forward the answers about it read from <see cref="_forwardedTypes"/>.
    /// </remarks>
    private TypeLookup Lookup(string typeName)
    {
        var parsed = ParsedTypeName.Parse(typeName);
        // Not a name the parser reads; match it verbatim, as a FullName.
        var key = parsed?.BareName ?? typeName;
        var sameNameDefined = _typesByFullName.Value[key].ToList();
        var defined = sameNameDefined.Select(t => t.FullTypeName.ReflectionName).ToHashSet(StringComparer.Ordinal);
        var sameNameReferenced = _referencedTypesByFullName.Value[key]
            .Where(r => !defined.Contains(r.Name.ReflectionName))
            .ToList();
        var definedOrReferenced = defined.Concat(sameNameReferenced.Select(r => r.Name.ReflectionName))
            .ToHashSet(StringComparer.Ordinal);
        var sameNameForwarded = _forwardedTypesByFullName.Value[key]
            .Where(f => !definedOrReferenced.Contains(f.Name.ReflectionName))
            .ToList();
        if (parsed == null)
            return new TypeLookup(null, sameNameDefined, sameNameReferenced, sameNameForwarded,
                sameNameDefined, sameNameReferenced, sameNameForwarded);

        var matchesDefined = Agreeing(sameNameDefined, t => t.FullTypeName);
        var matchesReferenced = Agreeing(sameNameReferenced, r => r.Name);
        var matchesForwarded = Agreeing(sameNameForwarded, f => f.Name);
        if (matchesDefined.Count + matchesReferenced.Count + matchesForwarded.Count > 1)
        {
            var plainDefined = Plain(matchesDefined, t => t.FullTypeName);
            var plainReferenced = Plain(matchesReferenced, r => r.Name);
            var plainForwarded = Plain(matchesForwarded, f => f.Name);
            if (plainDefined.Count + plainReferenced.Count + plainForwarded.Count == 1)
                (matchesDefined, matchesReferenced, matchesForwarded) = (plainDefined, plainReferenced, plainForwarded);
        }
        return new TypeLookup(parsed, matchesDefined, matchesReferenced, matchesForwarded,
            sameNameDefined, sameNameReferenced, sameNameForwarded);

        // The types whose arity the name allows, and of those the types without type parameters
        // wherever the name states none.
        List<T> Agreeing<T>(List<T> types, Func<T, FullTypeName> nameOf) =>
            types.Where(type => parsed.ArityAgrees(ParsedTypeName.SegmentArities(nameOf(type)))).ToList();
        List<T> Plain<T>(List<T> types, Func<T, FullTypeName> nameOf) =>
            types.Where(type => parsed.NonGenericWhereUnstated(ParsedTypeName.SegmentArities(nameOf(type)))).ToList();
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
    /// The types the assembly's exported-type rows forward to another assembly, by reflection
    /// name, skipping the rows <see cref="ReadForward"/> rejects.
    /// </summary>
    private static IReadOnlyDictionary<string, ForwardedType> ReadForwardedTypes(MetadataReader metadata)
    {
        var forwarded = new Dictionary<string, ForwardedType>(StringComparer.Ordinal);
        foreach (var handle in metadata.ExportedTypes)
        {
            if (ReadForward(metadata, handle) is { } type)
                forwarded.TryAdd(type.Name.ReflectionName, type);
        }
        return forwarded;
    }

    /// <summary>
    /// The type an exported-type row names, with the assembly the row forwards it to as
    /// <see cref="FormatAssembly"/> writes it. The row of a nested type names the row of the type
    /// enclosing it, and the type goes where the outermost type goes. Each name carries its count
    /// of type parameters after a backtick, as a reference row's does. Null for a row whose type
    /// lies in another file of the assembly, which forwards nothing, for a row the metadata reader
    /// cannot decode, and for a chain of enclosing rows that does not end within
    /// <see cref="MaxNesting"/> steps, which only an obfuscator writes.
    /// </summary>
    private static ForwardedType ReadForward(MetadataReader metadata, ExportedTypeHandle handle)
    {
        try
        {
            var row = metadata.GetExportedType(handle);
            var nested = new List<string>();
            for (var depth = 0; row.Implementation.Kind == HandleKind.ExportedType; depth++)
            {
                if (depth == MaxNesting || row.Implementation.IsNil)
                    return null;
                nested.Add(metadata.GetString(row.Name));
                row = metadata.GetExportedType((ExportedTypeHandle)row.Implementation);
            }
            if (AssemblyOf(metadata, row.Implementation) is not { } assembly)
                return null;

            var top = ReflectionHelper.SplitTypeParameterCountFromReflectionName(metadata.GetString(row.Name), out var count);
            FullTypeName name = new TopLevelTypeName(metadata.GetString(row.Namespace), top, count);
            // The enclosing rows were met from the innermost type outward.
            for (var level = nested.Count - 1; level >= 0; level--)
            {
                var inner = ReflectionHelper.SplitTypeParameterCountFromReflectionName(nested[level], out var innerCount);
                name = name.NestedType(inner, innerCount);
            }
            return new ForwardedType(name, FormatAssembly(assembly.Name, assembly.Version));
        }
        catch (BadImageFormatException)
        {
            return null;
        }
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
    /// The assembly a reference scope names, as <c>Name, Version=…</c>, or null where
    /// <see cref="AssemblyOf"/> finds none.
    /// </summary>
    private static string AssemblyNameOf(MetadataReader metadata, EntityHandle scope) =>
        AssemblyOf(metadata, scope) is var (name, version) ? FormatAssembly(name, version) : null;

    /// <summary>An assembly as an answer names it, <c>Name, Version=…</c>.</summary>
    internal static string FormatAssembly(string name, Version version) => $"{name}, Version={version}";

    /// <summary>
    /// The assembly a reference scope names, or null for a scope that names a module rather
    /// than an assembly, or an assembly row the metadata cannot read. Some builds of Harmony's
    /// own DLL from 2.3 on, 2.4.1's among them, have references that name rows past the end of
    /// their assembly table, left behind by the libraries merged into them.
    /// </summary>
    internal static (string Name, Version Version)? AssemblyOf(MetadataReader metadata, EntityHandle scope)
    {
        if (scope.Kind != HandleKind.AssemblyReference || scope.IsNil)
            return null;
        try
        {
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
            return (metadata.GetString(reference.Name), reference.Version);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The assemblies the reference rows of <paramref name="metadata"/> say the type
    /// <paramref name="name"/> comes from, one unless they disagree. None when no row names
    /// the type or none names an assembly.
    /// </summary>
    internal static IReadOnlyList<(string Name, Version Version)> AssembliesReferencedFor(
        MetadataReader metadata, FullTypeName name)
    {
        var assemblies = new List<(string Name, Version Version)>();
        foreach (var handle in metadata.TypeReferences)
        {
            if (TryReadReference(metadata, handle, out var referenced, out var scope)
                && referenced.ReflectionName == name.ReflectionName
                && AssemblyOf(metadata, scope) is { } assembly
                && !assemblies.Contains(assembly))
                assemblies.Add(assembly);
        }
        return assemblies;
    }

    /// <summary>
    /// A type's name as <c>ITypeDefinition.FullName</c> writes it, dotted and without arity.
    /// <c>FullTypeName.FullName</c> writes the same, except that it starts a type in the
    /// global namespace with a dot.
    /// </summary>
    private static string FullNameOf(FullTypeName name) =>
        string.IsNullOrEmpty(name.TopLevelTypeName.Namespace) ? name.FullName[1..] : name.FullName;

    /// <summary>
    /// Every type <paramref name="parsed"/> can name. The name does not say which of its
    /// segments are namespaces, so each segment in turn is read as the top-level type, in the
    /// namespace the segments before it spell and with those after it nested in it. A segment
    /// without a written arity is read as non-generic, and a namespace has none. The reading
    /// with the longest namespace comes first.
    /// </summary>
    private static IReadOnlyList<FullTypeName> Readings(ParsedTypeName parsed)
    {
        var segments = parsed.Segments;
        var readings = new List<FullTypeName>();
        for (var top = segments.Count - 1; top >= 0; top--)
        {
            if (segments.Take(top).Any(segment => segment.Arity.HasValue))
                continue;
            FullTypeName reading = new TopLevelTypeName(
                string.Join(".", segments.Take(top).Select(segment => segment.Name)),
                segments[top].Name, segments[top].Arity ?? 0);
            foreach (var nested in segments.Skip(top + 1))
                reading = reading.NestedType(nested.Name, nested.Arity ?? 0);
            readings.Add(reading);
        }
        return readings;
    }

    /// <summary>
    /// The error for a name several types match: those the assembly defines, those it references,
    /// those it forwards, or a group for each of these kinds that has any, each type passing back
    /// as a type name.
    /// </summary>
    private static ArgumentException AmbiguousType(string typeName, TypeLookup lookup)
    {
        var types = (lookup.Defined.Count, lookup.Referenced.Count, lookup.Forwarded.Count) switch
        {
            (_, 0, 0) => $": {FormatTypeList(lookup.Defined)}",
            (0, _, 0) => $" this assembly references but does not define: {FormatNames(lookup.Referenced.Select(t => t.Name))}",
            (0, 0, _) => $" this assembly forwards: {FormatNames(lookup.Forwarded.Select(t => t.Name))}",
            _ => $": {FormatGroups(lookup.Defined, lookup.Referenced, lookup.Forwarded)}",
        };
        return new($"Type name '{typeName}' matches {lookup.Count} types{types}. Name one as listed.");
    }

    /// <summary>
    /// Types of one name the assembly defines, references and forwards, as a group for each of
    /// these kinds that has any: <c>this assembly defines A; it references B, C from
    /// Lib, Version=1.0.0.0; it forwards D to Core, Version=3.0.0.0</c>, the library a group of
    /// referenced types comes from, or forwarded types go to, named once, and each library where
    /// they come from or go to several. <paramref name="subject"/> takes the place of "this
    /// assembly" in an answer about two builds, as "each build" or "it".
    /// </summary>
    private static string FormatGroups(IReadOnlyList<ITypeDefinition> defined, IReadOnlyList<ReferencedType> referenced,
        IReadOnlyList<ForwardedType> forwarded, string subject = ThisAssembly)
    {
        var groups = new List<string>(3);
        if (defined.Count > 0)
            groups.Add($"defines {FormatTypeList(defined)}");
        if (referenced.Count > 0)
            groups.Add("references " + string.Join(" and ", referenced
                .GroupBy(type => OriginOf(type.Assemblies) ?? "", StringComparer.Ordinal)
                .Select(group => group.Key.Length == 0
                    ? FormatNames(group.Select(t => t.Name))
                    : $"{FormatNames(group.Select(t => t.Name))} from {group.Key}")));
        if (forwarded.Count > 0)
            groups.Add("forwards " + string.Join(" and ", forwarded
                .GroupBy(type => type.Target, StringComparer.Ordinal)
                .Select(group => $"{FormatNames(group.Select(t => t.Name))} to {group.Key}")));
        return $"{subject} {string.Join("; it ", groups)}";
    }

    private static string FormatTypeList(IEnumerable<ITypeDefinition> types) =>
        string.Join(", ", types.Select(ReferenceFormatter.FormatTypeName));

    // --- Member umbrella -------------------------------------------------------

    /// <summary>
    /// Resolve a member by name. With a method-overload <paramref name="hint"/> the name is
    /// resolved as a method. Without a hint,
    /// every member kind is probed, and the matches of the most derived class that has the
    /// name count, as in C#: a single match is returned with its origin and category; a name
    /// that class declares as more than one kind is reported as an error rather than silently
    /// resolved by a fixed priority. Throws if no member by that name exists in any kind. The
    /// type's own name reaches its constructors, see <see cref="ConstructorNameFor"/>.
    /// </summary>
    /// <remarks>
    /// C# guarantees a method, property, field, and event within one type cannot share a
    /// name, but IL does not — and obfuscated assemblies deliberately reuse names across
    /// metadata tables. Probing every kind turns such a collision into an actionable error
    /// instead of a silent first-match-wins answer.
    /// </remarks>
    public (ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category) ResolveMember(
        ITypeDefinition type, string memberName, MethodHint hint = null)
    {
        hint ??= MethodHint.None;
        ValidateDisambiguationArgs(hint);
        memberName = ConstructorNameFor(type, memberName);

        // A method-overload hint means the caller is asking for a method specifically.
        if (hint.IsGiven)
        {
            var hinted = TryFindMethod(type, memberName, hint, includeExplicit: true, explainAbsence: false)
                ?? TryFindMethod(type, memberName, hint, includeExplicit: true, unreachable: true)
                ?? throw NotFound("Method", memberName, type, searchesExtensions: true);
            RequireOwn(type, hinted.Method, hint);
            return (hinted.Method, hinted.Origin, SymbolCategory.Method);
        }

        return FindMember(type, memberName, explainAbsence: false)
            ?? FindMember(type, memberName, unreachable: true)
            ?? throw NotFound("Member", memberName, type, searchesExtensions: true);
    }

    private (ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)? FindMember(
        ITypeDefinition type, string memberName, bool explainAbsence = true, bool unreachable = false)
    {
        var matches = NearestMembers(type, memberName, explainAbsence, unreachable);
        if (matches.Count == 0)
            return null;

        if (matches.Count == 1)
        {
            RequireOwn(type, (IEntity)matches[0].Symbol);
            return matches[0];
        }

        // More than one kind matched — the name collides across member kinds.
        var kinds = string.Join(", ", matches.Select(m => m.Category));
        throw new ArgumentException(
            $"Member '{memberName}' on {ReferenceFormatter.FormatTypeName(type)} is ambiguous — it exists as " +
            $"more than one member kind ({kinds}). Pass a {ParameterNames.ParameterCount} / " +
            $"{ParameterNames.ParameterTypes} hint if you mean the method; the type's decompiled code shows them all.");
    }

    /// <summary>
    /// The members a name without a hint finds, as <see cref="ResolveMember"/> finds them, for a
    /// caller that answers each case on its own: none, one, or several where the nearest class that
    /// has the name declares it as more than one kind. <see cref="ResolveMember"/> refuses several,
    /// and one that a class of another assembly declares (<see cref="IsExternal"/>). Without
    /// <paramref name="explainAbsence"/>, an accessor name whose property or event lacks that
    /// accessor finds nothing, where it throws saying so otherwise.
    /// </summary>
    public IReadOnlyList<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)> FindNearestMembers(
        ITypeDefinition type, string memberName, bool explainAbsence = true)
    {
        var name = ConstructorNameFor(type, memberName);
        var matches = NearestMembers(type, name, explainAbsence: false);
        return matches.Count > 0 ? matches : NearestMembers(type, name, explainAbsence, unreachable: true);
    }

    /// <summary>
    /// The members <see cref="FindNearestMembers"/> finds, among those the lookup sees
    /// (<see cref="Sees"/>), or with <paramref name="unreachable"/> among every member of the
    /// classes it walks, for the lookup's last try, which keeps the error that names a member of
    /// another library's class the type's code cannot reach.
    /// </summary>
    private List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)> NearestMembers(
        ITypeDefinition type, string memberName, bool explainAbsence = true, bool unreachable = false)
    {
        // No hint: probe every kind so a cross-kind name collision is caught. Explicit interface
        // implementations are probed only when no ordinary member of any kind has the name, as
        // C# binds the plain name to the ordinary member, so the two never collide.
        var matches = Probe(includeExplicit: false);
        if (matches.Count == 0)
            matches = Probe(includeExplicit: true);

        // The ordinary C# event declaration compiles to an event *and* a private field of
        // the same name holding the subscriber list, so the cross-kind probe reports every
        // one of them as a collision. That is not the collision this check exists for: the
        // pair has a fixed compiler-generated shape, and only the event was ever written in
        // source. Resolve to the event — otherwise no field-like event is addressable by
        // name at all, which silently rules out the common case.
        if (matches.Count > 1 && CollapseFieldLikeEvent(matches) is { } collapsed)
            return [collapsed];
        return matches;

        List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)> Probe(bool includeExplicit)
        {
            var found = new List<(ISymbol Symbol, MemberOrigin Origin, SymbolCategory Category)>();

            var method = TryFindMethod(type, memberName, MethodHint.None, includeExplicit, explainAbsence: false,
                includeExtensions: false, unreachable);
            if (method.HasValue)
                found.Add((method.Value.Method, method.Value.Origin, SymbolCategory.Method));

            var prop = TryFindProperty(type, memberName, includeExplicit, unreachable);
            if (prop.HasValue)
                found.Add((prop.Value.Property, prop.Value.Origin, SymbolCategory.Property));

            var field = TryFindField(type, memberName, unreachable);
            if (field.HasValue)
                found.Add((field.Value.Field, field.Value.Origin, SymbolCategory.Field));

            var evt = TryFindEvent(type, memberName, includeExplicit, unreachable);
            if (evt.HasValue)
                found.Add((evt.Value.Event, evt.Value.Origin, SymbolCategory.Event));

            // An extension method lies past every level, as C# tries one only when no member
            // answers, so it is looked for only then. Looked for beside the members, several of
            // one name would end the question in the overload error where a property, a field
            // or an event of the name answers. The probe with explicit implementations runs only
            // where this one found nothing, so it does not look again.
            if (found.Count == 0 && !includeExplicit)
            {
                var extension = TryFindMethod(type, memberName, MethodHint.None, includeExplicit, explainAbsence,
                    unreachable: unreachable);
                if (extension.HasValue)
                    found.Add((extension.Value.Method, extension.Value.Origin, SymbolCategory.Method));
            }

            // C# stops at the most derived class that declares the name, whatever the kind of
            // the member there, so only the matches of the nearest level count. A name one type
            // declares as two kinds, which IL allows, still collides.
            if (found.Count < 2)
                return found;
            var levels = Levels(type).Select(level => level.Origin).ToList();
            var nearest = found.Min(match => levels.IndexOf(match.Origin));
            return found.Where(match => levels.IndexOf(match.Origin) == nearest).ToList();
        }
    }

    /// <summary>
    /// A member's or a parameter's name as the caller writes it, read as C# reads the '@' before a
    /// keyword: <c>@base</c> is <c>base</c>, also after a dot and among the type parameters in
    /// brackets, <c>IOps.@do</c> and <c>Get&lt;@class&gt;</c>, as declarations write names
    /// (<see cref="ReferenceFormatter.FormatIdentifier"/>). The words are those ILSpy's test holds
    /// for without a context, await and the query keywords among them, which the decompiled code
    /// writes with '@' inside an async member or a query. Before any other word the '@' stays part
    /// of the name, since a compiler may begin a name with it, as F# names a closure's instance
    /// field <c>@_instance</c>, and ILens writes it before a keyword alone.
    /// </summary>
    public static string ReadName(string name) =>
        string.IsNullOrEmpty(name) || !name.Contains('@')
            ? name
            : VerbatimKeyword.Replace(name,
                match => CSharpOutputVisitor.IsKeyword(match.Groups[1].Value) ? match.Groups[1].Value : match.Value);

    private static readonly Regex VerbatimKeyword = new(@"(?<![\w@])@([a-z]+)\b", RegexOptions.Compiled);

    /// <summary>
    /// The name to look <paramref name="memberName"/> up by: <c>.ctor</c> for the simple name
    /// of <paramref name="type"/> itself, under which list_members prints its constructors, and
    /// the name as given otherwise. C# lets no member bear its type's name and IL does, so the
    /// name means the constructors only when the type, its base types and the extension methods
    /// have no member of that name. A static constructor is <c>.cctor</c> alone, since the name
    /// with no parameters would otherwise mean it and the parameterless constructor alike.
    /// </summary>
    private string ConstructorNameFor(ITypeDefinition type, string memberName) =>
        memberName == type.Name
        && type.DeclaredMethods().Any(method => method.IsConstructor && !method.IsStatic)
        && !HasMemberNamed(type, memberName)
            ? ".ctor"
            : memberName;

    /// <summary>
    /// True if any route of the member search finds something named <paramref name="name"/>:
    /// a member of any kind on the type or its base types, an explicit implementation of an
    /// interface member of that name, or an extension method; for a name with type parameters
    /// in brackets, a method with that count of them. A member of another library's base class
    /// that the type's code cannot reach counts as well, since the search's last try finds it
    /// (<see cref="Sees"/>).
    /// </summary>
    public bool HasMemberNamed(ITypeDefinition type, string name)
    {
        if (Levels(type).Any(level => MethodsAndAccessors(level.Level).Cast<IMember>()
                .Concat(level.Level.Properties).Concat(level.Level.Fields).Concat(level.Level.Events)
                .Any(member => member.Name == name || ImplementsExplicitly(member, name)))
            || FindExtensionMethodsByName(type, name).Count > 0)
            return true;
        var (bare, fits, _) = ByTypeParameters(type, name, unreachable: true);
        return bare != name
            && (Levels(type).Any(level => MethodsAndAccessors(level.Level)
                    .Any(method => (method.Name == bare || ImplementsExplicitly(method, bare)) && fits(method)))
                || FindExtensionMethodsByName(type, bare).Any(fits));
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

        return CompilerGeneratedFilter.IsBackingFieldOf(f, e) ? evt : null;
    }

    /// <summary>
    /// The declaration that introduced <paramref name="member"/>'s virtual slot: an override
    /// followed back through the declarations it overrides, and any other member itself. C#
    /// compiles a call to a virtual member as a call to that declaration, so <c>dog.Speak()</c>
    /// names <c>Animal.Speak</c> in the IL. Two kinds of call a C# compiler writes name an
    /// override instead: one on a value of a core library type such as <c>int</c>, a call that
    /// is not virtual, to that type's own override, and a virtual call of a generic method,
    /// which names the nearest override the receiver's class sees. A call on an object of a
    /// class that overrides with a more specific return type names that override as well,
    /// since the call returns its type. The VB and the F# compiler name the nearest override
    /// in any virtual call. A virtual call that names an override is therefore counted as a
    /// call to this declaration (<c>AssemblyHost.BodiesNaming</c>), and <c>CallSiteFinder</c>
    /// reads whether a call is virtual from the call's instruction where the decompiled code
    /// carries it. When the result is itself an override, the type system could not resolve
    /// the declaration it overrides, as for one in an assembly ILens does not load.
    /// <see cref="Overriding"/> says what overrides.
    /// </summary>
    public static T SlotDeclarationOf<T>(T member) where T : class, IMember
    {
        var current = member.MemberDefinition;
        while (Overriding.Overridden(current) is { } overridden)
            current = overridden.MemberDefinition;
        return (T)current;
    }

    /// <summary>
    /// True for a method that introduced a virtual slot, which an override can fill: a virtual
    /// or abstract method that is no override itself.
    /// </summary>
    public static bool IntroducesSlot(IMethod method) =>
        !Overriding.Of(method) && (method.IsVirtual || method.IsAbstract);

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

    /// <inheritdoc cref="ValidateDisambiguationArgs(int?, string[])"/>
    internal static void ValidateDisambiguationArgs(MethodHint hint) =>
        ValidateDisambiguationArgs(hint.ParameterCount, hint.ParameterTypes);

    // --- Method resolution -----------------------------------------------------

    /// <summary>
    /// Resolve a method by name, searching declared → inherited → extension methods, then
    /// explicit interface implementations. Returns the method and its origin relative to the
    /// requested type.
    /// </summary>
    public (IMethod Method, MemberOrigin Origin) ResolveMethod(
        ITypeDefinition type, string methodName, MethodHint hint = null)
    {
        hint ??= MethodHint.None;
        ValidateDisambiguationArgs(hint);
        methodName = ConstructorNameFor(type, methodName);
        var found = TryFindMethod(type, methodName, hint, includeExplicit: true, explainAbsence: false)
            ?? TryFindMethod(type, methodName, hint, includeExplicit: true, unreachable: true)
            ?? throw NotFound("Method", methodName, type, searchesExtensions: true);
        RequireOwn(type, found.Method, hint);
        return found;
    }

    /// <summary>
    /// <see cref="ResolveMethod"/> for a method the type may lack, as one of two builds of an
    /// assembly may: null where ResolveMethod throws because no method satisfies the request,
    /// including a hint that matches none of the overloads and an accessor its property or event
    /// does not have. Still throws where several methods satisfy it, and, as ResolveMethod does,
    /// for one inherited from a base class another assembly defines.
    /// </summary>
    public (IMethod Method, MemberOrigin Origin)? TryResolveMethod(
        ITypeDefinition type, string methodName, MethodHint hint)
    {
        ValidateDisambiguationArgs(hint);
        methodName = ConstructorNameFor(type, methodName);
        if ((TryFindMethod(type, methodName, hint, includeExplicit: true, explainAbsence: false)
                ?? TryFindMethod(type, methodName, hint, includeExplicit: true, explainAbsence: false, unreachable: true))
            is not { } found)
            return null;
        RequireOwn(type, found.Method, hint);
        return found;
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
        ITypeDefinition type, string methodName, MethodHint hint)
    {
        ValidateDisambiguationArgs(hint);
        methodName = ConstructorNameFor(type, methodName);
        if (hint.IsGiven)
            return [ResolveMethod(type, methodName, hint).Method];

        var (name, fits, _) = ByTypeParameters(type, methodName);
        foreach (var (level, _) in LevelsFor(type, name))
        {
            var here = MethodsAndAccessors(level).Where(m => m.Name == name && fits(m) && Sees(type, m)).ToList();
            if (here.Count > 0)
            {
                RequireOwn(type, here[0]);
                return here;
            }
        }

        return [ResolveMethod(type, methodName).Method];
    }

    /// <summary>
    /// The name a method is looked up by, a test that keeps the methods of the count of type
    /// parameters the name selects, and whether that test leaves out generic methods of the name.
    /// ILens writes a generic method with its type parameters after its name,
    /// <c>Field&lt;T&gt;</c>, and a method made for type arguments with those,
    /// <c>Field&lt;int&gt;</c>, so trailing angle brackets give the count, also unbound,
    /// <c>Field&lt;&gt;</c>. A name without brackets keeps the methods without type parameters
    /// where the name has one anywhere the search looks, as a type name without arity means the
    /// non-generic type, and every method otherwise: <c>Field</c> beside <c>Field&lt;T&gt;</c>
    /// is the one without, also where the two stand on different classes of the walk, and a
    /// generic method without such a sibling stays reachable by its plain name. Where the count
    /// leaves several methods, the names in the brackets keep those whose type parameters bear
    /// them, when any does, so <c>Take&lt;U&gt;</c> means <c>Take&lt;U&gt;(T)</c> beside
    /// <c>Take&lt;T&gt;(T)</c>, as ILens writes the two. A name a method bears as it stands,
    /// which IL allows with brackets, is that name. The methods counted are those the search
    /// sees (<see cref="Sees"/>), or with <paramref name="unreachable"/> every method of the
    /// classes it walks.
    /// </summary>
    private (string Name, Func<IMethod, bool> Fits, bool LeavesOutGeneric) ByTypeParameters(
        ITypeDefinition type, string methodName, bool unreachable = false) =>
        ByTypeParameters(Levels(type).SelectMany(level => MethodsAndAccessors(level.Level))
                .Where(method => unreachable || Sees(type, method)).ToList(),
            name => FindExtensionMethodsByName(type, name), methodName);

    /// <summary>
    /// <see cref="ByTypeParameters(ITypeDefinition, string)"/> among the
    /// <paramref name="methods"/> of the levels a search walks and the extension methods
    /// <paramref name="extensionsNamed"/> finds for a name.
    /// </summary>
    private static (string Name, Func<IMethod, bool> Fits, bool LeavesOutGeneric) ByTypeParameters(
        List<IMethod> methods, Func<string, List<IMethod>> extensionsNamed, string methodName)
    {
        if (!methods.Any(method => method.Name == methodName)
            && ReadTypeArgumentCount(methodName) is (var bare, { } count))
        {
            var names = TypeArgumentNames(methodName, bare);
            bool OfCount(IMethod method) => method.TypeParameters.Count == count;
            bool BearsNames(IMethod method) => method.TypeParameters.Select(parameter => parameter.Name).SequenceEqual(names);
            var ofCount = methods
                .Where(method => method.Name == bare || ImplementsExplicitly(method, bare))
                .Concat(extensionsNamed(bare))
                .Where(OfCount)
                .ToList();
            return ofCount.Count > 1 && ofCount.Any(BearsNames)
                ? (bare, method => OfCount(method) && BearsNames(method), false)
                : (bare, OfCount, false);
        }

        var named = methods
            .Where(method => method.Name == methodName || ImplementsExplicitly(method, methodName))
            .Concat(extensionsNamed(methodName))
            .ToList();
        var withoutTypeParameters = named.Any(method => method.TypeParameters.Count == 0);
        return (methodName, method => !withoutTypeParameters || method.TypeParameters.Count == 0,
            withoutTypeParameters && named.Any(method => method.TypeParameters.Count > 0));
    }

    /// <summary>
    /// A name and the count of type arguments that angle brackets ending it give, counted at
    /// their own depth, so <c>Get&lt;List&lt;int&gt;, string&gt;</c> gives two; the count is null
    /// when the name does not end in brackets after a name character. A compiler-generated name
    /// such as <c>&lt;Main&gt;b__0_0</c> ends in none.
    /// </summary>
    public static (string Name, int? Count) ReadTypeArgumentCount(string text)
    {
        if (string.IsNullOrEmpty(text) || text[^1] != '>')
            return (text, null);
        var depth = 0;
        var commas = 0;
        for (var i = text.Length - 1; i > 0; i--)
        {
            switch (text[i])
            {
                case '>':
                    depth++;
                    break;
                case ',' when depth == 1:
                    commas++;
                    break;
                case '<':
                    depth--;
                    if (depth == 0)
                        return char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'
                            ? (text[..i], commas + 1)
                            : (text, null);
                    break;
            }
        }
        return (text, null);
    }

    /// <summary>
    /// What the brackets that end <paramref name="text"/> hold, one entry per type argument, as
    /// <see cref="ReadTypeArgumentCount"/> counted them for <paramref name="bare"/>: the names of
    /// type parameters, <c>T</c>, type arguments, <c>int</c>, or nothing, for <c>Get&lt;&gt;</c>.
    /// </summary>
    private static List<string> TypeArgumentNames(string text, string bare) =>
        ParsedTypeName.SplitOutsideBrackets(text[(bare.Length + 1)..^1]);

    /// <summary>
    /// The method a name and hint select, or null when none does. With
    /// <paramref name="explainAbsence"/>, a name the walk finds only with other overloads, or an
    /// accessor name whose property or event lacks that accessor, throws saying so instead.
    /// Without <paramref name="includeExtensions"/> the extension methods are not searched, for a
    /// caller that looks for them only where no member of another kind answers; the choice
    /// between methods with and without type parameters still counts them. The levels' methods
    /// searched are those the search sees (<see cref="Sees"/>), or with
    /// <paramref name="unreachable"/> all of them, for the search's last try.
    /// </summary>
    /// <remarks>
    /// A hint with a pattern is held as an exact fit first, through every route of the search,
    /// and only where nothing anywhere fits it exactly as a loose fit (see
    /// <see cref="TypeMatcher.Matches(IType, string, bool)"/>). So a method whose parameter
    /// types the caller wrote is reached beside namesakes the loose fit also takes, such as
    /// <c>Truncate(decimal)</c> beside <c>Truncate(ref decimal)</c> or <c>Fill(int)</c> on a base
    /// class beside a derived class's <c>Fill(int?)</c>, while a hint written loosely still
    /// finds the one method it fits. The answer is about the method whose parameter types were
    /// written, which is what a hint asks for, wherever the search finds it.
    /// </remarks>
    private (IMethod Method, MemberOrigin Origin)? TryFindMethod(
        ITypeDefinition type, string methodName, MethodHint hint,
        bool includeExplicit, bool explainAbsence = true, bool includeExtensions = true, bool unreachable = false)
    {
        var hinted = hint.IsGiven;
        var (name, fits, leavesOutGeneric) = ByTypeParameters(type, methodName, unreachable);
        var levels = LevelsFor(type, name).ToList();
        var named = new List<IMethod>();

        bool[] fitsInOrder = hint.HasPatterns ? [true, false] : [false];
        foreach (var exact in fitsInOrder)
        {
            // A name without brackets keeps the methods without type parameters where the name
            // has both, and a hint none of them satisfies is held against the generic methods as
            // well: C# prefers a method without type parameters only where both fit the
            // arguments, so Write with (string, ref EventSourceOptions, ref T) means Write<T>.
            named.Clear();
            var found = Search(fits, exact);
            if (!found.HasValue && hinted && leavesOutGeneric)
            {
                named.Clear();
                found = Search(_ => true, exact);
            }
            if (found.HasValue)
                return found;
        }

        if (!explainAbsence)
            return null;

        // A hint was given and the name exists somewhere on the chain (or as an extension),
        // but nothing satisfies it. Report the whole candidate set — the overload the caller
        // wanted may be declared on a base type, and naming it is the actionable result.
        if (hinted && named.Count > 0)
            throw new ArgumentException(
                $"No overload of '{methodName}' on {ReferenceFormatter.FormatTypeName(type)} matches " +
                $"{hint.Describe()}. " +
                $"Available: {ReferenceFormatter.FormatMethodList(named)}{UnreadClause(BasesOf(type).Unread)}.");

        var missingAccessor = MissingAccessorError(type, methodName, includeExplicit);
        if (missingAccessor != null)
            throw missingAccessor;

        return null;

        // Every route, in C#'s order, among the methods of the name that `keeps` keeps, with
        // the hint held exactly or loosely.
        (IMethod Method, MemberOrigin Origin)? Search(Func<IMethod, bool> keeps, bool exact)
        {
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
            var ordinary = WalkLevels(m => m.Name == name && keeps(m), isExplicit: false, exact);
            if (ordinary.HasValue)
                return ordinary;

            // 3. Extension methods, tried only after no instance overload satisfied the request —
            // the order C# uses, where extensions are a fallback for unresolved instance calls.
            var extensions = includeExtensions ? FindExtensionMethodsByName(type, name).Where(keeps).ToList() : [];
            if (extensions.Count > 0)
            {
                if (!hinted || NarrowByHint(extensions, hint, exact).Count > 0)
                {
                    var method = DisambiguateMethod(extensions, "(extension methods)", methodName, hint, exact);
                    return (method, MemberOrigin.ExtensionOn(ReferenceFormatter.FormatExtendedType(method)));
                }
                named.AddRange(extensions);
            }

            // 4. Explicit interface implementations, found by the name of the interface member they
            // implement: System.IDisposable.Dispose by Dispose, and an accessor IShape.get_Name by
            // get_Name. C# never binds the plain name to one, so they come after everything above,
            // which keeps an ordinary GetEnumerator() reachable beside an explicit
            // IEnumerable.GetEnumerator() with the same parameters.
            return includeExplicit
                ? WalkLevels(m => ImplementsExplicitly(m, name) && keeps(m), isExplicit: true, exact)
                : null;
        }

        // The candidates at the most-derived level that has any, or with a hint, at the
        // most-derived level where one satisfies it. Several satisfying candidates at one level
        // are an ambiguity that no base level can settle.
        (IMethod Method, MemberOrigin Origin)? WalkLevels(Func<IMethod, bool> isCandidate, bool isExplicit, bool exact)
        {
            foreach (var (level, origin) in levels)
            {
                var here = MethodsAndAccessors(level)
                    .Where(method => isCandidate(method) && (unreachable || Sees(type, method)))
                    .ToList();
                if (here.Count == 0)
                    continue;

                named.AddRange(here);
                var satisfying = NarrowByHint(here, hint, exact);
                // A level another assembly defines answers with its first candidate, and
                // RequireOwn names them all in its error.
                if (satisfying.Count > 0 && IsExternal(level))
                    return (satisfying[0], origin);
                if (satisfying.Count == 1)
                    return (satisfying[0], origin);
                if (satisfying.Count > 1)
                    return isExplicit
                        ? throw AmbiguousExplicitImplementationError(satisfying, level, methodName)
                        : (DisambiguateMethod(here, ReferenceFormatter.FormatTypeName(level), methodName, hint, exact),
                            origin);
                // No candidate here satisfies the hint; a base level may still declare one.
            }
            return null;
        }
    }

    /// <summary>
    /// The members of <paramref name="member"/>'s kind and name that a lookup on
    /// <paramref name="type"/> sees: those of the type and of the base types a member of that
    /// name is looked for on, and for a method the extension methods of the name. An answer
    /// that names the member it was asked about writes it in full where one of them reads alike
    /// in the short form, since that name is all that shows which member came back.
    /// </summary>
    public IReadOnlyList<IMember> Namesakes(ITypeDefinition type, IMember member)
    {
        var levels = LevelsFor(type, member.Name).Select(level => level.Level).ToList();
        return member switch
        {
            IMethod method => [.. levels.SelectMany(MethodsAndAccessors).Where(m => m.Name == method.Name),
                .. FindExtensionMethodsByName(type, method.Name)],
            IProperty property => [.. levels.SelectMany(level => level.Properties).Where(p => p.Name == property.Name)],
            _ => [member],
        };
    }

    /// <summary>
    /// An extension method a lookup found through a type another assembly defines
    /// (<see cref="FindExtensionThrough"/>): the method; where it was found, an extension method
    /// on the type it extends; the methods a line naming it is told apart from, as
    /// <see cref="Namesakes"/> gives them for a type of this assembly; the type as the type system
    /// that holds its library reads it, or null where its library was not found; and the line
    /// that says what the lookup could not read, or null where it read everything.
    /// </summary>
    public sealed record ExtensionThrough(IMethod Method, MemberOrigin Origin, IReadOnlyList<IMethod> Namesakes,
        ITypeDefinition Type, string NotChecked);

    /// <summary>
    /// The extension method of this assembly that <paramref name="memberName"/> names through
    /// <paramref name="type"/>, a type the assembly only references or forwards, or null where the
    /// name names a member of that type, or nothing. The type is read from its own library where the
    /// reference search found it, and the name is looked up as C# binds it from this assembly: a
    /// member of the name that comes first on the type, one of its base classes or, for an
    /// interface, one of its base interfaces (<see cref="ReachableMembers"/>), or with a hint such
    /// a method that fits it, answers, by the rules <see cref="ResolveMember"/> applies to a type
    /// of this assembly, and then the extension methods for the type and every base class and
    /// interface it has, and those whose receiver is a type parameter the type fits. With
    /// <paramref name="methodsOnly"/>, for a question that looks for a method by name, as
    /// call_sites and compare do through a type of this assembly, only a method of the name comes
    /// first. Where the library was not found, what the type is and derives from is not known, so
    /// only an extension method for the type itself is found, or one whose receiver is a type
    /// parameter every type meets, and the members that come first are those this assembly's
    /// references name on the type (<see cref="ReferencedMembers"/>), which the answers about
    /// such a type's members are read from. The result says what was not read, as it does where
    /// a base class could not be read.
    /// </summary>
    /// <remarks>
    /// A member the assembly cannot reach does not keep an extension method out, since C# binds
    /// the extension method there, and neither does a static member, which C# discards for a call
    /// on a value from version 7.3. A hint names a declaration rather than a call, so with one, a
    /// static or protected member the hint fits comes first, also where the extension method takes
    /// the same parameters, as System.Type's static GetTypeCode(Type) does beside an extension
    /// method GetTypeCode(this Type); of the extension methods counted in a game's libraries, its
    /// mods and .NET 10 on 2026-10-19, three stand beside such a member, all of them GetTypeCode.
    /// For a type of this assembly every member of its own classes comes first, since its code is
    /// here. Of the extension methods whose receiver is another library's type, 3 to 7 % share
    /// their name with a public member of that type in a game's libraries, its mods and .NET 10,
    /// and under 2 % with a member that is not public alone; 0.4 to 7 % share it with a member
    /// their own assembly references on the type, counted on 2026-10-17.
    /// </remarks>
    public ExtensionThrough FindExtensionThrough(ExternalTypeName type, string memberName, MethodHint hint,
        bool methodsOnly = false)
    {
        hint ??= MethodHint.None;
        if (LookThrough(type, memberName, hint) is not { } through
            || FindExtensionBeside(through.Members, through.Named, through.LookedFor, hint, methodsOnly) is not { } found)
            return null;

        var definition = through.Definition;
        if (definition is null)
        {
            var reason = UnreadLibrary.Reason(
                [(type.Display, type.LookupNames.Count > 0 ? _unreadLibrariesOf(type.LookupNames[0]) : [])]);
            return new ExtensionThrough(found.Method, found.Origin, through.Named(found.Method.Name), null,
                NotReadLine(type.Display, memberName, reason));
        }
        return new ExtensionThrough(found.Method, found.Origin, [.. Namesakes(definition, found.Method).OfType<IMethod>()],
            definition, NotReadThrough(definition, memberName));
    }

    /// <summary>
    /// The line an answer found through <paramref name="type"/>, a type of another assembly read
    /// from its library, carries where a type whose members the lookup weighs could not be read:
    /// a base class, or for an interface a base interface. Null where it read them all.
    /// </summary>
    private string NotReadThrough(ITypeDefinition type, string memberName)
    {
        if (BasesOf(type).Unread is { } unread)
            return NotReadLine(unread.TypeName, memberName, unread.Reason);
        var interfaces = TypeWalker.WalkBaseInterfaces(type).Unread;
        return interfaces.Count == 0
            ? null
            : NotReadLine(ReferenceFormatter.FormatTypeName(type), memberName,
                UnreadLibrary.Reason(interfaces.Select(unreadInterface => (unreadInterface.TypeName, unreadInterface.Libraries))));
    }

    /// <summary>
    /// True where <see cref="FindExtensionThrough"/>, looking for a method as compare does, finds
    /// nothing only because a method of <paramref name="type"/> of the name, or with a hint one it
    /// fits, comes first: the name and hint would find an extension method of this assembly
    /// through the type, the type's members aside. Two builds of an assembly can differ in it
    /// while both declare the method, where each reads its own version of the type's library, or
    /// where the library is not found and only one of them references such a method.
    /// </summary>
    public bool FindsExtensionBehindMember(ExternalTypeName type, string memberName, MethodHint hint)
    {
        hint ??= MethodHint.None;
        return LookThrough(type, memberName, hint) is { } through
            && ExtensionCandidates(through.Members, through.Named, through.LookedFor, hint, methodsOnly: true) is null
            && ExtensionCandidates(TypeMembers.None, through.Named, through.LookedFor, hint, methodsOnly: true) is not null;
    }

    /// <summary>
    /// True where code cannot call <paramref name="method"/>, an extension method of this assembly
    /// whose receiver is a type parameter, on <paramref name="type"/> by C#'s rule
    /// (<see cref="AssemblyHost.FitsExtension"/>), which keeps a lookup through the type from
    /// finding it; false for any other method.
    /// </summary>
    /// <remarks>
    /// False where a base class of the type could not be read, since the check then cannot see
    /// whether that class brings what the constraints ask for.
    /// </remarks>
    public bool DoesNotFit(ITypeDefinition type, IMethod method) =>
        method.IsExtensionMethod && TypeWalker.ExtendedType(method) is ITypeParameter
        && BasesOf(type).Unread is null && !_fitsExtension(type, method);

    /// <summary>
    /// The line an answer carries that names <paramref name="method"/>, an extension method found
    /// through <paramref name="type"/>, a type of this assembly, while a base class of the type
    /// could not be read: a member of the method's name there would be what C# binds, as the
    /// route through another library's type says (<see cref="FindExtensionThrough"/>). Null for a
    /// method of the type or of a base class read, and for a type of another assembly, whose
    /// route carries the line itself.
    /// </summary>
    public string ExtensionNotChecked(ITypeDefinition type, IMethod method) =>
        method.IsExtensionMethod && !IsExternal(type)
        && !Levels(type).Any(level => level.Level.Equals(method.DeclaringTypeDefinition))
        && BasesOf(type).Unread is { } unread
            ? NotReadLine(unread.TypeName, method.Name, unread.Reason)
            : null;

    /// <summary>
    /// <see cref="DoesNotFit(ITypeDefinition, IMethod)"/> for a type of another assembly, read from
    /// its library; false where the library was not found, since nothing shows whether it fits.
    /// </summary>
    public bool DoesNotFit(ExternalTypeName type, IMethod method) =>
        _readExternalType(type.LookupNames) is { } definition && DoesNotFit(definition, method);

    /// <summary>
    /// A lookup through a type of another assembly, as <see cref="LookThrough"/> prepares it: the
    /// type as its library reads it, or null where the library was not found; the extension
    /// methods of this assembly a name finds for it; the members that come before them; and the
    /// name to look for.
    /// </summary>
    private sealed record Through(ITypeDefinition Definition, Func<string, List<IMethod>> Named,
        TypeMembers Members, string LookedFor);

    /// <summary>
    /// What a lookup of <paramref name="memberName"/> through <paramref name="type"/> works with,
    /// or null for a name no extension method of this assembly bears, with or without type
    /// parameters in brackets, which finds none and needs neither the other library nor the
    /// references read.
    /// </summary>
    private Through LookThrough(ExternalTypeName type, string memberName, MethodHint hint)
    {
        ValidateDisambiguationArgs(hint);
        if (!_extensionMethodsByName.Value[memberName].Any()
            && !_extensionMethodsByName.Value[ReadTypeArgumentCount(memberName).Name].Any())
            return null;

        var definition = _readExternalType(type.LookupNames);
        bool Receives(ITypeDefinitionOrUnknown receiver) => type.Matches(receiver.FullTypeName);
        // Without the type's library its kind and what it derives from are unknown, so of the
        // methods whose receiver is a type parameter only one that every type meets is found.
        static bool FitsAnyType(IMethod method) =>
            TypeWalker.ExtendedType(method) is ITypeParameter parameter && TypeWalker.ConstrainsNothing(parameter);
        List<IMethod> Named(string name) => definition is null
            ? FindExtensionMethodsByName(Receives, FitsAnyType, name)
            : FindExtensionMethodsByName(definition, name);
        return new Through(definition, Named,
            definition is null ? ReferencedMembers(type) : ReachableMembers(definition),
            definition is null ? memberName : ConstructorNameFor(definition, memberName));
    }

    /// <summary>
    /// The members of a type of another assembly that a lookup through it weighs an extension
    /// method against (<see cref="ExtensionCandidates"/>): the methods, accessors among them, that
    /// come first for a name without a hint; those a hint is held against, which can be more;
    /// whether a hint fits one of those, held exactly or loosely; and the names of its other
    /// members, the properties, fields and events.
    /// </summary>
    private sealed record TypeMembers(IReadOnlyList<IMethod> Methods, IReadOnlyList<IMethod> HintedMethods,
        Func<IMethod, MethodHint, bool, bool> Fits, IReadOnlySet<string> OtherNames)
    {
        /// <summary>No members, for a lookup that sets the type's members aside.</summary>
        public static TypeMembers None { get; } = new([], [], (_, _, _) => false, new HashSet<string>());
    }

    /// <summary>
    /// The members of <paramref name="type"/>, a type of another assembly read from its library,
    /// and of its base classes or, for an interface, its base interfaces, that come before an
    /// extension method of their name. For a name without a hint, those are the instance members
    /// code of this assembly can reach by C#'s rule, as ILSpy's resolver reads it from outside the
    /// type's classes: the public ones, and the internal and protected internal ones where the
    /// library's InternalsVisibleTo names this assembly. C# discards a static method for a call on
    /// a value from version 7.3, and reaches a protected or private protected member only from a
    /// class derived from its own, where a call on a receiver of another class binds the extension
    /// method all the same. A hint names a declaration rather than a call, so it is held against
    /// every method code of this assembly can name, static or not: those, the protected ones, and
    /// the private protected ones where the library grants this assembly its internals.
    /// </summary>
    private TypeMembers ReachableMembers(ITypeDefinition type)
    {
        var levels = Levels(type).Select(level => level.Level).Concat(TypeWalker.WalkBaseInterfaces(type).Read).ToList();
        var module = _decompilerTypeSystem().MainModule;
        var lookup = new MemberLookup(currentTypeDefinition: null, module, isInEnumMemberInitializer: false);
        var methods = levels.SelectMany(MethodsAndAccessors).ToList();
        return new(
            [.. methods.Where(Reaches)],
            [.. methods.Where(Names)],
            (method, hint, exact) => hint.Fits(method, exact),
            levels.SelectMany(level => level.Properties.Cast<IMember>().Concat(level.Fields).Concat(level.Events))
                .Where(Reaches).Select(member => member.Name).ToHashSet(StringComparer.Ordinal));

        bool Reaches(IMember member) => !member.IsStatic && lookup.IsAccessible(member, allowProtectedAccess: false);

        bool Names(IMember member) =>
            member.Accessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal
            || lookup.IsAccessible(member, allowProtectedAccess: false)
            || (member.Accessibility == Accessibility.ProtectedAndInternal && member.ParentModule.InternalsVisibleTo(module));
    }

    /// <summary>
    /// The members this assembly's references name on <paramref name="type"/>, which stand in for
    /// the type's members where its library was not found, as they do in the answers about such
    /// a type's members. A method is the one the decompiler's type system resolves its reference
    /// to, held against a hint with the types its reference row gives, as those answers hold it;
    /// one whose reference could not be resolved is known by its name alone. A property or an
    /// event is known by its accessors, and a field or a property an attribute sets by name by
    /// that attribute. For a name without a hint a static method does not come first, as on a
    /// type whose library was read (<see cref="ReachableMembers"/>); its row's signature says that
    /// it takes no instance. A hint is held against every method the rows name. A field's row does
    /// not say whether the field is static, and no row says whether a member is protected, so such
    /// a field and a protected member come first.
    /// </summary>
    private TypeMembers ReferencedMembers(ExternalTypeName type)
    {
        var (members, setByName) = _referencedMembersOf(type);
        var rows = new Dictionary<IMethod, TypeUsageScanner.ReferencedMember>(ReferenceEqualityComparer.Instance);
        var otherNames = new HashSet<string>(setByName, StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member.IsField || member.Called is null)
            {
                otherNames.Add(member.Name);
                continue;
            }
            rows[member.Called] = member;
            if (!member.Called.IsStatic && SplitAccessorPrefix(member.Name) is ({ }, var owner))
                otherNames.Add(owner);
        }
        return new([.. rows.Keys.Where(method => !method.IsStatic)], [.. rows.Keys],
            (method, hint, exact) => hint.Fits(rows[method].ParameterTypes, rows[method].ReturnType, exact),
            otherNames);
    }

    /// <summary>
    /// The line an answer found through a type of another assembly carries when
    /// <paramref name="typeName"/>, that type or one of its base classes, could not be read:
    /// a member of the name there would be what C# binds.
    /// </summary>
    private static string NotReadLine(string typeName, string memberName, string reason) =>
        $"Not checked: whether {typeName} or its base types have a member {memberName}, which C# would take first; {reason}";

    /// <summary>
    /// The extension method <paramref name="methodName"/> and <paramref name="hint"/> select among
    /// those <paramref name="named"/> finds for a name, where none of the type's
    /// <paramref name="members"/> answers first (<see cref="ExtensionCandidates"/>). Null where a
    /// member of the type answers or nothing fits, and an error where several fit alike.
    /// </summary>
    private (IMethod Method, MemberOrigin Origin)? FindExtensionBeside(TypeMembers members,
        Func<string, List<IMethod>> named, string methodName, MethodHint hint, bool methodsOnly)
    {
        if (ExtensionCandidates(members, named, methodName, hint, methodsOnly) is not { } candidates)
            return null;
        var found = DisambiguateMethod(candidates.Extensions, "(extension methods)", methodName, hint, candidates.Exact);
        return (found, MemberOrigin.ExtensionOn(ReferenceFormatter.FormatExtendedType(found)));
    }

    /// <summary>
    /// The extension methods of the name that a lookup chooses among, of those
    /// <paramref name="named"/> finds, and whether it holds <paramref name="hint"/> exactly there,
    /// where none of the type's <paramref name="members"/> answers first, by the order of
    /// <see cref="TryFindMethod"/>: a member of the name, or with a hint a method it fits, before
    /// an extension method, the hint held exactly through both before it is held loosely. With
    /// <paramref name="methodsOnly"/>, a property, field or event of the name does not answer
    /// first, as for a question that looks for a method by name through a type of this assembly.
    /// A hint is held against the type's methods a hint can name, which can be more than those that
    /// come first for the name alone (<see cref="TypeMembers"/>). Null where a member of the type
    /// answers or nothing fits.
    /// </summary>
    private static (List<IMethod> Extensions, bool Exact)? ExtensionCandidates(TypeMembers members,
        Func<string, List<IMethod>> named, string methodName, MethodHint hint, bool methodsOnly)
    {
        var methods = (hint.IsGiven ? members.HintedMethods : members.Methods).ToList();
        var (name, fits, leavesOutGeneric) = ByTypeParameters(methods, named, methodName);
        if (!hint.IsGiven
            && (methods.Any(method => method.Name == name && fits(method))
                || (!methodsOnly && members.OtherNames.Contains(methodName))))
            return null;

        bool[] fitsInOrder = hint.HasPatterns ? [true, false] : [false];
        Func<IMethod, bool>[] keepsInOrder = hint.IsGiven && leavesOutGeneric ? [fits, _ => true] : [fits];
        foreach (var exact in fitsInOrder)
        {
            foreach (var keeps in keepsInOrder)
            {
                // A method of the type that the hint fits is what the name and the hint mean.
                if (hint.IsGiven && methods.Any(method => method.Name == name && keeps(method) && members.Fits(method, hint, exact)))
                    return null;
                var extensions = named(name).Where(keeps).ToList();
                if (extensions.Count == 0 || (hint.IsGiven && NarrowByHint(extensions, hint, exact).Count == 0))
                    continue;
                return (extensions, exact);
            }
        }
        return null;
    }

    // --- Array types -------------------------------------------------------------

    /// <summary>
    /// An array type a question names (<see cref="ReadArray"/>): the array as the decompiler's
    /// type system holds it; the array as an answer names it, with its element's namespace,
    /// <c>System.Byte[]</c>; and the types in it whose library ILens could not load, which stand
    /// as types known by name alone, so what they derive from is not known.
    /// </summary>
    public sealed record NamedArray(IType Type, string Display, IReadOnlyList<UnreadBase> Unread);

    /// <summary>
    /// The C# keywords an array's element can be written with, and the types they name, as
    /// <see cref="ReferenceFormatter.KeywordFor"/> writes those types; void names no element.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, KnownTypeCode> KeywordTypes =
        KnownTypeReference.AllKnownTypes
            .Where(known => known.TypeParameterCount == 0)
            .Select(known => (Keyword: ReferenceFormatter.KeywordFor(known.Namespace, known.Name), known.KnownTypeCode))
            .Where(pair => pair.Keyword is not null and not "void")
            .ToDictionary(pair => pair.Keyword, pair => pair.KnownTypeCode, StringComparer.Ordinal);

    /// <summary>True for a name that ends in the brackets of an array as C# writes one.</summary>
    private static bool EndsInArrayBrackets(string typeName) =>
        !string.IsNullOrWhiteSpace(typeName) && TypeMatcher.NamesAnArray(typeName.Trim(), out _, out _);

    /// <summary>
    /// True for a name that names an array type as C# writes one, <c>byte[]</c> or
    /// <c>Shop.Part[][,]</c>, and no type the assembly defines, references or forwards, which IL
    /// would let bear it. An array has no members of its own in an assembly, so only the questions about
    /// the extension methods code can call on it take such a name (<see cref="ReadArray"/>), and
    /// every other one refuses it (<see cref="ArrayNotTaken"/>).
    /// </summary>
    public bool NamesArray(string typeName) => EndsInArrayBrackets(typeName) && Lookup(typeName).Count == 0;

    /// <summary>The error for an array type where a question takes none: the questions that do.</summary>
    public static ArgumentException ArrayNotTaken(string typeName) =>
        new($"{typeName.Trim()} is an array type, which only analyze with kind " +
            $"'{AnalysisKind.ExtensionMethods.ToSchemaName()}' and decompile, analyze and compare with " +
            $"{ParameterNames.MemberName} take, to find the extension methods code can call on it.");

    /// <summary>
    /// The array type <paramref name="typeName"/> names (<see cref="NamesArray"/>), built in the
    /// decompiler's type system, or null for a name that names none. The brackets that end the
    /// name are read from the outermost array inward, as C# writes them. The element is a C#
    /// keyword or a type's name, read by <see cref="Lookup"/>'s rules among the types the
    /// assembly defines, references and forwards and failing those among the libraries the
    /// decompiler loaded, where <c>System.Byte</c> is found when the assembly writes it only as a
    /// type code.
    /// An element may carry type arguments, each read as the element is, and a '?' after a value
    /// type makes it nullable. Throws where a type in the name matches no type, or several; with
    /// <paramref name="missingIsNull"/> a type that matches none gives null instead, for one of
    /// two builds of an assembly, which may lack it.
    /// </summary>
    public NamedArray ReadArray(string typeName, bool missingIsNull = false)
    {
        if (!NamesArray(typeName))
            return null;
        var unread = new List<UnreadBase>();
        return ReadArrayPart(typeName.Trim(), unread, missingIsNull) is { } type
            ? new NamedArray(type, ReferenceFormatter.FormatTypeName(type), unread)
            : null;
    }

    /// <summary>
    /// One type of an array's name, read as <see cref="ReadArray"/> says, in the decompiler's
    /// type system: the array itself, its element, or one of the element's type arguments. A type
    /// whose library was not loaded stands as one known by name alone and goes into
    /// <paramref name="unread"/>.
    /// </summary>
    private IType ReadArrayPart(string text, List<UnreadBase> unread, bool missingIsNull)
    {
        text = text.Trim();
        var typeSystem = _decompilerTypeSystem();
        if (TypeMatcher.NamesAnArray(text, out var element, out var rank))
            return ReadArrayPart(element, unread, missingIsNull) is { } elementType
                ? new ArrayType(typeSystem, elementType, rank, Nullability.Oblivious)
                : null;
        if (text.EndsWith('?'))
        {
            // After a value type the '?' makes it nullable; after any other type it is the
            // nullable annotation, which tells no two types apart.
            if (ReadArrayPart(text[..^1], unread, missingIsNull) is not { } underlying)
                return null;
            return underlying.IsReferenceType == false ? NullableType.Create(typeSystem, underlying) : underlying;
        }
        if (KeywordTypes.TryGetValue(text, out var code))
            return typeSystem.FindType(code);

        var lookup = Lookup(text);
        if (lookup.Count > 1)
            throw AmbiguousType(text, lookup);
        IType generic;
        if (lookup.Defined.Count == 1)
            generic = ((MetadataModule)typeSystem.MainModule).GetDefinition((TypeDefinitionHandle)lookup.Defined[0].MetadataToken);
        else if (lookup.Referenced.Count == 1 || lookup.Forwarded.Count == 1)
        {
            var name = lookup.Referenced.Count == 1 ? lookup.Referenced[0].Name : lookup.Forwarded[0].Name;
            generic = _readExternalType([name]);
            if (generic is null)
            {
                unread.Add(new UnreadBase(ReferenceFormatter.FormatTypeName(name), _unreadLibrariesOf(name)));
                generic = new UnknownType(name, null);
            }
        }
        else if (lookup.Parsed is { } parsed && _readExternalType(Readings(parsed)) is { } loaded)
            generic = loaded;
        else if (missingIsNull)
            return null;
        else
            throw NotDefined(text, lookup);

        var arguments = lookup.Parsed?.Segments.Where(segment => segment.Arguments != null)
            .SelectMany(segment => segment.Arguments).ToList() ?? [];
        if (arguments.Count == 0 && generic.TypeParameterCount == 0)
            return generic;
        if (arguments.Count != generic.TypeParameterCount)
            throw new ArgumentException(
                $"An array type writes its element with its type arguments, as C# does: {text} names " +
                $"{ReferenceFormatter.FormatTypeName(generic)}, which takes {generic.TypeParameterCount}.");
        var typeArguments = new List<IType>();
        foreach (var argument in arguments)
        {
            if (ReadArrayPart(argument, unread, missingIsNull) is not { } argumentType)
                return null;
            typeArguments.Add(argumentType);
        }
        return new ParameterizedType(generic, typeArguments);
    }

    /// <summary>
    /// What a lookup of a name through an array type finds
    /// (<see cref="FindExtensionThroughArray"/>): the extension method,
    /// or the name of the class that declares the member of System.Array or System.Object that
    /// comes first, which the question is then about. Both are null where the name finds neither,
    /// and <see cref="ArrayMemberNotFound"/> is the error then.
    /// </summary>
    public sealed record ArrayLookup(ExtensionThrough Extension, string MemberOwner);

    /// <summary>
    /// What <paramref name="memberName"/> names through <paramref name="array"/>, as C# binds a
    /// call on such an array: a public instance member of System.Array or System.Object of the
    /// name, or with a hint such a method that fits it, comes first, in the order
    /// <see cref="TryFindMethod"/> keeps; then an extension method of the name that the decompiler's
    /// rule lets code call on the array (<see cref="AssemblyHost.FitsExtension"/>): one for the
    /// array itself, for an array its element converts to, for an interface of the array, for
    /// System.Array or System.Object, and one whose receiver is a type parameter or an array of
    /// one that the array fits. A static member of System.Array does not come first, since C#
    /// discards a static method called on a value from version 7.3. With
    /// <paramref name="methodsOnly"/>, for a question that looks for a method by name, a property
    /// of the name does not come first either, as through a type of another assembly
    /// (<see cref="FindExtensionThrough"/>).
    /// </summary>
    /// <remarks>
    /// Of 478 extension methods for an array counted in a game's libraries, mods, the .NET 10
    /// runtimes and the SDK on 2026-10-17, 32 bear the name of a static method of System.Array,
    /// such as Reverse, Resize or IndexOf, and 30 that of an instance member, all of them
    /// MemoryExtensions.CopyTo beside Array.CopyTo. C# 14 also converts an array to Span&lt;T&gt;
    /// and ReadOnlySpan&lt;T&gt; for an extension method's receiver, which the decompiler's rule,
    /// written before it, does not, so an extension method for a span is not found through an
    /// array. The rule also leaves out IReadOnlyCollection&lt;T&gt;, which
    /// <see cref="FitsArray"/> adds.
    /// </remarks>
    public ArrayLookup FindExtensionThroughArray(NamedArray array, string memberName, MethodHint hint,
        bool methodsOnly = false)
    {
        hint ??= MethodHint.None;
        ValidateDisambiguationArgs(hint);
        var named = ArrayExtensionsNamed(array);
        var members = ArrayMembers(out var levels, out var unreadCore);
        if (FindExtensionBeside(members, named, memberName, hint, methodsOnly) is { } found)
        {
            var notChecked = unreadCore is not null
                ? NotReadLine(unreadCore.TypeName, memberName, unreadCore.Reason)
                : array.Unread.Count > 0
                  && ExtensionMethodsNamed(found.Method.Name).Any(method => TurnsOnElement(array, method) && !AppliesTo(array, method))
                    ? $"Not checked: whether other extension methods named {found.Method.Name} apply to {array.Display} " +
                      $"through what {DerivesFrom(array.Unread)}; {UnreadReason(array.Unread)}"
                    : null;
            return new ArrayLookup(
                new ExtensionThrough(found.Method, found.Origin, named(found.Method.Name), null, notChecked), null);
        }
        return new ArrayLookup(null, ArrayMemberOwner(levels, members, named, memberName, hint));
    }

    /// <summary>
    /// True where whether <paramref name="method"/> applies to <paramref name="array"/> can turn on
    /// what the array's element derives from: its receiver is an array of as many dimensions, an
    /// interface, a type ILens knows by name alone, or names a type parameter. Any other receiver
    /// an array converts to is System.Array or System.Object, which every array does.
    /// </summary>
    private static bool TurnsOnElement(NamedArray array, IMethod method) => TypeWalker.ExtendedType(method) switch
    {
        ArrayType receiver => array.Type is ArrayType asked && receiver.Dimensions == asked.Dimensions,
        var receiver => receiver.Kind is TypeKind.Interface or TypeKind.Unknown || TypeWalker.NamesTypeParameter(receiver),
    };

    /// <summary>
    /// Where <paramref name="owner"/>, System.Array or System.Object, comes from, read in the
    /// decompiler's type system, for a question through an array that a member of that class
    /// answers first (<see cref="ArrayLookup.MemberOwner"/>) where this assembly neither defines,
    /// references nor forwards the class, so that a question through the class by name finds nothing; as
    /// <c>Lib, Version=1.0.0.0</c>. Null where the assembly names the class, or the class was not read.
    /// </summary>
    public string UnnamedOwnerOrigin(string owner) =>
        Lookup(owner).Count == 0
        && _readExternalType([new TopLevelTypeName("System", owner[(owner.LastIndexOf('.') + 1)..])])?.ParentModule is { } module
            ? $"{module.AssemblyName}, Version={module.AssemblyVersion}"
            : null;

    /// <summary>
    /// True where <see cref="FindExtensionThroughArray"/>, looking for a method as compare does,
    /// finds no extension method only because a method of System.Array or System.Object comes
    /// first, as <see cref="FindsExtensionBehindMember"/> says it for a type of another assembly.
    /// </summary>
    public bool FindsExtensionBehindArrayMember(NamedArray array, string memberName, MethodHint hint)
    {
        hint ??= MethodHint.None;
        var named = ArrayExtensionsNamed(array);
        return ExtensionCandidates(ArrayMembers(out _, out _), named, memberName, hint, methodsOnly: true) is null
            && ExtensionCandidates(TypeMembers.None, named, memberName, hint, methodsOnly: true) is not null;
    }

    /// <summary>
    /// True where code cannot call <paramref name="method"/>, an extension method of this assembly
    /// whose receiver names a type parameter, on <paramref name="array"/> by C#'s rule, which keeps
    /// a lookup through the array from finding it; false for any other method, and where a type in
    /// the array was not read, since the rule then cannot see what that type derives from.
    /// </summary>
    public bool DoesNotFit(NamedArray array, IMethod method) =>
        method.IsExtensionMethod && TypeWalker.NamesTypeParameter(TypeWalker.ExtendedType(method))
        && array.Unread.Count == 0 && !FitsArray(array, method);

    /// <summary>
    /// The error for a name a lookup through <paramref name="array"/> finds neither as a member of
    /// System.Array or System.Object nor as an extension method (<see cref="ArrayLookup"/>): with
    /// a hint that fits none of the methods of the name, those methods; otherwise what was searched,
    /// and what was not read.
    /// </summary>
    public ArgumentException ArrayMemberNotFound(NamedArray array, string memberName, MethodHint hint)
    {
        hint ??= MethodHint.None;
        var members = ArrayMembers(out _, out var unreadCore);
        var named = ArrayExtensionsNamed(array);
        var (name, _, _) = ByTypeParameters(members.Methods.ToList(), named, memberName);
        var candidates = members.Methods.Where(method => method.Name == name).Concat(named(name)).ToList();
        var unread = (unreadCore is null ? "" : $"; what System.Array and System.Object declare was not read, since {UnreadReason([unreadCore])}")
            + (array.Unread.Count == 0 ? "" : $"; what {DerivesFrom(array.Unread)} was not read, since {UnreadReason(array.Unread)}");
        if (hint.IsGiven && candidates.Count > 0)
            return new($"No overload of '{memberName}' on {array.Display} matches {hint.Describe()}. " +
                $"Available: {ReferenceFormatter.FormatMethodList(candidates)}{unread}.");
        var searched = unreadCore is null
            ? "no public instance member of System.Array or System.Object and no extension method of this assembly that applies to it has that name"
            : "no extension method of this assembly that applies to it has that name";
        return new($"Member '{memberName}' not found on {array.Display}: {searched}{unread}.");
    }

    /// <summary>
    /// The extension methods analyze with extension_methods lists for <paramref name="array"/>:
    /// those whose receiver is exactly that array, passed by value or by reference, and those whose
    /// receiver is an array naming a type parameter that the array fits by C#'s rule
    /// (<see cref="AssemblyHost.FitsExtension"/>). A method for an interface the array implements,
    /// for System.Array, for System.Object or for a type parameter itself is listed under that
    /// type, as for any type (<see cref="TypeWalker.ListedUnder"/>), and one for an array the
    /// element converts to under that array. With them the line that says what was not checked,
    /// where a type in the array was not read and the rule turned a method down, or null.
    /// </summary>
    public (IReadOnlyList<ISymbol> Methods, string NotChecked) ArrayExtensionMethods(NamedArray array)
    {
        var module = (MetadataModule)_typeSystem.MainModule;
        var methods = new List<ISymbol>();
        var turnedDown = false;
        foreach (var method in ExtensionMethods.Select(handle => module.GetDefinition(handle)))
        {
            if (TypeWalker.ExtendedType(method) is not ArrayType receiver)
                continue;
            if (!TypeWalker.NamesTypeParameter(receiver))
            {
                if (IdentityName(receiver) == IdentityName(array.Type))
                    methods.Add(method);
            }
            else if (_fitsExtension(array.Type, method))
                methods.Add(method);
            else
                turnedDown = true;
        }
        return (methods, turnedDown && array.Unread.Count > 0
            ? $"Not checked: whether more extension methods apply to {array.Display} through what " +
              $"{DerivesFrom(array.Unread)}; {UnreadReason(array.Unread)}"
            : null);
    }

    /// <summary>
    /// What the types of an array that were not read leave open, as a line that names them says
    /// it: <c>Lib.Widget derives from</c>.
    /// </summary>
    private static string DerivesFrom(IReadOnlyList<UnreadBase> unread)
    {
        var types = unread.Select(type => type.TypeName).Distinct(StringComparer.Ordinal).ToList();
        return $"{string.Join(" and ", types)} derive{(types.Count == 1 ? "s" : "")} from";
    }

    /// <summary>Why the types of an array that were not read were not read.</summary>
    private static string UnreadReason(IReadOnlyList<UnreadBase> unread) =>
        UnreadLibrary.Reason(unread.Select(type => (type.TypeName, type.Libraries)));

    /// <summary>
    /// The members of System.Array and System.Object that come before an extension method in a
    /// lookup through an array type: the public instance ones other than constructors, which code
    /// can call on an array, read in the decompiler's type system, and in
    /// <paramref name="levels"/> the two classes, Array first. A hint is held against the same
    /// methods: a static method of System.Array is asked about through System.Array itself, where
    /// a hint that fits it reaches it (<see cref="ReachableMembers"/>). Where that type system does
    /// not hold both, as when the core library was not found, there are none, and
    /// <paramref name="unread"/> names System.Array.
    /// </summary>
    private TypeMembers ArrayMembers(out IReadOnlyList<ITypeDefinition> levels, out UnreadBase unread)
    {
        var classes = new[] { "Array", "Object" }
            .Select(name => _readExternalType([new TopLevelTypeName("System", name)]))
            .ToList();
        if (classes.Any(found => found is null))
        {
            levels = [];
            unread = new UnreadBase("System.Array", _unreadLibrariesOf(new TopLevelTypeName("System", "Array")));
            return TypeMembers.None;
        }
        levels = classes;
        unread = null;
        List<IMethod> methods =
            [.. classes.SelectMany(MethodsAndAccessors).Where(method => !method.IsConstructor && IsCallableOnArray(method))];
        return new(
            methods,
            methods,
            (method, hint, exact) => hint.Fits(method, exact),
            classes.SelectMany(level => level.Properties.Cast<IMember>().Concat(level.Fields).Concat(level.Events))
                .Where(IsCallableOnArray).Select(member => member.Name).ToHashSet(StringComparer.Ordinal));

        static bool IsCallableOnArray(IMember member) => member.Accessibility == Accessibility.Public && !member.IsStatic;
    }

    /// <summary>
    /// The class of the member of System.Array or System.Object that comes before the extension
    /// methods in a lookup through an array (<see cref="FindExtensionThroughArray"/>),
    /// as an answer names it, or null where none does: without a hint the nearest class that has
    /// a member of the name, and with one the nearest that has a method the hint fits, held
    /// exactly through both classes before loosely.
    /// </summary>
    private static string ArrayMemberOwner(IReadOnlyList<ITypeDefinition> levels, TypeMembers members,
        Func<string, List<IMethod>> named, string memberName, MethodHint hint)
    {
        var (name, fits, _) = ByTypeParameters(members.Methods.ToList(), named, memberName);
        bool[] fitsInOrder = hint.HasPatterns ? [true, false] : [false];
        foreach (var exact in fitsInOrder)
        {
            foreach (var level in levels)
            {
                var methods = members.Methods.Where(method => method.Name == name
                    && method.DeclaringTypeDefinition?.ReflectionName == level.ReflectionName);
                var answers = hint.IsGiven
                    ? methods.Any(method => members.Fits(method, hint, exact))
                    : methods.Any(fits) || level.Properties.Cast<IMember>().Concat(level.Fields).Concat(level.Events)
                        .Any(member => member.Name == memberName && member.Accessibility == Accessibility.Public && !member.IsStatic);
                if (answers)
                    return ReferenceFormatter.FormatTypeName(level);
            }
        }
        return null;
    }

    /// <summary>
    /// The assembly's extension methods named <paramref name="name"/>, in the order of the types
    /// and their methods.
    /// </summary>
    private IEnumerable<IMethod> ExtensionMethodsNamed(string name)
    {
        var module = (MetadataModule)_typeSystem.MainModule;
        return _extensionMethodsByName.Value[name].Select(handle => module.GetDefinition(handle));
    }

    /// <summary>
    /// The extension methods of a name that code can call on <paramref name="array"/> by C#'s
    /// rule (<see cref="AssemblyHost.FitsExtension"/>), whatever their receiver, each name checked
    /// once for the lookup it serves.
    /// </summary>
    private Func<string, List<IMethod>> ArrayExtensionsNamed(NamedArray array)
    {
        var checkedNames = new Dictionary<string, List<IMethod>>(StringComparer.Ordinal);
        return name => checkedNames.TryGetValue(name, out var methods)
            ? methods
            : checkedNames[name] = [.. ExtensionMethodsNamed(name).Where(method => AppliesTo(array, method))];
    }

    /// <summary>
    /// True where code can call <paramref name="method"/>, an extension method of this assembly, on
    /// <paramref name="array"/>: its receiver is that array, or the decompiler's rule takes it
    /// (<see cref="FitsArray"/>).
    /// </summary>
    /// <remarks>
    /// The receiver is compared by name first (<see cref="IdentityName"/>), since a type whose
    /// library was not loaded stands as an unknown type, which equals another only where both say
    /// alike whether it is a class, and a signature says so where a name does not.
    /// </remarks>
    private bool AppliesTo(NamedArray array, IMethod method) =>
        (TypeWalker.ExtendedType(method) is ArrayType receiver && IdentityName(receiver) == IdentityName(array.Type))
        || FitsArray(array, method);

    /// <summary>
    /// <paramref name="type"/>'s reflection name with the spellings C# takes for one type made one:
    /// dynamic as System.Object, nint and nuint as System.IntPtr and System.UIntPtr, and a tuple as
    /// its ValueTuple, in an array's element and in type arguments as well. A receiver the decompiled
    /// code writes with one spelling names the array a question writes with the other, as
    /// <c>nint[]</c> and <c>System.IntPtr[]</c> do. Compared as text, it matches across ILens's and
    /// the decompiler's type systems.
    /// </summary>
    private static string IdentityName(IType type) => type.Kind switch
    {
        TypeKind.Dynamic => "System.Object",
        TypeKind.NInt => "System.IntPtr",
        TypeKind.NUInt => "System.UIntPtr",
        _ => type switch
        {
            ArrayType array => IdentityName(array.ElementType) + "[" + new string(',', array.Dimensions - 1) + "]",
            TupleType tuple => IdentityName(tuple.UnderlyingType),
            ParameterizedType constructed => IdentityName(constructed.GenericType)
                + "[" + string.Join(",", constructed.TypeArguments.Select(IdentityName)) + "]",
            _ => type.ReflectionName,
        },
    };

    /// <summary>
    /// True where the decompiler's rule (<see cref="AssemblyHost.FitsExtension"/>) lets code call
    /// <paramref name="method"/> on <paramref name="array"/>, or, for a receiver of
    /// IReadOnlyCollection&lt;T&gt; and an array of one dimension, on the array's
    /// IReadOnlyList&lt;T&gt;, which the array converts to and which converts to that receiver.
    /// </summary>
    /// <remarks>
    /// C# converts an array of one dimension to IReadOnlyCollection&lt;T&gt; as to the other generic
    /// interfaces it implements. The conversions of ICSharpCode.Decompiler 9.1
    /// (CSharpConversions.UnpackGenericArrayInterface) leave that one out, so the rule alone turned
    /// down 65 such methods in 14 files of the .NET SDK, counted on 2026-10-18, which C# calls on an
    /// array; ILSpy 10.1.1 and 11.1 have the case. Asked through IReadOnlyList&lt;T&gt;, the rule
    /// still infers the type arguments and checks the constraints, with the array's element in the
    /// place of T.
    /// </remarks>
    private bool FitsArray(NamedArray array, IMethod method)
    {
        if (_fitsExtension(array.Type, method))
            return true;
        var receiver = TypeWalker.ExtendedType(method);
        var generic = receiver is ParameterizedType parameterized ? parameterized.GenericType : receiver;
        return array.Type is ArrayType { Dimensions: 1 } single
            && ReferenceFormatter.FullTypeNameOf(generic)?.ReflectionName == "System.Collections.Generic.IReadOnlyCollection`1"
            && _decompilerTypeSystem().FindType(KnownTypeCode.IReadOnlyListOfT) is ITypeDefinition readOnlyList
            && _fitsExtension(new ParameterizedType(readOnlyList, [single.ElementType]), method);
    }

    /// <summary>
    /// The requested type, then its base-class chain, most-derived first, each with the origin
    /// a member found there has relative to the requested type. The chain goes on past the base
    /// classes the assembly defines into those another assembly defines, as far as the
    /// assembly's references could be read. This assembly holds no code of a member found
    /// there, so <see cref="RequireOwn"/> turns it into an error naming where it comes from.
    /// Such a member still ends the search, as one on the assembly's own classes does, so an
    /// extension method or explicit implementation of the name is not taken in its place, unless
    /// the type's code cannot reach it (<see cref="Sees"/>). C# passes over such a member, and the
    /// search takes it only in its last try, where nothing else answers to the name. Only the last
    /// try says why nothing answers, since only it sees every member: in the first, a hint that
    /// fits no overload the type reaches would read as fitting none at all, and a setter the type
    /// cannot reach as one its property lacks.
    /// </summary>
    private IEnumerable<(ITypeDefinition Level, MemberOrigin Origin)> Levels(ITypeDefinition type)
    {
        yield return (type, MemberOrigin.Declared);
        foreach (var baseType in TypeWalker.WalkBaseTypes(type))
            yield return (baseType, MemberOrigin.InheritedFrom(ReferenceFormatter.FormatTypeName(baseType)));
        foreach (var level in BasesOf(type).Levels.Select(baseType => baseType.GetDefinition()))
            yield return (level, MemberOrigin.InheritedFrom(ReferenceFormatter.FormatTypeName(level)));
    }

    /// <summary>
    /// The base classes of <paramref name="type"/> that an assembly other than its own defines.
    /// For a type of this assembly they are read where the decompiler finds them
    /// (<see cref="_readExternalBases"/>). A type of another assembly, which a lookup through a
    /// type the assembly only references walks (<see cref="FindExtensionThrough"/>), is already
    /// one of that type system, so they are read in it.
    /// </summary>
    private ExternalBases BasesOf(ITypeDefinition type) =>
        IsExternal(type) ? TypeWalker.WalkExternalBaseTypes(type) : _readExternalBases(type);

    /// <summary>
    /// The levels a method of that name is looked for on: <see cref="Levels"/>, and for a
    /// constructor the type alone, since a constructor is not inherited. Walking on would
    /// answer a question about a struct's constructor with <c>System.ValueType</c>'s, and a
    /// hint no constructor of a class satisfies with a base class's constructor that does.
    /// </summary>
    private IEnumerable<(ITypeDefinition Level, MemberOrigin Origin)> LevelsFor(ITypeDefinition type, string methodName) =>
        methodName is ".ctor" or ".cctor" ? Levels(type).Take(1) : Levels(type);

    /// <summary>
    /// True for a member or type of an assembly other than the one this resolver reads, which
    /// <see cref="Levels"/> reaches on a base class another assembly defines.
    /// </summary>
    public bool IsExternal(IEntity entity) => entity.ParentModule != _typeSystem.MainModule;

    /// <summary>
    /// True where the search for a member of <paramref name="type"/> sees
    /// <paramref name="member"/>, a member of a class it walks: any member of a class of this
    /// assembly, whose code is here, and a member of a base class another library defines that
    /// code of the type can reach by C#'s rule, as ILSpy's resolver reads it: a public, protected
    /// or protected internal one, and an internal or private protected one where that library's
    /// InternalsVisibleTo names this assembly. C# passes over a private member there and binds an
    /// extension method of its name, so the search passes over it too.
    /// </summary>
    private bool Sees(ITypeDefinition type, IMember member)
    {
        if (!IsExternal(member))
            return true;
        var typeSystem = _decompilerTypeSystem();
        var current = IsExternal(type)
            ? type
            : ((MetadataModule)typeSystem.MainModule).GetDefinition((TypeDefinitionHandle)type.MetadataToken);
        return new MemberLookup(current, typeSystem.MainModule, isInEnumMemberInitializer: false)
            .IsAccessible(member, allowProtectedAccess: true);
    }

    /// <summary>
    /// Throws when <paramref name="found"/> lies on a base class another assembly defines. This
    /// assembly holds neither its code nor anything about it beyond the calls it makes, so the
    /// error names it, every member of that name the hint leaves on its class, and the file the
    /// class comes from, where its code can be read.
    /// </summary>
    private void RequireOwn(ITypeDefinition type, IEntity found, MethodHint hint = null)
    {
        if (!IsExternal(found))
            return;

        var declaringType = found.DeclaringTypeDefinition;
        IReadOnlyList<IEntity> named = found is IMethod
            ? NarrowByHint(MethodsAndAccessors(declaringType).Where(m => m.Name == found.Name).ToList(),
                hint ?? MethodHint.None)
            : [found];
        var file = (declaringType.ParentModule as MetadataModule)?.MetadataFile.FileName;
        throw new ArgumentException(
            $"{ReferenceFormatter.FormatTypeName(type)} inherits {ReferenceFormatter.FormatList(named)} from a base " +
            $"class this assembly does not define; {ReferenceFormatter.FormatTypeName(declaringType)} comes from {file}.");
    }

    /// <summary>
    /// The error for a name the search did not find. When the walk up the base classes stopped
    /// at one ILens could not read, the error says so, since that class may declare the name.
    /// Where a usual name does not apply, it names what does, see <see cref="MeantClause"/>.
    /// </summary>
    private ArgumentException NotFound(string kind, string name, ITypeDefinition type, bool searchesExtensions)
    {
        var unread = BasesOf(type).Unread;
        var bases = unread is null ? "its base types" : "the base types ILens could load";
        var searched = searchesExtensions
            ? $"{ReferenceFormatter.FormatTypeName(type)}, {bases}, or as an extension method"
            : $"{ReferenceFormatter.FormatTypeName(type)} or {bases}";
        return new ArgumentException(
            $"{kind} '{name}' not found on {searched}{MeantClause(type, name)}{UnreadClause(unread)}.");
    }

    /// <summary>
    /// The clause of <see cref="NotFound"/> for a usual name the type has another one for, and
    /// nothing for any other name. A method name with type parameters in brackets gets the
    /// methods of its name, when only the count does not fit. C# names an indexer <c>Item</c> unless the class carries
    /// <c>[IndexerName]</c>, as <c>System.String</c> does with <c>Chars</c>, so a question for
    /// <c>Item</c> or its accessors gets the accessors the indexer has; the most derived level
    /// with an indexer decides, since an indexer there hides one further up. The type's own
    /// name, which reaches its constructors, gets <c>.cctor</c> when the only one is static.
    /// </summary>
    private string MeantClause(ITypeDefinition type, string name)
    {
        // A name whose brackets give a count of type parameters no method of the name has.
        if (ReadTypeArgumentCount(name) is (var bare, not null) && bare != name)
        {
            var overloads = Levels(type).SelectMany(level => MethodsAndAccessors(level.Level))
                .Where(method => method.Name == bare)
                .Concat(FindExtensionMethodsByName(type, bare))
                .ToList();
            if (overloads.Count > 0)
                return $"; the methods named {bare} are {ReferenceFormatter.FormatMethodList(overloads)}";
        }
        if (name is "get_Item" or "set_Item" or "Item")
        {
            var indexers = Levels(type)
                .Select(level => level.Level.Properties
                    .Where(property => property.IsIndexer && !property.IsExplicitInterfaceImplementation)
                    .ToList())
                .FirstOrDefault(here => here.Count > 0);
            if (indexers != null && indexers[0].Name != "Item")
            {
                var accessors = indexers
                    .SelectMany(indexer => new[] { indexer.Getter, indexer.Setter })
                    .Where(accessor => accessor != null)
                    .ToList();
                return $"; its indexer is named {indexers[0].Name}: {ReferenceFormatter.FormatMethodList(accessors)}";
            }
        }
        if ((name == ".ctor" || name == type.Name)
            && !type.DeclaredMethods().Any(method => method.IsConstructor && !method.IsStatic))
        {
            if (type.Kind == TypeKind.Struct)
                return "; a struct that declares no constructor has none in the assembly, and " +
                    $"{AnalysisKind.InstantiatedBy.ToSchemaName()} lists where it is created";
            if (type.Kind == TypeKind.Enum)
                return "; an enum has no constructor";
        }
        if (name == type.Name && type.DeclaredMethods().Any(method => method.IsConstructor && method.IsStatic))
            return "; its static constructor is named '.cctor'";
        return "";
    }

    /// <summary>
    /// The clause that ends an error when the walk up the base classes stopped at
    /// <paramref name="unread"/>, which ILens could not read, and nothing when it did not stop.
    /// </summary>
    private static string UnreadClause(UnreadBase unread) =>
        unread is null
            ? ""
            : $"; {unread.TypeName} and its base types were not searched, since {unread.Reason}";

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
        type.DeclaredMethods()
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
        string methodName, MethodHint hint, bool exact)
    {
        var candidates = NarrowByHint(methods, hint, exact);

        if (candidates.Count == 1)
            return candidates[0];

        // Control reaches here only with more than one candidate: every caller passes a set
        // the hint is known to leave non-empty — the no-hint set is returned whole, and the
        // hinted callers pre-check for at least one match before delegating here. A hint that
        // matches nothing is handled upstream by TryFindMethod's cross-chain error, which can
        // name the whole inheritance chain rather than just this one level's candidates.
        var described = candidates
            .Select(m => (ReferenceFormatter.FormatTypeArguments(m),
                string.Join(", ", m.Parameters.Select(p => ReferenceFormatter.FormatParameterType(p, full: true))),
                ReferenceFormatter.FormatReturnType(m, full: true)))
            .ToList();
        throw new ArgumentException(
            $"'{methodName}' on {context} has {candidates.Count} matching overloads; " +
            $"{OverloadAdvice(described, hint)}: {ReferenceFormatter.FormatMethodList(candidates)}.");
    }

    /// <summary>
    /// What the overload error advises for <paramref name="candidates"/>, which the hint leaves
    /// several of: what tells them apart. Candidates that each print their own type parameters
    /// are told apart by the brackets after the name, which a parameter hint may not do for
    /// them. Candidates that take the same parameter types, as ILens writes them in full, and
    /// each return another type are told apart by return_type alone. Otherwise the parameter
    /// types do. Where the hint gave the pattern that would tell them apart, it fits more than
    /// one candidate, as a short name fits classes of that name in two namespaces, and the type
    /// of the one meant is to be written as listed. What none of these tells apart cannot be
    /// narrowed.
    /// </summary>
    /// <param name="candidates">
    /// Each candidate's type parameters, as the brackets after its name write them, and its
    /// parameter types and return type, written in full.
    /// </param>
    /// <param name="hint">The hint that left them.</param>
    public static string OverloadAdvice(
        IReadOnlyList<(string TypeParameters, string Parameters, string ReturnType)> candidates, MethodHint hint)
    {
        if (candidates.Select(m => m.TypeParameters).Distinct().Count() == candidates.Count)
            return "write the type parameters of the one meant after its name, as listed";
        if (candidates.Select(m => m.Parameters).Distinct().Count() == 1)
        {
            if (candidates.Select(m => m.ReturnType).Distinct().Count() < candidates.Count)
                return "the candidates share their parameter types and cannot be narrowed further";
            return string.IsNullOrEmpty(hint.ReturnType)
                ? $"they differ in their return type alone, which {ParameterNames.ReturnType} selects"
                : "this return type fits more than one; write that of the one meant as listed";
        }
        if (hint.ParameterTypes != null)
            return "these parameter types fit more than one; write those of the one meant as listed";
        return hint.ParameterCount.HasValue
            ? $"they are same-arity overloads — specify {ParameterNames.ParameterTypes} to disambiguate"
            : $"specify {ParameterNames.ParameterCount} or {ParameterNames.ParameterTypes} to disambiguate";
    }

    /// <summary>
    /// Narrow a same-name candidate set to those <paramref name="hint"/> fits, exactly or
    /// loosely, or the set unchanged without a hint. A pure filter: the 0 / 1 / many outcomes
    /// are interpreted by the caller, which is what lets <see cref="TryFindMethod"/> keep
    /// walking the base chain when a level yields zero.
    /// </summary>
    private static List<IMethod> NarrowByHint(List<IMethod> methods, MethodHint hint, bool exact) =>
        hint.IsGiven ? methods.Where(m => hint.Fits(m, exact)).ToList() : methods;

    /// <summary>
    /// <see cref="NarrowByHint(List{IMethod}, MethodHint, bool)"/> with the hint held exactly
    /// where any candidate fits it so, and loosely otherwise, as a search holds it.
    /// </summary>
    private static List<IMethod> NarrowByHint(List<IMethod> methods, MethodHint hint) =>
        NarrowByHint(methods, hint, exact: true) is { Count: > 0 } exactly ? exactly : NarrowByHint(methods, hint, exact: false);

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
    /// Find extension methods for a type by name: the assembly's extension methods whose
    /// receiver, as <see cref="TypeWalker.ExtendedType"/> reads it, matches the target type or
    /// any of its supertypes (base classes, implemented interfaces, and <c>System.Object</c>), and
    /// those whose receiver is a type parameter that code can call on the target type by C#'s
    /// rule (<see cref="AssemblyHost.FitsExtension"/>), in the order of the types and their
    /// methods.
    /// </summary>
    private List<IMethod> FindExtensionMethodsByName(ITypeDefinition targetType, string methodName)
    {
        if (!_extensionMethodsByName.Value[methodName].Any())
            return [];

        // GetAllBaseTypes includes the type itself plus every supertype — classes,
        // interfaces, and System.Object — so 'this IEnumerable<T>' extensions resolve on a
        // concrete List<T> and 'this object' extensions resolve on any reference type.
        // TypeWalker.WalkBaseTypes (used for instance-member resolution) intentionally stops
        // before Object and skips interfaces; extension-method discovery needs the wider scope.
        // A type another assembly defines has no definition here, only the name its reference
        // gives it, and the base classes such a type has are read from the assembly's
        // references. A type that a lookup through another library's type passes in is read in
        // the decompiler's type system, as are those base classes, while each method's receiver
        // is read in this one. So types compare by ReflectionName, which matches across the two
        // type systems and, unlike FullName, tells 'this Task' from 'this Task<TResult>'.
        var targetTypes = new HashSet<string>(targetType.GetAllBaseTypes()
            .Concat(BasesOf(targetType).Levels.SelectMany(level => level.GetAllBaseTypes()))
            .Select(t => t.GetDefinitionOrUnknown()?.ReflectionName)
            .OfType<string>());
        return FindExtensionMethodsByName(receiver => targetTypes.Contains(receiver.ReflectionName),
            method => _fitsExtension(targetType, method), methodName);
    }

    /// <summary>
    /// The assembly's extension methods named <paramref name="methodName"/> whose receiver, as
    /// <see cref="TypeWalker.ExtendedType"/> reads it, <paramref name="receives"/> takes, or whose
    /// receiver is a type parameter and <paramref name="fits"/> takes the method, in the order of
    /// the types and their methods.
    /// </summary>
    private List<IMethod> FindExtensionMethodsByName(Func<ITypeDefinitionOrUnknown, bool> receives,
        Func<IMethod, bool> fits, string methodName)
    {
        var module = (MetadataModule)_typeSystem.MainModule;
        var results = new List<IMethod>();
        foreach (var handle in _extensionMethodsByName.Value[methodName])
        {
            var method = module.GetDefinition(handle);
            var extended = TypeWalker.ExtendedType(method);
            if (extended is ITypeParameter
                    ? fits(method)
                    : extended.GetDefinitionOrUnknown() is { } receiver && receives(receiver))
                results.Add(method);
        }

        return results;
    }

    /// <summary>
    /// The rows of the extension methods <paramref name="module"/> declares, in the order of the
    /// types and their methods: every method ILSpy's <c>IsExtensionMethod</c> holds for, a
    /// static method with ExtensionAttribute, whatever its class, so a Visual Basic module's and
    /// an F# class's count as a C# static class's do. That is the rule a declaration line writes
    /// <c>this</c> by, so the lookup finds every method <c>list_members</c> marks that way. A
    /// method without parameters extends nothing and is left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C#'s own rule for metadata, Roslyn's <c>PEMethodSymbol.IsExtensionMethod</c>, also wants
    /// the class marked, the receiver neither <c>out</c> nor <c>params</c>, and a <c>ref</c>
    /// receiver only on a value type. Of 39,964 methods counted in a game's libraries, mods, the
    /// .NET runtimes and the SDK on 2026-10-16, the two rules differ in two Visual Basic module
    /// methods whose receiver is a class passed <c>ByRef</c>, which Visual Basic calls as
    /// extension methods.
    /// </para>
    /// <para>
    /// Read once, at the first search for an extension method, from the method rows, and only a
    /// static method that carries an attribute is constructed to ask ILSpy. ILSpyX's
    /// LoadedAssembly creates ILens's type system with TypeSystemOptions.Uncached, so whatever a
    /// search constructs is built anew each time, and compare asked for a field by name makes
    /// eight searches. On the largest assembly of a game's libraries, of the .NET 10 runtime and
    /// of the SDK's Visual Basic libraries, the first search, which reads the table among other
    /// things, took 159, 101 and 86 ms in the median of five runs on 2026-10-16, and a search
    /// after it 3, 4 and 6 ms. The table holds only the rows, so a search constructs the methods
    /// of its name alone, as fresh objects like every other lookup's.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MethodDefinitionHandle> ReadExtensionMethods(MetadataModule module)
    {
        var metadata = module.MetadataFile.Metadata;
        return metadata.MethodDefinitions
            .Where(handle => metadata.GetMethodDefinition(handle) is var row
                && (row.Attributes & System.Reflection.MethodAttributes.Static) != 0
                && row.GetCustomAttributes().Count > 0
                && module.GetDefinition(handle) is { IsExtensionMethod: true, Parameters.Count: > 0 })
            .ToList();
    }

    // --- Field resolution ------------------------------------------------------

    /// <summary>
    /// Resolve a field by name, searching declared → inherited.
    /// </summary>
    public (IField Field, MemberOrigin Origin) ResolveField(
        ITypeDefinition type, string fieldName)
    {
        var found = TryFindField(type, fieldName)
            ?? TryFindField(type, fieldName, unreachable: true)
            ?? throw NotFound("Field", fieldName, type, searchesExtensions: false);
        RequireOwn(type, found.Field);
        return found;
    }

    /// <summary>Whether the type or one of its base classes declares a field of the name.</summary>
    public bool HasField(ITypeDefinition type, string fieldName) =>
        TryFindField(type, fieldName, unreachable: true) is not null;

    /// <summary>
    /// The field of that name at the most-derived level of the declared → base walk that has one
    /// the search sees (<see cref="Sees"/>), or with <paramref name="unreachable"/> any.
    /// </summary>
    private (IField Field, MemberOrigin Origin)? TryFindField(
        ITypeDefinition type, string fieldName, bool unreachable = false)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var field = level.Fields.FirstOrDefault(f => f.Name == fieldName && (unreachable || Sees(type, f)));
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
            ?? TryFindProperty(type, propertyName, includeExplicit: true, unreachable: true)
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
        var rendered = ReferenceFormatter.FormatList(candidates);
        return new ArgumentException(
            $"Property '{propertyName}' on {context} has {candidates.Count} overloads — " +
            "in C# this is always an indexer, but non-C# metadata or obfuscation can " +
            "produce non-indexer collisions too. To choose one, name its accessor instead, " +
            $"get_{propertyName} or set_{propertyName}, with {ParameterNames.ParameterTypes}. Candidates: {rendered}.");
    }

    /// <summary>
    /// The property of that name at the most-derived level of the declared → base walk that
    /// has one, then, with <paramref name="includeExplicit"/>, an explicit implementation of
    /// that name. Throws when that level has several. Only a property the search sees counts
    /// (<see cref="Sees"/>), or with <paramref name="unreachable"/> any.
    /// </summary>
    private (IProperty Property, MemberOrigin Origin)? TryFindProperty(
        ITypeDefinition type, string propertyName, bool includeExplicit, bool unreachable = false)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = level.Properties.Where(p => p.Name == propertyName && (unreachable || Sees(type, p))).ToList();
            if (here.Count > 0 && IsExternal(level))
                return (here[0], origin);
            if (here.Count > 1)
                throw AmbiguousPropertyError(here, ReferenceFormatter.FormatTypeName(level), propertyName);
            if (here.Count == 1)
                return (here[0], origin);
        }

        return includeExplicit
            ? FindExplicitImplementation(type, level => level.Properties, propertyName, unreachable)
            : null;
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
            ?? TryFindEvent(type, eventName, includeExplicit: true, unreachable: true)
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
    /// name. Throws when that level has several. Only an event the search sees counts
    /// (<see cref="Sees"/>), or with <paramref name="unreachable"/> any.
    /// </summary>
    private (IEvent Event, MemberOrigin Origin)? TryFindEvent(
        ITypeDefinition type, string eventName, bool includeExplicit, bool unreachable = false)
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = level.Events.Where(e => e.Name == eventName && (unreachable || Sees(type, e))).ToList();
            if (here.Count > 0 && IsExternal(level))
                return (here[0], origin);
            if (here.Count > 1)
                throw AmbiguousEventError(here, ReferenceFormatter.FormatTypeName(level), eventName);
            if (here.Count == 1)
                return (here[0], origin);
        }

        return includeExplicit
            ? FindExplicitImplementation(type, level => level.Events, eventName, unreachable)
            : null;
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
    /// no level has one. Only a member the search sees counts (<see cref="Sees"/>), or with
    /// <paramref name="unreachable"/> any.
    /// </summary>
    private (T Member, MemberOrigin Origin)? FindExplicitImplementation<T>(
        ITypeDefinition type, Func<ITypeDefinition, IEnumerable<T>> membersOf, string name, bool unreachable)
        where T : IMember
    {
        foreach (var (level, origin) in Levels(type))
        {
            var here = membersOf(level).Where(m => ImplementsExplicitly(m, name) && (unreachable || Sees(type, m))).ToList();
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
            ? $"{method.Name}{ReferenceFormatter.FormatTypeArguments(method)}({string.Join(", ", method.Parameters.Select(ReferenceFormatter.FormatParameterType))})"
            : member.Name).ToList();
        return new ArgumentException(
            $"'{name}' on {ReferenceFormatter.FormatTypeName(level)} names {listed.Count} explicit interface " +
            $"implementations: {string.Join(", ", listed)}. Pass the full name of one instead.");
    }
}
