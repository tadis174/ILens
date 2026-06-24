using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Pairwise comparison of two loaded assemblies — what types differ, which
/// members shifted, which method bodies changed. Backs <c>list_changed_types</c>,
/// <c>compare_type</c>, and <c>compare_method</c>.
///
/// Members are matched by a signature key that includes the name and the
/// rendered parameter and return types (see <see cref="MatchKey"/>) — any change
/// to that key surfaces as remove+add rather than as a separate "signature
/// changed" category. Method-body equality is decided by normalized IL
/// disassembly: ILSpy renders operand tokens as symbolic names, so a method
/// whose source is unchanged digests identically even when the metadata tokens
/// around it shift on rebuild.
/// </summary>
public sealed class AssemblyComparer
{
    public AssemblyHost A { get; }
    public AssemblyHost B { get; }

    public AssemblyComparer(AssemblyHost a, AssemblyHost b)
    {
        A = a;
        B = b;
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

    public sealed record MemberChange(
        SymbolCategory Category,
        string Signature,
        MemberChangeKind Kind);

    public sealed record TypeDiff(
        string TypeName,
        TypePresence Side,
        IReadOnlyList<MemberChange> Changes);

    /// <summary>
    /// Enumerate every type whose presence or contents differ between A and B.
    /// <paramref name="namespaceFilter"/> restricts the walk to a single namespace
    /// when non-empty; <paramref name="excludeCompilerGenerated"/> drops types
    /// marked with <c>[CompilerGenerated]</c>. Results are sorted by full type name.
    /// </summary>
    public IEnumerable<TypeChange> EnumerateChangedTypes(
        string namespaceFilter, bool excludeCompilerGenerated)
    {
        // Key by ReflectionName, not FullName: same-named generics with different
        // arities (e.g. KeyComparer`1 vs KeyComparer`2) share a FullName and would
        // collide in a Dictionary. ReflectionName encodes the arity suffix.
        var aTypes = SelectTypes(A, namespaceFilter, excludeCompilerGenerated)
            .ToDictionary(t => t.ReflectionName);
        var bTypes = SelectTypes(B, namespaceFilter, excludeCompilerGenerated)
            .ToDictionary(t => t.ReflectionName);

        foreach (var key in aTypes.Keys.Where(n => !bTypes.ContainsKey(n)).OrderBy(n => n))
            yield return new TypeChange(aTypes[key].FullName, TypePresence.Removed, ChangeKinds.None);

        foreach (var key in bTypes.Keys.Where(n => !aTypes.ContainsKey(n)).OrderBy(n => n))
            yield return new TypeChange(bTypes[key].FullName, TypePresence.Added, ChangeKinds.None);

        foreach (var key in aTypes.Keys.Where(bTypes.ContainsKey).OrderBy(n => n))
        {
            var kinds = ClassifyChangedType(aTypes[key], bTypes[key]);
            if (kinds != ChangeKinds.None)
                yield return new TypeChange(aTypes[key].FullName, TypePresence.Changed, kinds);
        }
    }

    /// <summary>
    /// Compare a single type by full name. Throws when the type is missing from
    /// both assemblies — the call would otherwise have no useful result.
    /// </summary>
    public TypeDiff CompareType(string typeName)
    {
        var aType = A.TypeSystem.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.FullName == typeName);
        var bType = B.TypeSystem.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.FullName == typeName);

        if (aType == null && bType == null)
            throw new ArgumentException(
                $"Type '{typeName}' not found in either assembly.");

        if (aType == null)
        {
            var added = EnumerateMembers(bType!)
                .Select(m => new MemberChange(m.Category, m.DisplaySignature, MemberChangeKind.Added))
                .ToList();
            return new TypeDiff(typeName, TypePresence.Added, added);
        }
        if (bType == null)
        {
            var removed = EnumerateMembers(aType)
                .Select(m => new MemberChange(m.Category, m.DisplaySignature, MemberChangeKind.Removed))
                .ToList();
            return new TypeDiff(typeName, TypePresence.Removed, removed);
        }

        var aMembers = EnumerateMembers(aType).ToDictionary(m => m.MatchKey);
        var bMembers = EnumerateMembers(bType).ToDictionary(m => m.MatchKey);

        var changes = new List<MemberChange>();
        foreach (var key in aMembers.Keys.Where(k => !bMembers.ContainsKey(k)).OrderBy(k => k))
            changes.Add(new MemberChange(
                aMembers[key].Category, aMembers[key].DisplaySignature, MemberChangeKind.Removed));
        foreach (var key in bMembers.Keys.Where(k => !aMembers.ContainsKey(k)).OrderBy(k => k))
            changes.Add(new MemberChange(
                bMembers[key].Category, bMembers[key].DisplaySignature, MemberChangeKind.Added));

        foreach (var key in aMembers.Keys.Where(bMembers.ContainsKey).OrderBy(k => k))
        {
            var aEntry = aMembers[key];
            var bEntry = bMembers[key];
            if (aEntry.Category != SymbolCategory.Method) continue;

            var aBody = A.DisassembleMethodBody((IMethod)aEntry.Symbol);
            var bBody = B.DisassembleMethodBody((IMethod)bEntry.Symbol);
            if (aBody != bBody)
                changes.Add(new MemberChange(
                    SymbolCategory.Method, aEntry.DisplaySignature, MemberChangeKind.BodyChanged));
        }

        return new TypeDiff(typeName, TypePresence.Changed, changes);
    }

    /// <summary>
    /// Find a method on the named type in each assembly, matched by the same
    /// disambiguation rules as <c>decompile_method</c>. Returns the pair so a
    /// caller can decide how to render the difference (IL or C# text).
    /// </summary>
    public (IMethod InA, IMethod InB) ResolveMethodPair(
        string typeName, string methodName, int? parameterCount, string[] parameterTypes)
    {
        var aType = A.Resolver.ResolveType(typeName);
        var bType = B.Resolver.ResolveType(typeName);
        var (aMethod, _) = A.Resolver.ResolveMethod(aType, methodName, parameterCount, parameterTypes);
        var (bMethod, _) = B.Resolver.ResolveMethod(bType, methodName, parameterCount, parameterTypes);
        return (aMethod, bMethod);
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

        foreach (var key in aMembers.Keys.Where(bMembers.ContainsKey))
        {
            var aEntry = aMembers[key];
            var bEntry = bMembers[key];
            if (aEntry.Category != SymbolCategory.Method) continue;
            var aBody = A.DisassembleMethodBody((IMethod)aEntry.Symbol);
            var bBody = B.DisassembleMethodBody((IMethod)bEntry.Symbol);
            if (aBody != bBody)
            {
                kinds |= ChangeKinds.Bodies;
                break;
            }
        }
        return kinds;
    }

    /// <summary>
    /// One-line snapshot of a type's class-level metadata for equality comparison —
    /// base type, direct interfaces, modifiers, accessibility. Type-level
    /// <c>[CLR]</c> attributes are intentionally excluded for now (a future flag
    /// could opt them in).
    /// </summary>
    private static string TypeMetadataSignature(ITypeDefinition type)
    {
        var baseType = type.DirectBaseTypes
            .Select(t => t.GetDefinition())
            .FirstOrDefault(t => t?.Kind == TypeKind.Class)?.FullName ?? "";
        var interfaces = string.Join(",",
            type.DirectBaseTypes
                .Where(t => t.GetDefinition()?.Kind == TypeKind.Interface)
                .Select(t => t.FullName)
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

    private static IEnumerable<MemberEntry> EnumerateMembers(ITypeDefinition type)
    {
        foreach (var m in type.Methods)
            yield return new MemberEntry(m, SymbolCategory.Method,
                BuildMatchKey(m), SignatureFormatter.FormatMember(m));
        foreach (var p in type.Properties)
            yield return new MemberEntry(p, SymbolCategory.Property,
                BuildMatchKey(p), SignatureFormatter.FormatMember(p));
        foreach (var f in type.Fields)
            yield return new MemberEntry(f, SymbolCategory.Field,
                BuildMatchKey(f), SignatureFormatter.FormatMember(f));
        foreach (var e in type.Events)
            yield return new MemberEntry(e, SymbolCategory.Event,
                BuildMatchKey(e), SignatureFormatter.FormatMember(e));
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
            ":" + ReferenceFormatter.FormatTypeRef(method.ReturnType),
        IProperty prop => prop.IsIndexer
            ? "P:" + prop.Name +
              "(" + string.Join(",", prop.Parameters.Select(BuildParamKey)) + ")" +
              ":" + ReferenceFormatter.FormatTypeRef(prop.ReturnType)
            : "P:" + prop.Name + ":" + ReferenceFormatter.FormatTypeRef(prop.ReturnType),
        IField field => "F:" + field.Name + ":" + ReferenceFormatter.FormatTypeRef(field.Type),
        IEvent evt => "E:" + evt.Name + ":" + ReferenceFormatter.FormatTypeRef(evt.ReturnType),
        _ => member.ToString() ?? "?",
    };

    /// <summary>
    /// Parameter-position key for <see cref="BuildMatchKey"/> — type plus the
    /// ref/out/in modifier, which IS part of the CLR signature and lets
    /// <c>Foo(int)</c>, <c>Foo(ref int)</c>, <c>Foo(out int)</c> coexist as
    /// distinct overloads. Drops the parameter name (irrelevant for matching).
    /// </summary>
    private static string BuildParamKey(IParameter p)
    {
        var prefix = p.ReferenceKind switch
        {
            ReferenceKind.Ref => "ref ",
            ReferenceKind.Out => "out ",
            ReferenceKind.In => "in ",
            _ => ""
        };
        return prefix + ReferenceFormatter.FormatTypeRef(p.Type);
    }
}
