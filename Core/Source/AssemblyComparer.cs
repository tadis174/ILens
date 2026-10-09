using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Pairwise comparison of two loaded assemblies — what types differ, which
/// members shifted, which method bodies changed. Backs the <c>compare</c> tool.
///
/// Members are matched by a signature key that includes the name and the
/// rendered parameter and return types (see <see cref="MatchKey"/>) — any change
/// to that key surfaces as remove+add rather than as a separate "signature
/// changed" category. A method is compared together with the code the compiler generated
/// for it (see <see cref="AssemblyHost.MethodUnit"/>), as IL disassembly: ILSpy renders
/// operand tokens as symbolic names, so a method whose source is unchanged digests
/// identically even when the metadata tokens around it shift on rebuild, and
/// <see cref="GeneratedNameNormalizer"/> neutralizes the numbers the compiler gives generated
/// names by position. So a change inside a lambda shows as a change of the method it is
/// written in, and a method added before another does not make the other show as changed.
/// </summary>
/// <remarks>
/// A generated method that no method's unit reaches is not compared at all. Measured on
/// 2026-10-04 on three builds of a game's main assembly, those were only the constructor and
/// static constructor of each class <c>&lt;&gt;c</c>, which hold nothing written in source, and
/// one Burst <c>Initialize$…$BurstDirectCall</c>. A lambda, local function or state-machine
/// method among them would falsify that: a change inside it would go unreported, and
/// <see cref="AssemblyHost.MethodUnit"/> would have to follow whatever reaches it.
/// </remarks>
public sealed class AssemblyComparer
{
    private const string NoBody = "(no body)\n";

    public AssemblyHost A { get; }
    public AssemblyHost B { get; }

    /// <summary>
    /// Whether compiler-generated types and members are compared on their own, under the names
    /// they have, besides counting as part of the methods they were generated for.
    /// </summary>
    private readonly bool _includeCompilerGenerated;

    public AssemblyComparer(AssemblyHost a, AssemblyHost b, bool includeCompilerGenerated)
    {
        A = a;
        B = b;
        _includeCompilerGenerated = includeCompilerGenerated;
    }

    /// <summary>How a type's presence differs between A and B.</summary>
    public enum TypePresence { Added, Removed, Changed }

    /// <summary>
    /// Categories of within-type change. Multiple flags can combine on a single
    /// Changed type — e.g. a refactor that both adds members and rewrites bodies.
    /// </summary>
    [Flags]
    public enum ChangeKinds
    {
        None = 0,
        Metadata = 1,
        Members = 2,
        Bodies = 4,
    }

    public sealed record TypeChange(string TypeName, TypePresence Presence, ChangeKinds Kinds);

    public enum MemberChangeKind { Added, Removed, BodyChanged }

    /// <summary>
    /// One member's change. <paramref name="Accessors"/> names the accessors whose bodies
    /// differ when a property's or event's body changed.
    /// </summary>
    public sealed record MemberChange(
        SymbolCategory Category,
        string Name,
        string Signature,
        MemberChangeKind Kind,
        IReadOnlyList<string> Accessors = null);

    public sealed record TypeDiff(
        string TypeName,
        TypePresence Side,
        IReadOnlyList<MemberChange> Changes);

    /// <summary>
    /// Enumerate every type whose presence or contents differ between A and B.
    /// <paramref name="namespaceFilter"/> restricts the walk to a single namespace
    /// when non-empty. Compiler-generated types are left out unless the comparer includes
    /// them. Results are sorted by full type name.
    /// </summary>
    public IEnumerable<TypeChange> EnumerateChangedTypes(string namespaceFilter)
    {
        // Key by ReflectionName, not FullName: types that differ only in generic arity
        // (e.g. KeyComparer and KeyComparer`2) share a FullName and would collide in a
        // Dictionary. ReflectionName encodes the arity suffix.
        var aTypes = SelectTypes(A, namespaceFilter, !_includeCompilerGenerated)
            .ToDictionary(t => t.ReflectionName);
        var bTypes = SelectTypes(B, namespaceFilter, !_includeCompilerGenerated)
            .ToDictionary(t => t.ReflectionName);

        foreach (var key in aTypes.Keys.Where(n => !bTypes.ContainsKey(n)).OrderBy(n => n))
            yield return new TypeChange(
                ReferenceFormatter.FormatTypeName(aTypes[key]), TypePresence.Removed, ChangeKinds.None);

        foreach (var key in bTypes.Keys.Where(n => !aTypes.ContainsKey(n)).OrderBy(n => n))
            yield return new TypeChange(
                ReferenceFormatter.FormatTypeName(bTypes[key]), TypePresence.Added, ChangeKinds.None);

        foreach (var key in aTypes.Keys.Where(bTypes.ContainsKey).OrderBy(n => n))
        {
            var kinds = ClassifyChangedType(aTypes[key], bTypes[key]);
            if (kinds != ChangeKinds.None)
                yield return new TypeChange(
                    ReferenceFormatter.FormatTypeName(aTypes[key]), TypePresence.Changed, kinds);
        }
    }

    /// <summary>
    /// Compare a single type by full name. Throws when the type is missing from
    /// both assemblies — the call would otherwise have no useful result — and names
    /// the assembly it comes from when either one references it. The diff carries the
    /// name of the type found, as ILens prints it, not the caller's spelling.
    /// </summary>
    public TypeDiff CompareType(string typeName)
    {
        var aType = A.Resolver.TryResolveType(typeName);
        var bType = B.Resolver.TryResolveType(typeName);
        if (aType == null && bType == null)
            throw NotInEither(typeName);

        var found = ReferenceFormatter.FormatTypeName(aType ?? bType);
        if (aType == null)
        {
            var added = EnumerateMembers(bType!)
                .Select(m => new MemberChange(m.Category, m.Symbol.Name, m.DisplaySignature, MemberChangeKind.Added))
                .ToList();
            return new TypeDiff(found, TypePresence.Added, added);
        }
        if (bType == null)
        {
            var removed = EnumerateMembers(aType)
                .Select(m => new MemberChange(m.Category, m.Symbol.Name, m.DisplaySignature, MemberChangeKind.Removed))
                .ToList();
            return new TypeDiff(found, TypePresence.Removed, removed);
        }

        var aMembers = EnumerateMembers(aType).ToDictionary(m => m.MatchKey);
        var bMembers = EnumerateMembers(bType).ToDictionary(m => m.MatchKey);

        var changes = new List<MemberChange>();
        foreach (var key in aMembers.Keys.Where(k => !bMembers.ContainsKey(k)).OrderBy(k => k))
            changes.Add(new MemberChange(
                aMembers[key].Category, aMembers[key].Symbol.Name, aMembers[key].DisplaySignature, MemberChangeKind.Removed));
        foreach (var key in bMembers.Keys.Where(k => !aMembers.ContainsKey(k)).OrderBy(k => k))
            changes.Add(new MemberChange(
                bMembers[key].Category, bMembers[key].Symbol.Name, bMembers[key].DisplaySignature, MemberChangeKind.Added));

        foreach (var key in aMembers.Keys.Where(bMembers.ContainsKey).OrderBy(k => k))
        {
            var aEntry = aMembers[key];
            var changed = ChangedBodies(aEntry.Symbol, bMembers[key].Symbol);
            if (changed.Count > 0)
                changes.Add(new MemberChange(aEntry.Category, aEntry.Symbol.Name, aEntry.DisplaySignature,
                    MemberChangeKind.BodyChanged,
                    aEntry.Category == SymbolCategory.Method ? [] : changed));
        }

        return new TypeDiff(found, TypePresence.Changed, changes);
    }

    /// <summary>
    /// Find a method on the named type in each assembly, matched by the same
    /// disambiguation rules as <c>decompile</c>. Returns the pair so a
    /// caller can decide how to render the difference (IL or C# text), and the
    /// type as found in each, for naming it the way ILens prints it. A side whose
    /// assembly lacks the type or such a method gets null, so a method only one
    /// assembly has comes back alone. Throws when neither assembly defines the type,
    /// as <see cref="CompareType"/> does.
    /// </summary>
    public (IMethod InA, IMethod InB, ITypeDefinition TypeInA, ITypeDefinition TypeInB) ResolveMethodPair(
        string typeName, string methodName, int? parameterCount, string[] parameterTypes)
    {
        var aType = A.Resolver.TryResolveType(typeName);
        var bType = B.Resolver.TryResolveType(typeName);
        if (aType == null && bType == null)
            throw NotInEither(typeName);
        var aMethod = aType == null ? null : A.Resolver.TryResolveMethod(aType, methodName, parameterCount, parameterTypes);
        var bMethod = bType == null ? null : B.Resolver.TryResolveMethod(bType, methodName, parameterCount, parameterTypes);
        return (aMethod, bMethod, aType, bType);
    }

    /// <summary>
    /// The error for a type neither assembly defines, naming the assembly it comes from when
    /// either one references it.
    /// </summary>
    private ArgumentException NotInEither(string typeName)
    {
        var assemblies = new[] { A, B }
            .SelectMany(host => host.Resolver.FindReferencedTypes(typeName).Matches)
            .SelectMany(type => type.Assemblies)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ArgumentException(SymbolResolver.OriginOf(assemblies) is { } origin
            ? $"Type '{typeName}' is not defined in either assembly; it comes from {origin}."
            : $"Type '{typeName}' not found in either assembly.");
    }

    private static IEnumerable<ITypeDefinition> SelectTypes(
        AssemblyHost host, string namespaceFilter, bool excludeCompilerGenerated)
    {
        IEnumerable<ITypeDefinition> query = host.TypeSystem.MainModule.TypeDefinitions;
        if (!string.IsNullOrEmpty(namespaceFilter))
            query = query.Where(t => t.Namespace == namespaceFilter);
        if (excludeCompilerGenerated)
            query = query.Where(t => !CompilerGeneratedFilter.IsCompilerGenerated(t));
        return query;
    }

    private ChangeKinds ClassifyChangedType(ITypeDefinition a, ITypeDefinition b)
    {
        var kinds = ChangeKinds.None;
        if (TypeMetadataSignature(a) != TypeMetadataSignature(b))
            kinds |= ChangeKinds.Metadata;

        var aMembers = EnumerateMembers(a).ToDictionary(m => m.MatchKey);
        var bMembers = EnumerateMembers(b).ToDictionary(m => m.MatchKey);

        if (aMembers.Keys.Except(bMembers.Keys).Any() ||
            bMembers.Keys.Except(aMembers.Keys).Any())
            kinds |= ChangeKinds.Members;

        if (aMembers.Keys.Where(bMembers.ContainsKey)
            .Any(key => ChangedBodies(aMembers[key].Symbol, bMembers[key].Symbol).Count > 0))
            kinds |= ChangeKinds.Bodies;
        return kinds;
    }

    /// <summary>
    /// The names of the bodies that differ between a member matched in A and in B: the method
    /// itself, or a property's or event's accessors, which ILSpy does not list among a type's
    /// methods. Accessors pair by role, so an accessor only one side has counts as changed and
    /// is named from the side that has it. A member written in source is compared with the code
    /// generated for it. A generated member, which only a comparer that includes generated
    /// members lists, is compared as it stands.
    /// </summary>
    private List<string> ChangedBodies(ISymbol a, ISymbol b)
    {
        var generated = IsGenerated(a);
        var changed = new List<string>();
        foreach (var (aBody, bBody) in BodiesOf(a).Zip(BodiesOf(b)))
        {
            if (aBody == null && bBody == null) continue;
            if (aBody == null || bBody == null
                || (generated
                    ? A.DisassembleMethodBody(aBody) != B.DisassembleMethodBody(bBody)
                    : !HaveEqualCode(aBody, bBody)))
                changed.Add((aBody ?? bBody).Name);
        }
        return changed;
    }

    /// <summary>
    /// Whether a method matched in A and in B has the same code in both: the same IL in its own
    /// body and in every method generated for it, once the numbers the compiler gives generated
    /// names by position are set aside.
    /// </summary>
    public bool HaveEqualCode(IMethod a, IMethod b)
    {
        var aText = UnitText(A, a);
        var bText = UnitText(B, b);
        return aText == bText
            || GeneratedNameNormalizer.Normalize(aText) == GeneratedNameNormalizer.Normalize(bText);
    }

    /// <summary>
    /// The IL of <paramref name="method"/> and of every method generated for it, as the
    /// comparison reads it: each generated method after a line naming it, and the numbers the
    /// compiler gives generated names by position replaced.
    /// </summary>
    public static string CodeOf(AssemblyHost host, IMethod method) =>
        GeneratedNameNormalizer.Normalize(UnitText(host, method));

    /// <summary>
    /// The copy of <paramref name="method"/>'s code that Burst keeps and compiles, or <c>null</c>
    /// when Burst does not compile the method.
    /// </summary>
    public static IMethod BurstManagedCopyOf(AssemblyHost host, IMethod method) =>
        host.MethodUnit(method).Skip(1).FirstOrDefault(part => part.Name == method.Name + "$BurstManaged");

    private static string UnitText(AssemblyHost host, IMethod method)
    {
        var parts = host.MethodUnit(method);
        var text = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                text.Append("\n// Generated: ").Append(ReferenceFormatter.FormatMethodReference(parts[i])).Append('\n');
            var body = host.DisassembleMethodBody(parts[i]) ?? NoBody;
            text.Append(body);
            if (!body.EndsWith('\n'))
                text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="member"/> is left out unless the comparer includes generated
    /// members: a compiler-generated one, or the copy of a method's code Burst keeps, which
    /// counts as part of that method.
    /// </summary>
    private static bool IsGenerated(ISymbol member) =>
        member is IMember m
        && (CompilerGeneratedFilter.IsCompilerGenerated(m) || CompilerGeneratedFilter.IsBurstManagedCopy(m));

    private static IMethod[] BodiesOf(ISymbol member) => member switch
    {
        IMethod method => [method],
        IProperty property => [property.Getter, property.Setter],
        IEvent @event => [@event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor],
        _ => [],
    };

    /// <summary>
    /// One-line snapshot of a type's class-level metadata for equality comparison —
    /// base type, direct interfaces, modifiers, accessibility. The base type and
    /// interfaces are recorded by <c>ReflectionName</c>, not <c>FullName</c>, which drops
    /// the generic arity and type arguments, so a base that moves from <c>Task</c> to
    /// <c>Task&lt;int&gt;</c>, or from <c>List&lt;int&gt;</c> to <c>List&lt;string&gt;</c>,
    /// counts as a change. Type-level
    /// <c>[CLR]</c> attributes are intentionally excluded for now (a future flag
    /// could opt them in).
    /// </summary>
    private static string TypeMetadataSignature(ITypeDefinition type)
    {
        var baseType = type.DirectBaseTypes
            .FirstOrDefault(t => t.GetDefinition()?.Kind == TypeKind.Class)?.ReflectionName ?? "";
        var interfaces = string.Join(",",
            type.DirectBaseTypes
                .Where(t => t.GetDefinition()?.Kind == TypeKind.Interface)
                .Select(t => t.ReflectionName)
                .OrderBy(s => s, StringComparer.Ordinal));
        return $"base={baseType};ifaces={interfaces};static={TypeWalker.IsStaticClass(type)};" +
            $"abstract={type.IsAbstract};sealed={type.IsSealed};access={type.Accessibility};" +
            $"kind={type.Kind}";
    }

    private sealed record MemberEntry(
        ISymbol Symbol,
        SymbolCategory Category,
        string MatchKey,
        string DisplaySignature);

    /// <summary>
    /// The members of <paramref name="type"/> the comparison lists, each with its match key:
    /// those written in source, and the generated ones as well when the comparer includes them.
    /// </summary>
    private IEnumerable<MemberEntry> EnumerateMembers(ITypeDefinition type)
    {
        var members = type.Methods.Select(m => new MemberEntry(m, SymbolCategory.Method,
                BuildMatchKey(m), SignatureFormatter.FormatMember(m)))
            .Concat(type.Properties.Select(p => new MemberEntry(p, SymbolCategory.Property,
                BuildMatchKey(p), SignatureFormatter.FormatMember(p))))
            .Concat(type.Fields.Select(f => new MemberEntry(f, SymbolCategory.Field,
                BuildMatchKey(f), SignatureFormatter.FormatMember(f))))
            .Concat(type.Events.Select(e => new MemberEntry(e, SymbolCategory.Event,
                BuildMatchKey(e), SignatureFormatter.FormatMember(e))));
        return _includeCompilerGenerated ? members : members.Where(entry => !IsGenerated(entry.Symbol));
    }

    /// <summary>
    /// Cross-assembly identity key for a member. Includes name, generic-method
    /// arity, parameter ref kinds and types, and return type — every overload-
    /// distinguishing axis the CLR allows — so overloads survive matching and so
    /// any change to them surfaces as remove+add rather than silently morphing
    /// one entry into another.
    /// </summary>
    private static string BuildMatchKey(ISymbol member) => member switch
    {
        IMethod method =>
            "M:" + method.Name +
            (method.TypeParameters.Count > 0 ? "`" + method.TypeParameters.Count : "") +
            "(" + string.Join(",", method.Parameters.Select(BuildParamKey)) + ")" +
            ":" + ReferenceFormatter.FormatReturnType(method),
        IProperty prop => prop.IsIndexer
            ? "P:" + prop.Name +
              "(" + string.Join(",", prop.Parameters.Select(BuildParamKey)) + ")" +
              ":" + ReferenceFormatter.FormatReturnType(prop)
            : "P:" + prop.Name + ":" + ReferenceFormatter.FormatReturnType(prop),
        IField field => "F:" + field.Name + ":" + ReferenceFormatter.FormatTypeRef(field.Type),
        IEvent evt => "E:" + evt.Name + ":" + ReferenceFormatter.FormatTypeRef(evt.ReturnType),
        _ => member.ToString() ?? "?",
    };

    /// <summary>
    /// Parameter-position key for <see cref="BuildMatchKey"/> — type plus the
    /// ref, out, in or ref readonly keyword, which IS part of the CLR signature and lets
    /// <c>Foo(int)</c>, <c>Foo(ref int)</c>, <c>Foo(out int)</c> coexist as
    /// distinct overloads. Drops the parameter name (irrelevant for matching).
    /// </summary>
    private static string BuildParamKey(IParameter p) => ReferenceFormatter.FormatParameterType(p);
}
