using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Pairwise comparison of two loaded assemblies — what types differ, which
/// members shifted, whose declarations changed, which constants took another value, which
/// method bodies changed. Backs the <c>compare</c> tool.
///
/// Members are matched by a signature key that includes the name and the full
/// parameter and return types (see <see cref="BuildMatchKey"/>) — any change
/// to that key surfaces as remove+add rather than as a separate "signature
/// changed" category. A matched member whose declaration reads otherwise, in its
/// accessibility, its modifiers, the names of its parameters or their defaults, is a changed
/// member (see <see cref="DeclarationsDiffer"/>). A constant has no body; its value is compared instead, since the
/// compiler copies it into every use (see <see cref="ValueDiffers"/>). A method is compared
/// together with the code the compiler generated for it (see
/// <see cref="AssemblyHost.MethodUnit"/>), as IL disassembly: ILSpy renders operand tokens as
/// symbolic names, so a method whose source is unchanged digests
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

    /// <summary>
    /// Whether bodies are compared: not where A or B is a reference assembly
    /// (<see cref="AssemblyHost.IsReferenceAssembly"/>), whose bodies hold no code, so that they
    /// read alike or not whatever the library does.
    /// </summary>
    public bool ComparesBodies => !A.IsReferenceAssembly && !B.IsReferenceAssembly;

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
        Values = 8,
        Declarations = 16,
    }

    /// <summary>
    /// One type that differs. For a type only one assembly defines, <paramref name="Elsewhere"/>
    /// says where the other assembly's metadata says it lies, null where it names no assembly.
    /// </summary>
    public sealed record TypeChange(string TypeName, TypePresence Presence, ChangeKinds Kinds,
        SymbolResolver.Elsewhere Elsewhere = null);

    public enum MemberChangeKind { Added, Removed, BodyChanged, ValueChanged, DeclarationChanged }

    /// <summary>
    /// One member's change. <paramref name="Signature"/> holds both declarations, A's and B's,
    /// for a member whose declaration changed. <paramref name="Bodies"/> names the bodies that
    /// differ for a member both assemblies have: a method's own, or a property's or event's
    /// accessors.
    /// </summary>
    public sealed record MemberChange(
        SymbolCategory Category,
        string Name,
        string Signature,
        MemberChangeKind Kind,
        IReadOnlyList<string> Bodies = null);

    /// <summary>
    /// One type's changes: its member changes and, for a type both assemblies define whose
    /// metadata changed, <paramref name="Declaration"/>, its two declarations, A's and B's, since
    /// no member shows such a change; null otherwise, and where the two read alike in full as
    /// well. For a type only one assembly defines,
    /// <paramref name="Elsewhere"/> says where the other assembly's metadata says it lies, null
    /// where it names no assembly.
    /// </summary>
    public sealed record TypeDiff(
        string TypeName,
        TypePresence Side,
        IReadOnlyList<MemberChange> Changes,
        SymbolResolver.Elsewhere Elsewhere = null,
        string Declaration = null);

    /// <summary>
    /// Enumerate every type whose presence or contents differ between A and B.
    /// <paramref name="namespaceFilter"/> restricts the walk to a single namespace
    /// when non-empty. Compiler-generated types are left out unless the comparer includes
    /// them. Results are sorted by full type name. A type only one assembly defines carries
    /// where the other says it lies, as when a type moved to a library.
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
                ReferenceFormatter.FormatTypeName(aTypes[key]), TypePresence.Removed, ChangeKinds.None,
                B.Resolver.WhereElse(aTypes[key].FullTypeName));

        foreach (var key in bTypes.Keys.Where(n => !aTypes.ContainsKey(n)).OrderBy(n => n))
            yield return new TypeChange(
                ReferenceFormatter.FormatTypeName(bTypes[key]), TypePresence.Added, ChangeKinds.None,
                A.Resolver.WhereElse(bTypes[key].FullTypeName));

        foreach (var key in aTypes.Keys.Where(bTypes.ContainsKey).OrderBy(n => n))
        {
            var kinds = ClassifyChangedType(aTypes[key], bTypes[key]);
            if (kinds != ChangeKinds.None)
                yield return new TypeChange(
                    ReferenceFormatter.FormatTypeName(aTypes[key]), TypePresence.Changed, kinds);
        }
    }

    /// <summary>
    /// Compare a single type by full name, the pair <see cref="ResolveTypePair"/> finds. Throws
    /// when the type is missing from both assemblies — the call would otherwise have no useful
    /// result — and names the assembly it comes from when either one references it. The diff carries the
    /// name of the type found, as ILens prints it, not the caller's spelling, for a type
    /// only one assembly defines, where the other says it lies, and for a type whose metadata
    /// changed, as the type list marks it, both its declarations.
    /// </summary>
    public TypeDiff CompareType(string typeName)
    {
        var (aType, bType) = ResolveTypePair(typeName);

        var found = ReferenceFormatter.FormatTypeName(aType ?? bType);
        if (aType == null)
        {
            var addedEntries = EnumerateMembers(bType!).ToList();
            var (addedLine, addedInFull) = (LineAmong(addedEntries), InFullAmong(addedEntries));
            var added = addedEntries
                .Select(m => new MemberChange(m.Category, m.Symbol.Name, Text(m, addedInFull(m), addedLine),
                    MemberChangeKind.Added))
                .ToList();
            return new TypeDiff(found, TypePresence.Added, added, A.Resolver.WhereElse(bType!.FullTypeName));
        }
        if (bType == null)
        {
            var removedEntries = EnumerateMembers(aType).ToList();
            var (removedLine, removedInFull) = (LineAmong(removedEntries), InFullAmong(removedEntries));
            var removed = removedEntries
                .Select(m => new MemberChange(m.Category, m.Symbol.Name, Text(m, removedInFull(m), removedLine),
                    MemberChangeKind.Removed))
                .ToList();
            return new TypeDiff(found, TypePresence.Removed, removed, B.Resolver.WhereElse(aType.FullTypeName));
        }

        var aMembers = MembersByKey(aType);
        var bMembers = MembersByKey(bType);
        // A line is told apart from every member of the type in both builds, listed or not.
        var line = LineAmong(aMembers.Values.Concat(bMembers.Values));

        var onlyInA = aMembers.Keys.Where(k => !bMembers.ContainsKey(k)).OrderBy(k => k).Select(k => aMembers[k]).ToList();
        var onlyInB = bMembers.Keys.Where(k => !aMembers.ContainsKey(k)).OrderBy(k => k).Select(k => bMembers[k]).ToList();
        var matched = aMembers.Keys.Where(bMembers.ContainsKey).OrderBy(k => k)
            .Select(k => (A: aMembers[k], B: bMembers[k], Bodies: ChangedBodies(aMembers[k].Symbol, bMembers[k].Symbol)))
            .Where(pair => DeclarationsDiffer(pair.A, pair.B) || ValueDiffers(pair.A.Symbol, pair.B.Symbol)
                || pair.Bodies.Count > 0)
            .ToList();
        var inFull = InFullAmong(onlyInA.Concat(onlyInB).Concat(matched.SelectMany(pair => new[] { pair.A, pair.B })));

        var changes = new List<MemberChange>();
        foreach (var entry in onlyInA)
            changes.Add(new MemberChange(entry.Category, entry.Symbol.Name, Text(entry, inFull(entry), line),
                MemberChangeKind.Removed));
        foreach (var entry in onlyInB)
            changes.Add(new MemberChange(entry.Category, entry.Symbol.Name, Text(entry, inFull(entry), line),
                MemberChangeKind.Added));

        foreach (var (aEntry, bEntry, changed) in matched)
        {
            var full = inFull(aEntry) || inFull(bEntry);

            // A declaration that changed is written in full on both sides, a constant's with its
            // value, so the line also shows a value that changed with it.
            if (DeclarationsDiffer(aEntry, bEntry))
                changes.Add(new MemberChange(aEntry.Category, aEntry.Symbol.Name,
                    $"{Text(aEntry, full, line)} → {Text(bEntry, full, line)}", MemberChangeKind.DeclarationChanged, changed));
            else if (ValueDiffers(aEntry.Symbol, bEntry.Symbol))
                changes.Add(new MemberChange(aEntry.Category, aEntry.Symbol.Name,
                    SignatureFormatter.FormatConstantChange((IField)aEntry.Symbol, (IField)bEntry.Symbol, full),
                    MemberChangeKind.ValueChanged));
            else
                changes.Add(new MemberChange(aEntry.Category, aEntry.Symbol.Name, Text(aEntry, full, line),
                    MemberChangeKind.BodyChanged, changed));
        }

        var declaration = TypeMetadataSignature(aType) != TypeMetadataSignature(bType)
            ? DeclarationChange(aType, bType)
            : null;
        return new TypeDiff(found, TypePresence.Changed, changes, Declaration: declaration);
    }

    /// <summary>
    /// A type's two declarations, <c>A → B</c>, as list_members writes the heading, or written in
    /// full where they read alike that way or where a name in them stands for two different
    /// types (<see cref="ReferenceFormatter.Sides{T}"/>), as when a base class gave way to a class
    /// of the same name in another namespace, alone or beside another change. Null where they read
    /// alike in full as well, which leaves nothing to show.
    /// </summary>
    private static string DeclarationChange(ITypeDefinition a, ITypeDefinition b)
    {
        var (aLine, bLine) = ReferenceFormatter.Sides(a, b,
            type => SignatureFormatter.FormatTypeDeclaration(type), type => SignatureFormatter.FormatTypeDeclaration(type, full: true));
        return aLine == bLine ? null : $"{aLine} → {bLine}";
    }

    /// <summary>
    /// The line each of <paramref name="entries"/> is listed with: its line as list_members writes
    /// it, or written in full where another entry's reads alike and is another member, as a
    /// parameter type that gave way to a class of the same name in another namespace makes it.
    /// </summary>
    private static Func<MemberEntry, string> LineAmong(IEnumerable<MemberEntry> entries) =>
        ReferenceFormatter.Distinguishing(entries, entry => entry.DisplaySignature,
            entry => SignatureFormatter.FormatMember((IMember)entry.Symbol, full: true));

    /// <summary>
    /// Which of <paramref name="listed"/>, the members a type's list of changes names, are written in
    /// full: those whose line names a type that a short name stands for beside another type among
    /// the lines of their member's name. Those lines are read together, as a reader pairs a member
    /// removed with one added, so a class that gave way to one of the same name in another namespace
    /// beside another change shows, which the test of whole lines in <see cref="LineAmong"/> does
    /// not see. Members of the name the list does not name do not count, and a line is read without
    /// the member's own name (<see cref="ReferenceFormatter.WithoutName"/>).
    /// </summary>
    private static Func<MemberEntry, bool> InFullAmong(IEnumerable<MemberEntry> listed)
    {
        var namesakes = listed
            .GroupBy(entry => entry.Symbol.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => ReferenceFormatter.NamesakesAmong(group.Select(Tested)), StringComparer.Ordinal);
        return entry => ReferenceFormatter.NamesOneOf(Tested(entry), namesakes[entry.Symbol.Name]);

        static string Tested(MemberEntry entry) => ReferenceFormatter.WithoutName(entry.Declaration, entry.Symbol.Name);
    }

    /// <summary>
    /// A listed member's line: as <paramref name="line"/> writes it, or in full.
    /// </summary>
    private static string Text(MemberEntry entry, bool full, Func<MemberEntry, string> line) =>
        full ? SignatureFormatter.FormatMember((IMember)entry.Symbol, full: true) : line(entry);

    /// <summary>
    /// What a method name finds in one of the two assemblies: the type asked about, null where the
    /// assembly lacks it; the method, null where the type has none of that name and hint; where
    /// the method was found relative to the type; the methods its line is told apart from,
    /// where no type of the assembly gives them; and the line that says what the lookup could not
    /// read, where it has one of its own. The last two come with an extension method found through
    /// a type of another assembly (<see cref="SymbolResolver.ExtensionThrough"/>), whose type is
    /// that type as its library reads it, null where the library was not found.
    /// </summary>
    public sealed record MethodSide(ITypeDefinition Type, IMethod Method, SymbolResolver.MemberOrigin Origin,
        IReadOnlyList<IMethod> Namesakes = null, string NotChecked = null)
    {
        /// <summary>The side an extension method found through a type of another assembly gives.</summary>
        public static MethodSide Through(SymbolResolver.ExtensionThrough found) =>
            found is null
                ? new(null, null, default)
                : new(found.Type, found.Method, found.Origin, found.Namesakes, found.NotChecked);
    }

    /// <summary>
    /// What a method name finds through a type neither assembly defines: the extension method each
    /// assembly finds, looking for a method as through a type the assemblies define
    /// (<see cref="SymbolResolver.FindExtensionThrough"/>), null in one that finds none; the type
    /// as an answer names it; whether the assembly that finds none declares an extension method
    /// the name would find there but for a method of the type of that name, which comes first
    /// (<see cref="SymbolResolver.FindsExtensionBehindMember"/>); and whether it
    /// declares the method the other finds, whose receiver is a type parameter that the type, as
    /// that assembly reads it, does not fit
    /// (<see cref="SymbolResolver.DoesNotFit(SymbolResolver.ExternalTypeName, IMethod)"/>).
    /// </summary>
    public sealed record ExtensionPair(SymbolResolver.ExtensionThrough A, SymbolResolver.ExtensionThrough B,
        string TypeDisplay, bool BehindMember, bool DoesNotFit);

    /// <summary>
    /// The <see cref="ExtensionPair"/> <paramref name="methodName"/> finds through
    /// <paramref name="typeName"/>; null where either assembly defines the type or neither finds
    /// an extension method, which leaves the question to <see cref="ResolveTypePair"/>. Throws
    /// where <see cref="TryResolveTypePair"/> does, for a name that fits no type of either assembly
    /// exactly and finds a different one in each.
    /// </summary>
    public ExtensionPair ResolveExtensionPair(string typeName, string methodName, SymbolResolver.MethodHint hint)
    {
        if (TryResolveTypePair(typeName) is not (null, null))
            return null;
        var (externalA, externalB) = (A.Resolver.TryResolveExternal(typeName), B.Resolver.TryResolveExternal(typeName));
        var (a, b) = (Find(A, externalA), Find(B, externalB));
        if (a is null && b is null)
            return null;

        var (missing, external) = a is null ? (A, externalA) : (B, externalB);
        var oneSided = (a is null || b is null) && external is not null;
        var behindMember = oneSided && missing.Resolver.FindsExtensionBehindMember(external, methodName, hint);
        var doesNotFit = oneSided && !behindMember
            && SameMethodIn(missing, (a ?? b).Method) is { } same && missing.Resolver.DoesNotFit(external, same);
        return new ExtensionPair(a, b, (externalA ?? externalB).Display, behindMember, doesNotFit);

        SymbolResolver.ExtensionThrough Find(AssemblyHost host, SymbolResolver.ExternalTypeName type) =>
            type is null ? null : host.Resolver.FindExtensionThrough(type, methodName, hint, methodsOnly: true);
    }

    /// <summary>
    /// What a method name finds through an array type in the two assemblies
    /// (<see cref="SymbolResolver.FindExtensionThroughArray"/>): the extension methods as an
    /// <see cref="ExtensionPair"/>, or, where neither finds one, the class that declares the member
    /// of System.Array or System.Object that comes first, which the question is then about.
    /// </summary>
    public sealed record ArrayPair(ExtensionPair Extensions, string MemberOwner);

    /// <summary>
    /// The <see cref="ArrayPair"/> <paramref name="methodName"/> finds through
    /// <paramref name="typeName"/>, or null where the name names no array type
    /// (<see cref="SymbolResolver.NamesArray"/>). A build that lacks a type the array names finds
    /// nothing through it. Throws where neither build finds an extension method or a member: the
    /// error of the first build that reads the array.
    /// </summary>
    public ArrayPair ResolveArrayExtensionPair(string typeName, string methodName, SymbolResolver.MethodHint hint)
    {
        if (!A.Resolver.NamesArray(typeName) && !B.Resolver.NamesArray(typeName))
            return null;
        var (arrayA, arrayB) = (A.Resolver.ReadArray(typeName, missingIsNull: true), B.Resolver.ReadArray(typeName, missingIsNull: true));
        // Neither build has every type the array names: the first build's error says which it lacks.
        if (arrayA is null && arrayB is null)
            _ = A.Resolver.ReadArray(typeName) ?? B.Resolver.ReadArray(typeName);
        var (a, b) = (Find(A, arrayA), Find(B, arrayB));
        var (extensionA, extensionB) = (a?.Extension, b?.Extension);
        if (extensionA is null && extensionB is null)
        {
            if ((a?.MemberOwner ?? b?.MemberOwner) is { } owner)
                return new ArrayPair(null, owner);
            var (host, array) = arrayA is not null ? (A, arrayA) : (B, arrayB);
            throw host.Resolver.ArrayMemberNotFound(array, methodName, hint);
        }

        var (missing, missingArray) = extensionA is null ? (A, arrayA) : (B, arrayB);
        var oneSided = (extensionA is null || extensionB is null) && missingArray is not null;
        var behindMember = oneSided && missing.Resolver.FindsExtensionBehindArrayMember(missingArray, methodName, hint);
        var doesNotFit = oneSided && !behindMember
            && SameMethodIn(missing, (extensionA ?? extensionB).Method) is { } same
            && missing.Resolver.DoesNotFit(missingArray, same);
        return new ArrayPair(
            new ExtensionPair(extensionA, extensionB, (arrayA ?? arrayB).Display, behindMember, doesNotFit), null);

        SymbolResolver.ArrayLookup Find(AssemblyHost host, SymbolResolver.NamedArray array) =>
            array is null ? null : host.Resolver.FindExtensionThroughArray(array, methodName, hint, methodsOnly: true);
    }

    /// <summary>
    /// True where <paramref name="host"/>'s assembly declares <paramref name="found"/>, an extension
    /// method the other assembly finds through a type, as the same member
    /// (<see cref="AreOneMember"/>), and its receiver is a type parameter that
    /// <paramref name="type"/>, the type as <paramref name="host"/>'s assembly has it, does not fit
    /// by C#'s rule: the reason that assembly finds no method of the name through the type.
    /// </summary>
    public static bool DoesNotFitIn(AssemblyHost host, ITypeDefinition type, IMethod found) =>
        type is not null && SameMethodIn(host, found) is { } same && host.Resolver.DoesNotFit(type, same);

    /// <summary>
    /// The member of <paramref name="host"/>'s assembly that is one member with
    /// <paramref name="method"/>, a method of the other assembly, as <see cref="CompareType"/>
    /// pairs members; null where it declares none.
    /// </summary>
    /// <remarks>
    /// Only an extension method whose receiver names a type parameter, itself or as an array's
    /// element, is looked for, the one kind a type can stop fitting, and its class by its
    /// metadata name, which a printed name can share with a nested class.
    /// </remarks>
    private static IMethod SameMethodIn(AssemblyHost host, IMethod method) =>
        method.IsExtensionMethod && TypeWalker.NamesTypeParameter(TypeWalker.ExtendedType(method))
        && method.DeclaringTypeDefinition is { } declaring
        && host.TypeSystem.MainModule.GetTypeDefinition(declaring.FullTypeName) is { } counterpart
            ? counterpart.DeclaredMethods().FirstOrDefault(candidate => AreOneMember(method, candidate))
            : null;

    /// <summary>
    /// The type a name finds in each assembly, paired as <see cref="TryResolveTypePair"/> pairs
    /// them, null in one that lacks it. Throws when neither assembly defines it, as
    /// <see cref="CompareType"/> does.
    /// </summary>
    public (ITypeDefinition A, ITypeDefinition B) ResolveTypePair(string typeName)
    {
        var (aType, bType) = TryResolveTypePair(typeName);
        if (aType == null && bType == null)
            throw NotInEither(typeName);
        return (aType, bType);
    }

    /// <summary>
    /// The type a name finds in each assembly, paired as the list of changed types pairs types, by
    /// reflection name; null in an assembly that lacks it, both where neither defines a type of
    /// the name.
    /// </summary>
    /// <remarks>
    /// Where the name fits a type of either assembly exactly as written
    /// (<see cref="SymbolResolver.DefinitionNamedExactly"/>), as every name the list prints does,
    /// that type is meant. Each assembly's own lookup would read a name without a count as the
    /// only type of that name, so the name of a type one build alone has, such as a class that
    /// gains a type parameter, or a class nested in one that the other build makes generic beside
    /// a class of the old name without type parameters, would find the generic type of the other
    /// build, and the two would be compared as one. Only where the name fits neither exactly does each assembly's lookup decide, and
    /// types of two reflection names are then an error naming both, since the name means neither
    /// more than the other. Whichever way the type is found, each assembly's side is its type of
    /// that reflection name, so an assembly that defines it is not passed over where its own
    /// lookup reads the name as a type it only references, as a class without type parameters
    /// that another library declares beside the generic class the assembly defines.
    /// </remarks>
    private (ITypeDefinition A, ITypeDefinition B) TryResolveTypePair(string typeName)
    {
        var found = A.Resolver.DefinitionNamedExactly(typeName) ?? B.Resolver.DefinitionNamedExactly(typeName);
        if (found == null)
        {
            var aType = A.Resolver.TryResolveType(typeName);
            var bType = B.Resolver.TryResolveType(typeName);
            if (aType != null && bType != null && aType.ReflectionName != bType.ReflectionName)
                throw new ArgumentException($"Type name '{typeName}' matches {ReferenceFormatter.FormatTypeName(aType)} " +
                    $"in A and {ReferenceFormatter.FormatTypeName(bType)} in B. Name one as listed.");
            found = aType ?? bType;
        }
        return found == null ? (null, null) : (Named(A, found), Named(B, found));

        static ITypeDefinition Named(AssemblyHost host, ITypeDefinition type) =>
            host.TypeSystem.MainModule.GetTypeDefinition(type.FullTypeName);
    }

    /// <summary>
    /// Find a method on the type in each assembly, matched by the same
    /// disambiguation rules as <c>decompile</c>. Returns the pair so a
    /// caller can decide how to render the difference (IL or C# text), with the
    /// type as found in each and where the method was found on it, for naming it as
    /// <c>decompile</c> does. A side whose assembly lacks the type or such a method has no
    /// method, so a method only one assembly has comes back alone. Each assembly is asked on
    /// its own, so the two methods can be different members, see <see cref="AreOneMember"/>.
    /// </summary>
    /// <remarks>
    /// An assembly that lacks the type but references the type the other defines, from an
    /// assembly its references name, or forwards it to another assembly, is asked through that
    /// type as decompile asks it (<see cref="SymbolResolver.FindExtensionThrough"/>), so a method
    /// one build declares on the type and the other, which takes the type from a library, as an
    /// extension method for it is compared as two methods. It looks for a method there as through a
    /// type it defines, so a property, field or event of the type does not come first; where a
    /// method of the type of the name does, that side finds nothing, and the answer is that only
    /// the other has a method of the name. The type is named
    /// there by its reflection name, which carries a generic type's count of type parameters, and
    /// what that name finds must be the type itself, since a name without a count can find a
    /// generic type of that name.
    /// </remarks>
    public (MethodSide A, MethodSide B) ResolveMethodPair(ITypeDefinition aType, ITypeDefinition bType,
        string methodName, SymbolResolver.MethodHint hint)
    {
        return (Side(A, aType, bType), Side(B, bType, aType));

        MethodSide Side(AssemblyHost host, ITypeDefinition type, ITypeDefinition other)
        {
            if (type is null)
                return MethodSide.Through(other is not null
                    && host.Resolver.TryResolveExternal(other.ReflectionName) is { } external
                    && (external.Origin ?? external.Forward) is not null
                    && external.Matches(other.FullTypeName)
                        ? host.Resolver.FindExtensionThrough(external, methodName, hint, methodsOnly: true)
                        : null);
            return host.Resolver.TryResolveMethod(type, methodName, hint) is { } found
                ? new MethodSide(type, found.Method, found.Origin)
                : new MethodSide(type, null, default);
        }
    }

    /// <summary>
    /// What a member name finds as a constant in one of the two assemblies: the type asked about,
    /// null where the assembly lacks it; the constant, null where the type has no member of that
    /// name; and where the constant was found relative to the type.
    /// </summary>
    public sealed record ConstantSide(ITypeDefinition Type, IField Constant, SymbolResolver.MemberOrigin Origin);

    /// <summary>
    /// Find the member a name without a hint finds on each of the two types, as <c>decompile</c>
    /// looks one up, when, in each assembly that has a member of that name, it is a constant that
    /// assembly declares; or null when one of them finds some other member, more than one, or one
    /// of a class another assembly defines, or neither finds one. As in C#, a constant hides a
    /// base class's method of its name. A constant has no body, so the caller compares its values.
    /// </summary>
    public (ConstantSide A, ConstantSide B)? ResolveConstantPair(
        ITypeDefinition aType, ITypeDefinition bType, string memberName)
    {
        // Only a name that a class of either walk declares as a field can find a constant. Any other
        // name is left to the caller's lookup of a method, with that lookup's answers and errors.
        if (!(aType is not null && A.Resolver.HasField(aType, memberName))
            && !(bType is not null && B.Resolver.HasField(bType, memberName)))
            return null;

        IReadOnlyList<(ISymbol Symbol, SymbolResolver.MemberOrigin Origin, SymbolCategory Category)> a =
            aType is null ? [] : A.Resolver.FindNearestMembers(aType, memberName);
        IReadOnlyList<(ISymbol Symbol, SymbolResolver.MemberOrigin Origin, SymbolCategory Category)> b =
            bType is null ? [] : B.Resolver.FindNearestMembers(bType, memberName);
        if ((a.Count == 0 && b.Count == 0) || !IsOwnConstant(A, a) || !IsOwnConstant(B, b))
            return null;
        return (Side(aType, a), Side(bType, b));

        static bool IsOwnConstant(AssemblyHost host,
            IReadOnlyList<(ISymbol Symbol, SymbolResolver.MemberOrigin Origin, SymbolCategory Category)> found) =>
            found.Count == 0
            || (found.Count == 1 && found[0].Symbol is IField { IsConst: true } field && !host.Resolver.IsExternal(field));

        static ConstantSide Side(ITypeDefinition type,
            IReadOnlyList<(ISymbol Symbol, SymbolResolver.MemberOrigin Origin, SymbolCategory Category)> found) =>
            found.Count == 0 ? new(type, null, default) : new(type, (IField)found[0].Symbol, found[0].Origin);
    }

    /// <summary>
    /// What a member name finds in one of the two assemblies where the two find members of
    /// different kinds: the type asked about, the member, where it was found relative to the
    /// type, and for a method the namesakes and the line of what was not read that its
    /// <see cref="MethodSide"/> carries.
    /// </summary>
    public sealed record MemberSide(ITypeDefinition Type, IMember Member, SymbolResolver.MemberOrigin Origin,
        IReadOnlyList<IMethod> Namesakes = null, string NotChecked = null);

    /// <summary>
    /// The members a name without a hint finds on the two types when they are of different kinds,
    /// such as a field in A and a property in B; null where they are of one kind, or where either
    /// assembly lacks the type and finds no extension method through it, finds no member or
    /// several, or finds one of a class another assembly defines, which the caller's lookup
    /// answers with its own errors. Each side's member is the method
    /// <see cref="ResolveMethodPair"/> found, and otherwise the member the name finds
    /// as decompile looks one up, so a property that hides a base class's method of its name is
    /// passed over where the base class's method is found, as elsewhere in the member view. A
    /// field and a constant that are one member, whose declaration changed, count as one kind.
    /// </summary>
    public (MemberSide A, MemberSide B)? ResolveKindPair(MethodSide a, MethodSide b, string memberName)
    {
        if (Side(A, a) is not { } aSide || Side(B, b) is not { } bSide
            || KindOf(aSide.Member) == KindOf(bSide.Member)
            || (aSide.Member is IField && bSide.Member is IField && AreOneMember(aSide.Member, bSide.Member)))
            return null;
        return (aSide, bSide);

        MemberSide Side(AssemblyHost host, MethodSide side)
        {
            if (side.Method is not null)
                return new MemberSide(side.Type, side.Method, side.Origin, side.Namesakes, side.NotChecked);
            if (side.Type is null
                || host.Resolver.FindNearestMembers(side.Type, memberName, explainAbsence: false)
                    is not [(IMember member, var origin, _)]
                || host.Resolver.IsExternal(member))
                return null;
            return new MemberSide(side.Type, member, origin);
        }
    }

    /// <summary>
    /// The kind of member the member view answers each in its own way: a method, a property, an
    /// event, a field, or a constant, which has a value where a field has the constructors that
    /// set it.
    /// </summary>
    public static string KindOf(IMember member) => member switch
    {
        IMethod => "method",
        IProperty => "property",
        IEvent => "event",
        IField { IsConst: true } => "constant",
        _ => "field",
    };

    /// <summary>
    /// Whether a member of A and one of B are one member as <see cref="CompareType"/> pairs members:
    /// declared by the same type, with the same <see cref="BuildMatchKey"/>. A name can find two
    /// that are not, such as a method whose return type changed, a constant whose type changed, or
    /// a member that moved to a base class or gave way to an extension method or an explicit
    /// implementation.
    /// </summary>
    public static bool AreOneMember(IMember a, IMember b) =>
        a.DeclaringTypeDefinition?.ReflectionName == b.DeclaringTypeDefinition?.ReflectionName
        && BuildMatchKey(a) == BuildMatchKey(b);

    /// <summary>
    /// Whether a member's declaration reads otherwise in B than in A, as list_members writes it:
    /// in its accessibility, its modifiers, the names of its parameters or type parameters,
    /// params, a parameter's default (<see cref="DefaultsDiffer"/>), the conditions on its type
    /// parameters, or a property's accessors. A constant's value is left out, since a value that
    /// changed alone has a line of its own. The lines are compared in full: the conditions are no
    /// part of the match key, so a condition whose type gave way to a class of the same name in
    /// another namespace changes the full line alone.
    /// </summary>
    public static bool DeclarationsDiffer(IMember a, IMember b) =>
        DeclarationsDiffer(SignatureFormatter.FormatDeclaration(a), SignatureFormatter.FormatDeclaration(b), a, b);

    private static bool DeclarationsDiffer(MemberEntry a, MemberEntry b) =>
        DeclarationsDiffer(a.Declaration, b.Declaration, (IMember)a.Symbol, (IMember)b.Symbol);

    private static bool DeclarationsDiffer(string aDeclaration, string bDeclaration, IMember a, IMember b) =>
        aDeclaration != bDeclaration || DefaultsDiffer(a, b);

    /// <summary>
    /// Whether a parameter of <paramref name="a"/> and the parameter at its place in
    /// <paramref name="b"/> take different defaults: a default in one of them alone, or values
    /// that are not the same (<see cref="SameValue"/>) and that read otherwise as the declarations
    /// write them, as two constants' values differ (<see cref="ValuesDiffer"/>). So a value of an
    /// enum that one build names in a library ILens did not find, written there as its number with
    /// the enum's name, is the same as the member's name the other build writes for that number.
    /// </summary>
    private static bool DefaultsDiffer(IMember a, IMember b) =>
        a is IParameterizedMember aMember && b is IParameterizedMember bMember
        && aMember.Parameters.Zip(bMember.Parameters).Any(pair => DefaultDiffers(pair.First, pair.Second));

    private static bool DefaultDiffers(IParameter a, IParameter b)
    {
        var takes = SignatureFormatter.TakesDefault(a);
        return takes != SignatureFormatter.TakesDefault(b)
            || (takes && !SameValue(a.GetConstantValue(), b.GetConstantValue())
                && ConstantFormatter.DefaultOf(a, full: true) != ConstantFormatter.DefaultOf(b, full: true));
    }

    /// <summary>
    /// A member's two declarations where they differ, <c>A → B</c>, as list_members writes them,
    /// or written in full where they read alike that way (<see cref="DeclarationsDiffer"/>) or where
    /// a name in them stands for two different types (<see cref="ReferenceFormatter.Sides{T}"/>), as
    /// when a condition's class gave way to a class of the same name in another namespace beside a
    /// change of the member's accessibility.
    /// </summary>
    public static string DeclarationChange(IMember a, IMember b)
    {
        var (aLine, bLine) = ReferenceFormatter.Sides(a, b,
            member => SignatureFormatter.FormatMember(member), member => SignatureFormatter.FormatMember(member, full: true),
            TypesNamed);
        return $"{aLine} → {bLine}";
    }

    /// <summary>
    /// The text <see cref="ReferenceFormatter.Sides{T}"/> reads a member's declaration by for its
    /// test: the declaration in full, without a constant's value or the parameters' defaults, which
    /// name the parameter's own enum or, for a special value such as <c>Math.PI</c>, a class of the
    /// core library, and without the member's own name, which a line writes as it is in either form
    /// (<see cref="ReferenceFormatter.WithoutName"/>).
    /// </summary>
    public static string TypesNamed(IMember member) =>
        ReferenceFormatter.WithoutName(SignatureFormatter.FormatDeclaration(member), member.Name);

    /// <summary>
    /// Whether two members matched in A and B are constants with different values. A field
    /// that is a constant in one of them alone is a change of its declaration.
    /// </summary>
    private static bool ValueDiffers(ISymbol a, ISymbol b) =>
        a is IField { IsConst: true } aField && b is IField { IsConst: true } bField && ValuesDiffer(aField, bField);

    /// <summary>
    /// Whether two constants hold different values: values that are not the same
    /// (<see cref="SameValue"/>) and that read otherwise as their declarations write them
    /// (<see cref="ConstantFormatter.ValueOf"/>). A constant of an enum type that names the same
    /// member in both, after the enum's members were numbered anew, reads alike and counts as
    /// unchanged, as a parameter's default does in a declaration; the new numbers show at the
    /// enum's own members.
    /// </summary>
    public static bool ValuesDiffer(IField a, IField b) =>
        !SameValue(a.GetConstantValue(), b.GetConstantValue())
        && ConstantFormatter.ValueOf(a, full: true) != ConstantFormatter.ValueOf(b, full: true);

    /// <summary>
    /// Whether two constant values are the same: equal, or the same number in two integral types,
    /// as an enum's members are after its underlying type changed. That change shows in the enum's
    /// metadata, which names the underlying type.
    /// Floating-point values compare exactly, by <c>Equals</c>: through <c>decimal</c>, as
    /// <c>CallSiteFinder.SameConstant</c> compares, two doubles that differ past their 15th
    /// significant digit would read alike.
    /// </summary>
    public static bool SameValue(object a, object b) =>
        Equals(a, b) || (IsIntegral(a) && IsIntegral(b) && Convert.ToDecimal(a) == Convert.ToDecimal(b));

    private static bool IsIntegral(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;

    /// <summary>
    /// The error for a type neither assembly defines. It names the assembly the type comes from
    /// where either one references it, and the assembly each one that forwards the type forwards
    /// it to: <c>…; in B it comes from Lib, Version=1.0.0.0; A forwards it to
    /// Core, Version=3.0.0.0.</c> The builds a reference is in are named only beside a forward,
    /// since without one what the references say holds for both. Where neither build has the
    /// name at all, it names the types the caller may have meant
    /// (<see cref="SymbolResolver.Meant(string, string)"/>), once where both builds have the
    /// same, as each build has them, and otherwise for each build that has any:
    /// <c>In B, types named Banner: Shop.Banner.</c>
    /// </summary>
    private ArgumentException NotInEither(string typeName)
    {
        if (A.Resolver.NamesArray(typeName) || B.Resolver.NamesArray(typeName))
            return SymbolResolver.ArrayNotTaken(typeName);
        var sides = new[] { (Name: "A", Host: A), (Name: "B", Host: B) }
            .Select(side => (side.Name, Where: side.Host.Resolver.WhereNamed(typeName)))
            .ToList();
        var clauses = new List<string>();
        var comesFrom = sides.SelectMany(side => side.Where.ComesFrom).Distinct(StringComparer.Ordinal).ToList();
        if (SymbolResolver.OriginOf(comesFrom) is { } origin)
            clauses.Add(sides.All(side => side.Where.ForwardsTo.Count == 0)
                ? $"it comes from {origin}"
                : $"in {string.Join(" and ", sides.Where(side => side.Where.ComesFrom.Count > 0).Select(side => side.Name))} " +
                  $"it comes from {origin}");
        clauses.AddRange(sides
            .Where(side => side.Where.ForwardsTo.Count > 0)
            .GroupBy(side => SymbolResolver.OriginOf(side.Where.ForwardsTo), StringComparer.Ordinal)
            .Select(group => group.Count() > 1
                ? $"{string.Join(" and ", group.Select(side => side.Name))} forward it to {group.Key}"
                : $"{group.Single().Name} forwards it to {group.Key}"));
        if (clauses.Count > 0)
            return new ArgumentException(
                $"Type '{typeName}' is not defined in either assembly; {string.Join("; ", clauses)}.");

        // Where the types are written in groups, "each build" or "it" names the assembly.
        var (inA, inB) = (A.Resolver.Meant(typeName, "each build"), B.Resolver.Meant(typeName, "each build"));
        var meant = inA != null && inA == inB
            ? $" Types {inA}."
            : string.Concat(new[] { (Name: "A", Host: A), (Name: "B", Host: B) }
                .Select(side => (side.Name, Types: side.Host.Resolver.Meant(typeName, "it")))
                .Where(side => side.Types != null)
                .Select(side => $" In {side.Name}, types {side.Types}."));
        return new ArgumentException($"Type '{typeName}' not found in either assembly.{meant}");
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

        var aMembers = MembersByKey(a);
        var bMembers = MembersByKey(b);

        if (aMembers.Keys.Except(bMembers.Keys).Any() ||
            bMembers.Keys.Except(aMembers.Keys).Any())
            kinds |= ChangeKinds.Members;

        var matched = aMembers.Keys.Where(bMembers.ContainsKey).ToList();
        if (matched.Any(key => DeclarationsDiffer(aMembers[key], bMembers[key])))
            kinds |= ChangeKinds.Declarations;
        if (matched.Any(key => ValueDiffers(aMembers[key].Symbol, bMembers[key].Symbol)))
            kinds |= ChangeKinds.Values;
        if (matched.Any(key => ChangedBodies(aMembers[key].Symbol, bMembers[key].Symbol).Count > 0))
            kinds |= ChangeKinds.Bodies;
        return kinds;
    }

    /// <summary>
    /// The names of the bodies that differ between a member matched in A and in B: the method
    /// itself, or a property's or event's accessors, which ILSpy does not list among a type's
    /// methods. Accessors pair by role, so an accessor only one side has counts as changed and
    /// is named from the side that has it. A member written in source is compared with the code
    /// generated for it. A generated member, which only a comparer that includes generated
    /// members lists, is compared as it stands. None where no body is compared
    /// (<see cref="ComparesBodies"/>).
    /// </summary>
    private List<string> ChangedBodies(ISymbol a, ISymbol b)
    {
        if (!ComparesBodies)
            return [];
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
    /// One-line snapshot of a type's class-level metadata for equality comparison: the words its
    /// declaration writes before its name, as list_members' heading writes them
    /// (<see cref="SignatureFormatter.FormatTypeKeywords"/>), which are its accessibility, static,
    /// abstract or sealed, readonly, ref, record and its kind; its base type, an enum's underlying
    /// type, its direct interfaces, and its own type parameters. The base type, the interfaces and
    /// the types a condition names are recorded by <c>ReflectionName</c>, not <c>FullName</c>,
    /// which drops the generic arity and type arguments, so a base that moves from <c>Task</c> to
    /// <c>Task&lt;int&gt;</c>, or from <c>List&lt;int&gt;</c> to <c>List&lt;string&gt;</c>,
    /// counts as a change. Each type parameter is an entry of its own, by position, as
    /// <c>ReflectionName</c> names a type parameter, so that a type parameter renamed alone does
    /// not count: its in or out, and its conditions, sorted, as the interfaces are, so that a
    /// where clause reordered in source does not count either. A type whose name does not count
    /// its type parameters, as the F# compiler names the classes it generates, can gain or lose
    /// one under that name, and the number of entries makes that a change as well. The attributes
    /// on a type are not compared, beyond the two the compiler writes for readonly and ref, which
    /// the words show (a future flag could opt the others in).
    /// </summary>
    private static string TypeMetadataSignature(ITypeDefinition type)
    {
        var baseType = type.DirectBaseTypes
            .FirstOrDefault(t => t.GetDefinition()?.Kind == TypeKind.Class)?.ReflectionName ?? "";
        var underlying = type.Kind == TypeKind.Enum ? type.EnumUnderlyingType?.ReflectionName ?? "" : "";
        var interfaces = string.Join(",",
            type.DirectBaseTypes
                .Where(t => t.GetDefinition()?.Kind == TypeKind.Interface)
                .Select(t => t.ReflectionName)
                .OrderBy(s => s, StringComparer.Ordinal));
        var parameters = string.Join(";", TypeWalker.OwnTypeParameters(type)
            .Select(parameter => $"{parameter.Variance}:" + string.Join(",",
                ReferenceFormatter.FormatConditions(parameter, t => t.ReflectionName).Order(StringComparer.Ordinal))));
        return $"words={SignatureFormatter.FormatTypeKeywords(type)};base={baseType};underlying={underlying};" +
            $"ifaces={interfaces};parameters={parameters}";
    }

    /// <summary>
    /// A member as the comparison lists it: its match key, its line, and its declaration, which
    /// is the line in full without a constant's value and without the parameters' defaults
    /// (<see cref="SignatureFormatter.FormatDeclaration"/>).
    /// </summary>
    private sealed record MemberEntry(
        ISymbol Symbol,
        SymbolCategory Category,
        string MatchKey,
        string DisplaySignature,
        string Declaration);

    /// <summary>
    /// The members of <paramref name="type"/> the comparison lists, each with its match key:
    /// those written in source, and the generated ones as well when the comparer includes them.
    /// </summary>
    private IEnumerable<MemberEntry> EnumerateMembers(ITypeDefinition type)
    {
        var members = type.DeclaredMethods().Select(m => Entry(m, SymbolCategory.Method))
            .Concat(type.Properties.Select(p => Entry(p, SymbolCategory.Property)))
            .Concat(type.Fields.Select(f => Entry(f, SymbolCategory.Field)))
            .Concat(type.Events.Select(e => Entry(e, SymbolCategory.Event)));
        return _includeCompilerGenerated ? members : members.Where(entry => !IsGenerated(entry.Symbol));

        static MemberEntry Entry(IMember member, SymbolCategory category) =>
            new(member, category, BuildMatchKey(member), SignatureFormatter.FormatMember(member),
                SignatureFormatter.FormatDeclaration(member));
    }

    /// <summary>
    /// The members of <paramref name="type"/> by match key. Members whose keys are alike are told
    /// apart by the order they stand in the type, so that the first of them in A is compared with
    /// the first in B. The key holds the full signature, so only metadata no C# or VB compiler
    /// writes has such members, such as two fields of one name and type from an obfuscator.
    /// Without the order, one such type would end the comparison of the whole assembly.
    /// </summary>
    /// <remarks>
    /// Where such members stand in another order in the two assemblies, the order pairs the wrong
    /// ones, and the comparison reports their bodies as changed.
    /// </remarks>
    private Dictionary<string, MemberEntry> MembersByKey(ITypeDefinition type)
    {
        var members = new Dictionary<string, MemberEntry>();
        foreach (var entry in EnumerateMembers(type))
        {
            var key = entry.MatchKey;
            for (var place = 2; !members.TryAdd(key, entry); place++)
                key = $"{entry.MatchKey}\n{place}";
        }
        return members;
    }

    /// <summary>
    /// Cross-assembly identity key for a member: its name, a method's number of type
    /// parameters, each parameter's type with the keyword it is passed by, and the return type,
    /// so overloads survive matching and any change to them surfaces as remove+add rather than
    /// silently morphing one entry into another. Every type is written in full
    /// (<see cref="ReferenceFormatter.FormatTypeIdentity"/>): in the short form ILens prints,
    /// two overloads that take classes of one name from two namespaces would have one key. A
    /// property's parameters are part of its key whenever it has any, not only for an indexer:
    /// VB declares properties with parameters under any name and overloads them, and ILSpy reads
    /// as an indexer only the property its class names as the default member.
    /// </summary>
    private static string BuildMatchKey(ISymbol member) => member switch
    {
        IMethod method =>
            "M:" + method.Name +
            (method.TypeParameters.Count > 0 ? "`" + method.TypeParameters.Count : "") +
            "(" + string.Join(",", method.Parameters.Select(BuildParamKey)) + ")" +
            ":" + BuildReturnKey(method),
        IProperty prop => prop.Parameters.Count > 0
            ? "P:" + prop.Name +
              "(" + string.Join(",", prop.Parameters.Select(BuildParamKey)) + ")" +
              ":" + BuildReturnKey(prop)
            : "P:" + prop.Name + ":" + BuildReturnKey(prop),
        IField field => "F:" + field.Name + ":" + ReferenceFormatter.FormatTypeIdentity(field.Type),
        IEvent evt => "E:" + evt.Name + ":" + ReferenceFormatter.FormatTypeIdentity(evt.ReturnType),
        _ => member.ToString() ?? "?",
    };

    /// <summary>
    /// Parameter-position key for <see cref="BuildMatchKey"/> — the full type plus the
    /// ref, out, in or ref readonly keyword, which IS part of the CLR signature and lets
    /// <c>Foo(int)</c>, <c>Foo(ref int)</c>, <c>Foo(out int)</c> coexist as
    /// distinct overloads. Drops the parameter name (irrelevant for matching).
    /// </summary>
    private static string BuildParamKey(IParameter p) =>
        ReferenceFormatter.PassingKeyword(ParameterPassing.Of(p)) + ReferenceFormatter.FormatTypeIdentity(p.Type);

    /// <summary>
    /// Return-type key for <see cref="BuildMatchKey"/>: the full type, after <c>ref</c> or
    /// <c>ref readonly</c> for a member that returns by reference.
    /// </summary>
    private static string BuildReturnKey(IMember member) =>
        ReferenceFormatter.ReturnKeyword(member) + ReferenceFormatter.FormatTypeIdentity(member.ReturnType);
}
