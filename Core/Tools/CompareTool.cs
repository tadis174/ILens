using System.ComponentModel;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class CompareTool
{
    [McpServerTool(Name = "compare", ReadOnly = true),
     Description("Compare two versions of an assembly, from the types that differ down to one method: " +
        "without type_name it lists the changed types, with type_name what changed in that type, and " +
        "with member_name a diff of a method's or accessor's two bodies, or only a verdict when they print " +
        "alike, or a constant's two values. " +
        "A line starts with + for what only B has, - for what only A has, and ~ for what both have but " +
        "changed; a changed type names what changed. Members are matched by name, parameter types and " +
        "return type, so a changed signature is a removal and an addition. Bodies are compared as IL with " +
        "tokens resolved to names, so a method a rebuild leaves unchanged does not show as changed.")]
    public static string Compare(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.AssemblyA)] string assembly_a,
        [Description(ParameterDescriptions.AssemblyB)] string assembly_b,
        [Description(ParameterDescriptions.TypeName)] string type_name = null,
        [Description(ParameterDescriptions.MemberName)] string member_name = null,
        [Description(ParameterDescriptions.ParameterCount)] int? parameter_count = null,
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null,
        [Description(ParameterDescriptions.ReturnType)] string return_type = null,
        [Description(ParameterDescriptions.Namespace)] string @namespace = null,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description("How to print the two bodies: csharp decompiles them, il disassembles them as the comparison reads them.")]
        CompareFormat format = CompareFormat.Csharp,
        [Description("Unchanged lines shown around each change; more than the method has shows it whole, each change marked.")]
        int context = BodyDiff.DefaultContext,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        var hint = new SymbolResolver.MethodHint(parameter_count, TypeMatcher.SplitPatterns(parameter_types), return_type);
        // A name with the '@' C# writes before a keyword, as a declaration line writes it, means the keyword.
        member_name = SymbolResolver.ReadName(member_name);

        // Each parameter belongs to one level of the drill-down. Dropped silently at another,
        // it would leave the answer to a broader question looking like the answer to the one asked.
        var byType = !string.IsNullOrEmpty(type_name);
        var byMember = !string.IsNullOrEmpty(member_name);
        if (byMember && !byType)
            throw new ArgumentException($"{nameof(member_name)} needs {nameof(type_name)}.");
        if (!byMember && hint.IsGiven)
            throw new ArgumentException(
                $"{nameof(parameter_count)}, {nameof(parameter_types)} and {nameof(return_type)} select a " +
                $"method overload, so they need {nameof(member_name)}.");
        if (byType && !string.IsNullOrEmpty(@namespace))
            throw new ArgumentException($"{nameof(@namespace)} applies only without {nameof(type_name)}.");
        if (byMember && include_compiler_generated)
            throw new ArgumentException(
                $"{nameof(include_compiler_generated)} applies only without {nameof(member_name)}.");
        if (!byMember && format != CompareFormat.Csharp)
            throw new ArgumentException($"{nameof(format)} applies only with {nameof(member_name)}.");
        if (!byMember && context != BodyDiff.DefaultContext)
            throw new ArgumentException($"{nameof(context)} applies only with {nameof(member_name)}.");

        using var leaseA = registry.GetOrLoad(assembly_a);
        using var leaseB = registry.GetOrLoad(assembly_b);
        var comparer = new AssemblyComparer(leaseA.Host, leaseB.Host, include_compiler_generated);

        if (byMember)
            return CompareBodies(leaseA.Host, leaseB.Host, comparer, type_name, member_name, hint, format, context);
        if (byType)
            return CompareMembers(comparer, type_name, limit);
        return CompareTypes(leaseA.Host, leaseB.Host, comparer, @namespace, limit);
    }

    private static string CompareTypes(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        string @namespace, int limit)
    {
        // An exact namespace asserts it exists, so a misspelling is an error rather than an
        // answer that reads as "nothing changed there".
        if (!string.IsNullOrEmpty(@namespace)
            && !hostA.Resolver.HasNamespace(@namespace) && !hostB.Resolver.HasNamespace(@namespace))
            throw new ArgumentException($"Namespace not found in either assembly: {@namespace}.");

        var lines = comparer
            .EnumerateChangedTypes(@namespace)
            .OrderBy(change => change.TypeName, StringComparer.Ordinal)
            .Select(FormatTypeChange)
            .ToList();
        return ListOutput.Format(lines, limit, new Noun("changed type", "changed types"), notShown: NotCompared(comparer));
    }

    /// <summary>
    /// The line a list of changes ends with where a build is a reference assembly, whose bodies
    /// the comparison does not read (<see cref="AssemblyComparer.ComparesBodies"/>), so that the
    /// list without a body change does not read as code that stayed the same:
    /// <c>Not compared: bodies; A is a reference assembly, which holds no code</c>. Null where
    /// bodies are compared.
    /// </summary>
    private static string NotCompared(AssemblyComparer comparer) =>
        comparer.ComparesBodies ? null
        : $"Not compared: bodies; {HoldsNoCode(comparer.A.IsReferenceAssembly, comparer.B.IsReferenceAssembly)}";

    /// <summary>
    /// What a build that is a reference assembly holds, said of <paramref name="build"/>:
    /// <c>B is a reference assembly, which holds no code</c>.
    /// </summary>
    private static string HoldsNoCode(string build) => $"{build} is a reference assembly, which holds no code";

    /// <summary>
    /// What the builds that are reference assemblies hold, said of each that is one:
    /// <c>A and B are reference assemblies, which hold no code</c>, or of one build as
    /// <see cref="HoldsNoCode(string)"/> says it; null where neither is one.
    /// </summary>
    private static string HoldsNoCode(bool a, bool b) => (a, b) switch
    {
        (true, true) => "A and B are reference assemblies, which hold no code",
        (true, false) => HoldsNoCode("A"),
        (false, true) => HoldsNoCode("B"),
        _ => null,
    };

    /// <summary>
    /// What stands where the code of <paramref name="member"/>, a member of
    /// <paramref name="build"/>, a reference assembly, would stand: its declaration, as the member
    /// list writes it, and a line that says the build holds no code, since a stub that throws null
    /// would read as the member's code.
    /// </summary>
    private static string WithoutCode(string build, IMember member) =>
        $"{SignatureFormatter.FormatMember(member)}\n// {HoldsNoCode(build)}.";

    /// <summary>
    /// A changed type's line in the type list. A type only one build defines is followed by where
    /// the other takes it from, as when it moved to a library, so that it does not read as deleted.
    /// </summary>
    private static string FormatTypeChange(AssemblyComparer.TypeChange change) => change.Presence switch
    {
        AssemblyComparer.TypePresence.Added => $"+ {change.TypeName}{Then(", ", WhereClause(change.Elsewhere, "A"))}",
        AssemblyComparer.TypePresence.Removed => $"- {change.TypeName}{Then(", ", WhereClause(change.Elsewhere, "B"))}",
        _ => $"~ {change.TypeName} [{FormatKinds(change.Kinds)}]",
    };

    /// <summary>
    /// Where <paramref name="side"/>, a build that does not define a type, takes it from, as its
    /// metadata records it (<see cref="SymbolResolver.WhereElse"/>), said of
    /// <paramref name="subject"/>: <c>in B it comes from Lib, Version=1.0.0.0</c> or
    /// <c>B forwards it to Lib, Version=1.0.0.0</c>; without a subject as a clause after the
    /// type's name, <c>which in B comes from …</c> or <c>which B forwards to …</c>. Null where the
    /// metadata names no assembly.
    /// </summary>
    private static string WhereClause(SymbolResolver.Elsewhere elsewhere, string side, string subject = null) => elsewhere switch
    {
        null => null,
        { Forwarded: true } => subject is null
            ? $"which {side} forwards to {elsewhere.Assembly}"
            : $"{side} forwards {subject} to {elsewhere.Assembly}",
        _ => subject is null
            ? $"which in {side} comes from {elsewhere.Assembly}"
            : $"in {side} {subject} comes from {elsewhere.Assembly}",
    };

    private static string Then(string separator, string clause) => clause is null ? "" : separator + clause;

    private static string FormatKinds(AssemblyComparer.ChangeKinds kinds)
    {
        var parts = new List<string>(5);
        if ((kinds & AssemblyComparer.ChangeKinds.Metadata) != 0) parts.Add("metadata");
        if ((kinds & AssemblyComparer.ChangeKinds.Members) != 0) parts.Add("members");
        if ((kinds & AssemblyComparer.ChangeKinds.Declarations) != 0) parts.Add("declarations");
        if ((kinds & AssemblyComparer.ChangeKinds.Values) != 0) parts.Add("values");
        if ((kinds & AssemblyComparer.ChangeKinds.Bodies) != 0) parts.Add("bodies");
        return string.Join(",", parts);
    }

    private static string CompareMembers(AssemblyComparer comparer, string typeName, int limit)
    {
        var diff = comparer.CompareType(typeName);
        var lines = diff.Changes
            .OrderBy(change => change.Name, StringComparer.Ordinal)
            .ThenBy(change => change.Signature, StringComparer.Ordinal)
            .Select(change => change.Kind switch
            {
                AssemblyComparer.MemberChangeKind.Added => $"+ {change.Signature}",
                AssemblyComparer.MemberChangeKind.Removed => $"- {change.Signature}",
                _ => $"~ {change.Signature}{BodiesMark(change)}",
            })
            .ToList();
        var subject = diff.Side switch
        {
            AssemblyComparer.TypePresence.Added =>
                $"in {diff.TypeName}, which only B has{Then("; ", WhereClause(diff.Elsewhere, "A", "it"))}",
            AssemblyComparer.TypePresence.Removed =>
                $"in {diff.TypeName}, which only A has{Then("; ", WhereClause(diff.Elsewhere, "B", "it"))}",
            _ => $"in {diff.TypeName}",
        };
        var list = ListOutput.Format(lines, limit, new Noun("member change", "member changes"), subject,
            NotCompared(comparer));

        // A change of the type's own declaration, which the type list marks metadata, shows in no
        // member, so the view ends with both declarations.
        return diff.Declaration is null ? list : $"{list}\nDeclaration: {diff.Declaration}";
    }

    /// <summary>
    /// What follows a changed member's line where bodies changed. A property or event names its
    /// accessors whose bodies changed, which are what member_name takes to compare the two bodies.
    /// A method's line alone says that its body changed, unless the line holds two declarations,
    /// so there the body is named. A constant has no body, so its line takes no mark; it holds
    /// both values.
    /// </summary>
    private static string BodiesMark(AssemblyComparer.MemberChange change) =>
        change.Bodies is not { Count: > 0 } bodies ? ""
        : change.Category != SymbolCategory.Method ? $" [{string.Join(",", bodies)}]"
        : change.Kind == AssemblyComparer.MemberChangeKind.DeclarationChanged ? " [body]"
        : "";

    private static string CompareBodies(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        string typeName, string memberName, SymbolResolver.MethodHint hint, CompareFormat format, int context)
    {
        // A type neither build defines holds no code in either, but an extension method a build
        // declares for it does, which is compared as one found through a type the builds define.
        // So does one for an array, where a member of System.Array or System.Object that comes
        // first is the question asked through the class that declares it.
        if (comparer.ResolveArrayExtensionPair(typeName, memberName, hint) is { } arrays)
        {
            if (arrays.MemberOwner is not { } owner)
                return CompareExtensions(hostA, hostB, comparer, arrays.Extensions, format, context);
            // Builds whose code uses arrays through their own instructions alone name neither class.
            if (hostA.Resolver.UnnamedOwnerOrigin(owner) is { } origin && hostB.Resolver.UnnamedOwnerOrigin(owner) is not null)
                throw new ArgumentException($"Type '{owner}' is not defined in either assembly; it comes from {origin}.");
            return CompareBodies(hostA, hostB, comparer, owner, memberName, hint, format, context);
        }
        if (comparer.ResolveExtensionPair(typeName, memberName, hint) is { } extensions)
            return CompareExtensions(hostA, hostB, comparer, extensions, format, context);

        var (aType, bType) = comparer.ResolveTypePair(typeName);
        var hinted = hint.IsGiven;

        // Where one build does not define the type, each line that says only one build has a member
        // ends with where that build takes the type from, as when the type moved to a library.
        var elsewhere = aType is null ? ElsewhereClause(hostA, "A", bType)
            : bType is null ? ElsewhereClause(hostB, "B", aType)
            : "";

        // A constant has a value where a method has a body. The name means the constant where the
        // nearest class that has the name declares a constant of it, which hides a base class's
        // method of the name as in C#; a hint asks for a method.
        if (!hinted && comparer.ResolveConstantPair(aType, bType, memberName) is { } constants)
            return CompareValues(constants.A, constants.B, elsewhere);

        var (a, b) = comparer.ResolveMethodPair(aType, bType, memberName, hint);

        // A name that finds members of different kinds in the two builds, such as a field in A and
        // a property in B, is answered with both, since neither is the whole difference. A hint
        // asks for a method.
        if (!hinted && comparer.ResolveKindPair(a, b, memberName) is { } kinds)
            return CompareKinds(hostA, hostB, kinds.A, kinds.B, memberName, format);

        if (a.Method is null && b.Method is null)
        {
            // Neither assembly has such a method, and decompile's lookup says what the name is. It
            // asks A, unless A lacks the type or only B has a member of the name. A hint asks for a
            // method, which neither has, so with a hint it asks A whenever A has the type, and the
            // error lists A's overloads.
            var inB = a.Type is null
                || (!hinted && b.Type is not null
                    && !hostA.Resolver.HasMemberNamed(a.Type, memberName)
                    && hostB.Resolver.HasMemberNamed(b.Type, memberName));
            var (side, host, type, otherHost, otherType) =
                inB ? ("B", hostB, b.Type, hostA, a.Type) : ("A", hostA, a.Type, hostB, b.Type);
            var (symbol, origin, _) = host.Resolver.ResolveMember(type, memberName, hint);

            // Only a method or an accessor has a body. A field has none, so it is an error that names
            // the constructors that set it. A property or event only one assembly has is the whole
            // difference, so its code is shown, as a method's is; one both have is compared through
            // its accessors.
            var resolved = new AssemblyComparer.MemberSide(type, (IMember)symbol, origin);
            if (symbol is IField field)
            {
                var (aField, bField) = inB ? (null, resolved) : (resolved, FieldIn(hostB, b.Type, memberName));
                var (name, clause) = aField is not null && bField is not null
                    ? Naming(aField, bField, memberName, "fields")
                    : (ReferenceFormatter.FormatSymbol(field), "");
                throw new ArgumentException(FieldHasNoBody(name, clause,
                    SettersOf(hostA, aField?.Member as IField), SettersOf(hostB, bField?.Member as IField), elsewhere));
            }
            if (symbol is IProperty or IEvent
                && (inB || otherType is null || !otherHost.Resolver.HasMemberNamed(otherType, memberName)))
                return $"// {Reference(type, (IMember)symbol, origin)} — only {side} has it{elsewhere}.\n\n" +
                    MemberCode(host, side, type, (IMember)symbol, format);
            throw new ArgumentException(
                NotAMethod(resolved, inB ? null : SameKindIn(hostB, b.Type, symbol, memberName), memberName));
        }

        return CompareMethods(hostA, hostB, comparer, a, b, format, context, elsewhere);
    }

    /// <summary>
    /// The clause a line of the member view that says only one build has a member ends with where
    /// <paramref name="side"/> does not define <paramref name="type"/>, which the other build
    /// defines: <c>; in B Shop.Kettle comes from Lib, Version=1.0.0.0</c> or
    /// <c>; B forwards Shop.Kettle to Lib, Version=1.0.0.0</c>; nothing where its metadata names no
    /// assembly for the type.
    /// </summary>
    private static string ElsewhereClause(AssemblyHost host, string side, ITypeDefinition type) =>
        Then("; ", WhereClause(host.Resolver.WhereElse(type.FullTypeName), side, ReferenceFormatter.FormatTypeName(type)));

    /// <summary>
    /// <see cref="CompareBodies"/> for an extension method found through a type neither build
    /// defines or an array type, in one build or both, with the lines that say what a lookup could
    /// not read first. A build that finds none only because a method of the type of that name
    /// comes first there, or because the type does not fit the constraints of a receiver that is a
    /// type parameter, declares the method all the same, so the answer says so in place of naming
    /// the method as one only the other build has.
    /// </summary>
    private static string CompareExtensions(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        AssemblyComparer.ExtensionPair pair, CompareFormat format, int context)
    {
        if (pair.BehindMember || pair.DoesNotFit)
        {
            var notChecked = NotCheckedLines(pair.A?.NotChecked, pair.B?.NotChecked);
            var (found, host, side, other) = pair.A is null ? (pair.B, hostB, "B", "A") : (pair.A, hostA, "A", "B");
            var copy = CopyToShow(host, found.Method, format);
            var why = pair.BehindMember
                ? "the type has a member of that name, which C# takes first"
                : $"{pair.TypeDisplay} does not meet the method's constraints";
            return notChecked +
                $"// {Reference(host, AssemblyComparer.MethodSide.Through(found), copy)} — only {side} finds it " +
                $"through {pair.TypeDisplay}; in {other} {why}.\n\n" +
                CodeToShow(host, side, found.Method, copy, format);
        }
        return CompareMethods(hostA, hostB, comparer,
            AssemblyComparer.MethodSide.Through(pair.A), AssemblyComparer.MethodSide.Through(pair.B), format, context);
    }

    /// <summary>
    /// The methods a name found in the two builds, at least one of them: the code of a method only
    /// one build has, or the verdict on the two bodies or their diff, under the lines that say what
    /// the diff alone does not show. Where neither build has a body, or a build is a reference
    /// assembly, which holds no code, the verdict says so in place of comparing two texts that read
    /// alike or a stub with code. <paramref name="elsewhere"/> ends the line for a method only one
    /// build has, where the other does not define the type (<see cref="ElsewhereClause"/>).
    /// </summary>
    private static string CompareMethods(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        AssemblyComparer.MethodSide a, AssemblyComparer.MethodSide b, CompareFormat format, int context,
        string elsewhere = "")
    {
        // An extension method a build found while a base class of the type, or the type of another
        // assembly it was found through, was not read may stand where a member of that class would
        // answer, which the first lines say.
        var notChecked = NotCheckedLines(NotChecked(hostA, a), NotChecked(hostB, b));

        // A method only one assembly has is the whole difference, so its code is shown alone. An
        // extension method whose receiver is a type parameter that the type asked through fits in
        // one build alone is declared in the other as well, which the line says in its place.
        if (a.Method is null || b.Method is null)
        {
            var (side, host, found, other, otherHost, otherType) =
                a.Method is null ? ("B", hostB, b, "A", hostA, a.Type) : ("A", hostA, a, "B", hostB, b.Type);
            var copy = CopyToShow(host, found.Method, format);
            // A build that found the method through a type of another assembly whose library was not
            // read has no type of its own, so the type is named as the other build has it.
            var why = AssemblyComparer.DoesNotFitIn(otherHost, otherType, found.Method)
                ? $"only {side} finds it through {ReferenceFormatter.FormatTypeName(found.Type ?? otherType)}; " +
                    $"in {other} {ReferenceFormatter.FormatTypeName(otherType)} does not meet the method's constraints"
                : $"only {side} has it";
            return $"{notChecked}// {Reference(host, found, copy)} — {why}{elsewhere}.\n\n" +
                CodeToShow(host, side, found.Method, copy, format);
        }

        var aCopy = CopyToShow(hostA, a.Method, format);
        var bCopy = CopyToShow(hostB, b.Method, format);

        // Each assembly is asked for the name on its own, so the two methods can be what the member
        // list counts as a removal and an addition: their signatures differ, or one lies on another
        // class. They are compared all the same, under a line that names both, each in full where
        // the two would read alike or where a short name among the types their lines write stands
        // for two different classes.
        var oneMember = AssemblyComparer.AreOneMember(a.Method, b.Method);
        var subject = oneMember
            ? Reference(hostA, a, aCopy ?? bCopy)
            : $"{Reference(hostA, a, aCopy, b.Method)} in A and {Reference(hostB, b, bCopy, a.Method)} in B are different methods";

        // A method whose declaration changed is named with both declarations first, as the member
        // list writes it, since neither the verdict nor the IL of a body shows the declaration.
        var declarations = notChecked + (oneMember ? DeclarationsLine(a.Method, b.Method) : "");

        // A reference assembly holds no code, so no body of one is compared. The other build's code
        // is all the code there is, so it is shown where that build has a body.
        if (!comparer.ComparesBodies)
        {
            var marked = HoldsNoCode(hostA.IsReferenceAssembly, hostB.IsReferenceAssembly);
            if (hostA.IsReferenceAssembly && hostB.IsReferenceAssembly)
                return $"{declarations}// {subject} — {marked} to compare.";
            var (other, otherHost, otherMethod, otherCopy) = hostA.IsReferenceAssembly
                ? ("B", hostB, b.Method, bCopy)
                : ("A", hostA, a.Method, aCopy);
            var missing = otherHost.WhyNoBody(otherMethod);
            return missing != AssemblyHost.MissingBody.None
                ? $"{declarations}// {subject} — {marked}, and {other} has no body for it{Reason(missing)}."
                : $"{declarations}// {subject} — {marked} to compare with {other}'s.\n\n" +
                    CodeToShow(otherHost, other, otherMethod, otherCopy, format);
        }

        // Where neither build has a body there is nothing to compare either, which two texts that
        // read alike would hide, so the answer says so, and why where each build's row says.
        var (aMissing, bMissing) = (hostA.WhyNoBody(a.Method), hostB.WhyNoBody(b.Method));
        if (aMissing != AssemblyHost.MissingBody.None && bMissing != AssemblyHost.MissingBody.None)
            return $"{declarations}// {subject} — neither assembly has a body for it{Reasons(aMissing, bMissing)}.";

        // Two bodies that print alike are answered by the verdict alone: a copy would show nothing
        // of what changed, and decompile returns the code. The verdict is the one the member list
        // gives, which also counts the code generated for the method; the C# can read alike where
        // the IL does not.
        if (comparer.HaveEqualCode(a.Method, b.Method))
            return $"{declarations}// {subject} — bodies are identical in both assemblies.";
        var aBody = CodeToShow(hostA, "A", a.Method, aCopy, format);
        var bBody = CodeToShow(hostB, "B", b.Method, bCopy, format);
        if (aBody == bBody)
            return $"{declarations}// {subject} — bodies decompile alike in both assemblies, but their IL differs; " +
                "format il shows where.";

        // The call names the method, so the line before the diff stands only where it says more:
        // that the two are different methods, where a method was found, or that the code is
        // Burst's copy.
        var plain = ReferenceFormatter.FormatResolvedSignature(a.Method, a.Type);
        var header = declarations + (subject != plain ? $"// {subject}\n" : "");
        return (header.Length > 0 ? header + "\n" : "") +
            BodyDiff.Format(aBody, bBody, hostA.AssemblyPath, hostB.AssemblyPath, context, format == CompareFormat.Il);
    }

    /// <summary>
    /// Why neither build has a body for a method, from what each one's row says
    /// (<see cref="AssemblyHost.WhyNoBody"/>): <c>: abstract in both</c>, <c>: abstract in A, extern
    /// in B</c>, or for one build alone where the other's row does not say; nothing where neither
    /// says.
    /// </summary>
    private static string Reasons(AssemblyHost.MissingBody a, AssemblyHost.MissingBody b) => (Word(a), Word(b)) switch
    {
        (null, null) => "",
        var (x, y) when x == y => $": {x} in both",
        (var x, null) => $": {x} in A",
        (null, var y) => $": {y} in B",
        var (x, y) => $": {x} in A, {y} in B",
    };

    /// <summary>Why one build has no body for a method, as <see cref="Reasons"/> writes it: <c>: abstract</c>.</summary>
    private static string Reason(AssemblyHost.MissingBody missing) => Word(missing) is { } word ? $": {word}" : "";

    /// <summary>The word for why a method has no body; null where its row does not say.</summary>
    private static string Word(AssemblyHost.MissingBody missing) => missing switch
    {
        AssemblyHost.MissingBody.Abstract => "abstract",
        AssemblyHost.MissingBody.Extern => "extern",
        AssemblyHost.MissingBody.Runtime => "provided by the runtime",
        _ => null,
    };

    /// <summary>
    /// The copy of a method's code that Burst compiles, which is the C# to show for the method,
    /// since Burst turns the method into a call to its wrapper; null where there is none, and in IL,
    /// which shows the method as the comparison reads it.
    /// </summary>
    private static IMethod CopyToShow(AssemblyHost host, IMethod method, CompareFormat format) =>
        format == CompareFormat.Il ? null : AssemblyComparer.BurstManagedCopyOf(host, method);

    /// <summary>
    /// A method's code as the answer shows it: its IL as the comparison reads it, or its C#, that of
    /// Burst's copy where there is one; where <paramref name="build"/> is a reference assembly, its
    /// declaration in place of the stub (<see cref="WithoutCode"/>).
    /// </summary>
    private static string CodeToShow(AssemblyHost host, string build, IMethod method, IMethod copy, CompareFormat format) =>
        host.IsReferenceAssembly ? WithoutCode(build, method)
        : format == CompareFormat.Il ? AssemblyComparer.CodeOf(host, method)
        : host.DecompileMethod(copy ?? method);

    /// <summary>
    /// The answer for a name that finds members of different kinds in the two builds, such as a
    /// field in A and a property in B: a line with both kinds and both declarations, as the member
    /// list writes them, and then the code of each member that has code, a method's, a property's
    /// or an event's, under a line that names its build and the member as decompile's header does.
    /// A field or a constant has no code, so the line holds all there is of it, a constant's value
    /// included, and where its build found it off the type, its kind says where. An extension
    /// method found through a type whose base class was not read, or through a type of another
    /// assembly whose library was not read, may stand where a member of that class would answer,
    /// which the first lines say, as for a method.
    /// </summary>
    private static string CompareKinds(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer.MemberSide a,
        AssemblyComparer.MemberSide b, string memberName, CompareFormat format)
    {
        var notChecked = NotCheckedLines(NotChecked(hostA, a.Type, a.Member, a.NotChecked),
            NotChecked(hostB, b.Type, b.Member, b.NotChecked));
        // A build that found an extension method through a type of another assembly whose library
        // was not read has no type of its own, so the type is named as the other build has it. The
        // two declarations are written in full where a name in them stands for two different types.
        var (aDeclaration, bDeclaration) = ReferenceFormatter.Sides(a.Member, b.Member,
            member => SignatureFormatter.FormatMember(member), member => SignatureFormatter.FormatMember(member, full: true),
            AssemblyComparer.TypesNamed);
        var line = $"{notChecked}// {ReferenceFormatter.FormatTypeName(a.Type ?? b.Type)}.{memberName} " +
            $"is {Kind(a)} in A and {Kind(b)} in B: {aDeclaration} → {bDeclaration}";
        var blocks = new[] { (Build: "A", Host: hostA, Side: a), (Build: "B", Host: hostB, Side: b) }
            .Where(entry => entry.Side.Member is not IField)
            .Select(entry => Block(entry.Build, entry.Host, entry.Side))
            .ToList();
        return blocks.Count == 0 ? line : $"{line}\n\n{string.Join("\n", blocks)}";

        static string Kind(AssemblyComparer.MemberSide side)
        {
            var word = side.Member is IMethod && side.Origin.Kind == "extension"
                ? "extension method"
                : AssemblyComparer.KindOf(side.Member);
            var kind = (word is "event" or "extension method" ? "an " : "a ") + word;
            return side.Member is IField && side.Origin.Kind != "declared" ? $"{kind} {side.Origin.Format()}" : kind;
        }

        // Each block ends with a line break, so a blank line stands between two.
        string Block(string build, AssemblyHost host, AssemblyComparer.MemberSide side)
        {
            string header, code;
            if (side.Member is IMethod method)
            {
                var copy = CopyToShow(host, method, format);
                header = Reference(host, new AssemblyComparer.MethodSide(side.Type, method, side.Origin, side.Namesakes), copy);
                code = CodeToShow(host, build, method, copy, format);
            }
            else
            {
                header = Reference(side.Type, side.Member, side.Origin);
                code = MemberCode(host, build, side.Type, side.Member, format);
            }
            return $"// {build}: {header}\n{code}{(code.EndsWith('\n') ? "" : "\n")}";
        }
    }

    /// <summary>
    /// The line that names a member's declaration in A and in B where the two read otherwise, as
    /// the member list writes it (<see cref="AssemblyComparer.DeclarationChange"/>), with the line
    /// break after it; nothing where they read alike. An
    /// accessor is named by its property's or event's line, which the member list writes, since a
    /// method's line cannot show an init accessor or an accessor's own accessibility beside its
    /// property's.
    /// </summary>
    private static string DeclarationsLine(IMember a, IMember b)
    {
        if (a is IMethod { AccessorOwner: { } aOwner } && b is IMethod { AccessorOwner: { } bOwner })
            (a, b) = (aOwner, bOwner);
        return AssemblyComparer.DeclarationsDiffer(a, b) ? $"// {AssemblyComparer.DeclarationChange(a, b)}\n" : "";
    }

    /// <summary>
    /// A method as the answer names it: as decompile's header does, under the type asked about, or
    /// for an extension method under the class that declares it, with where it was found when the
    /// type asked about does not declare it, and with the copy of its code Burst compiles, where
    /// there is one. It is written in full where another method of its name in its build, or
    /// <paramref name="alongside"/>, the other build's method the line names beside it, would read
    /// alike, and where a short name stands for two different types among the types it and
    /// alongside's line write, as when a parameter's class gave way to one of the same name in
    /// another namespace beside another change. Those are the types after the name, read without
    /// the name, which carries each method's class, another one for an extension method.
    /// </summary>
    private static string Reference(AssemblyHost host, AssemblyComparer.MethodSide side, IMethod copy,
        IMethod alongside = null)
    {
        IEnumerable<IMethod> namesakes = side.Namesakes ?? host.Resolver.Namesakes(side.Type, side.Method).OfType<IMethod>();
        string Format(IMethod method, bool full) => ReferenceFormatter.FormatResolvedSignature(method, side.Type, full);
        var name = alongside is not null && ReferenceFormatter.ShortFormHidesNamesakes(
                ReferenceFormatter.FormatSignatureTypes(side.Method, full: true),
                ReferenceFormatter.FormatSignatureTypes(alongside, full: true))
            ? Format(side.Method, full: true)
            : ReferenceFormatter.NameAmong(side.Method, alongside is null ? namesakes : namesakes.Append(alongside), Format);
        return name + (side.Origin.Kind == "declared" ? "" : " " + side.Origin.Format()) + Via(copy);
    }

    private static string Via(IMethod copy) => copy is null ? "" : $" (via {copy.Name})";

    /// <summary>
    /// The line that says what a build's lookup could not read, where its method is an extension
    /// method it found through the type: a base class of the type
    /// (<see cref="SymbolResolver.ExtensionNotChecked"/>), or for a type of another assembly the
    /// line its side carries.
    /// </summary>
    private static string NotChecked(AssemblyHost host, AssemblyComparer.MethodSide side) =>
        NotChecked(host, side.Type, side.Method, side.NotChecked);

    /// <inheritdoc cref="NotChecked(AssemblyHost, AssemblyComparer.MethodSide)"/>
    private static string NotChecked(AssemblyHost host, ITypeDefinition type, IMember member, string carried) =>
        carried ?? (member is IMethod method && type is not null ? host.Resolver.ExtensionNotChecked(type, method) : null);

    /// <summary>
    /// The lines an answer starts with that say what a lookup could not read, each line once, from
    /// those of <paramref name="lines"/> that are not null.
    /// </summary>
    private static string NotCheckedLines(params string[] lines) =>
        string.Concat(lines.OfType<string>().Distinct().Select(line => $"// {line}\n"));

    /// <summary>
    /// The answer for a constant asked for by name, one line with its values: in both assemblies,
    /// or the value of the one that has it, followed by <paramref name="elsewhere"/>. Where the name
    /// finds two constants that the member list counts as a removal and an addition, the line names
    /// both with their types, as two methods are named with their return types.
    /// </summary>
    private static string CompareValues(AssemblyComparer.ConstantSide a, AssemblyComparer.ConstantSide b,
        string elsewhere)
    {
        string Value(AssemblyComparer.ConstantSide side) => ConstantFormatter.ValueOf(side.Constant);

        if (a.Constant is null || b.Constant is null)
        {
            var (side, found) = a.Constant is null ? ("B", b) : ("A", a);
            return $"// {Reference(found, withType: false)} = {Value(found)} — only {side} has it{elsewhere}.";
        }
        if (!AssemblyComparer.AreOneMember(a.Constant, b.Constant))
        {
            // Two constants whose types read alike, or whose types are two of one short name, are
            // named with their types in full, and their values with them, since an enum's value
            // names its type. The test reads the types alone, since the line also writes each
            // constant under its class, as a name it holds in either form.
            string Type(AssemblyComparer.ConstantSide side) => ReferenceFormatter.FormatTypeRef(side.Constant.Type, full: true);
            var (aName, bName) = ReferenceFormatter.Sides(a, b,
                side => Reference(side, withType: true), side => Reference(side, withType: true, full: true), Type);
            var (aValue, bValue) = ReferenceFormatter.Sides(a, b,
                Value, side => ConstantFormatter.ValueOf(side.Constant, full: true), Type);
            return $"// {aName} in A and {bName} in B are different constants — {aValue} in A, {bValue} in B.";
        }
        return DeclarationsLine(a.Constant, b.Constant) +
            (!AssemblyComparer.ValuesDiffer(a.Constant, b.Constant)
                ? $"// {Reference(a, withType: false)} — {Value(a)} in both assemblies."
                : $"// {Reference(a, withType: false)} — {Value(a)} in A, {Value(b)} in B.");
    }

    /// <summary>
    /// A constant as the answer names it: as decompile's header names a field, under the type asked
    /// about and with where it was found when that type does not declare it.
    /// </summary>
    private static string Reference(AssemblyComparer.ConstantSide side, bool withType, bool full = false) =>
        $"{ReferenceFormatter.FormatTypeName(side.Type)}.{side.Constant.Name}" +
        (withType ? " → " + ReferenceFormatter.FormatTypeRef(side.Constant.Type, full) : "") +
        (side.Origin.Kind == "declared" ? "" : " " + side.Origin.Format());

    /// <summary>
    /// A property or event as the answer names it: as decompile's header names it, under the type
    /// asked about and with where it was found when that type does not declare it.
    /// </summary>
    private static string Reference(ITypeDefinition type, IMember member, SymbolResolver.MemberOrigin origin) =>
        $"{ReferenceFormatter.FormatTypeName(type)}.{member.Name}" +
        (member is IProperty property ? ReferenceFormatter.FormatIndices(property) : "") +
        (origin.Kind == "declared" ? "" : " " + origin.Format());

    /// <summary>
    /// The code of a property or event: in C# as decompile shows it, the declaration with its
    /// accessors, or as IL each accessor as the comparison reads it, under a line that names it;
    /// where <paramref name="build"/> is a reference assembly, its declaration in place of the
    /// stubs (<see cref="WithoutCode"/>).
    /// </summary>
    private static string MemberCode(AssemblyHost host, string build, ITypeDefinition type, IMember member, CompareFormat format)
    {
        if (host.IsReferenceAssembly)
            return WithoutCode(build, member);
        if (format != CompareFormat.Il)
            return member is IProperty property ? host.DecompileProperty(property) : host.DecompileEvent((IEvent)member);
        IMethod[] accessors = member is IProperty p
            ? [p.Getter, p.Setter]
            : [((IEvent)member).AddAccessor, ((IEvent)member).RemoveAccessor, ((IEvent)member).InvokeAccessor];
        var typeName = ReferenceFormatter.FormatTypeName(type);
        return string.Join("\n", accessors.Where(accessor => accessor is not null).Select(accessor =>
            $"// {ReferenceFormatter.FormatResolvedSignature(accessor, typeName)}\n{AssemblyComparer.CodeOf(host, accessor)}"));
    }

    /// <summary>
    /// The error for a member of A that has no body of its own. A property or event is compared
    /// through its accessors, so the error names every accessor that it or <paramref name="inB"/>,
    /// B's member of the name and kind, has, each that only one build has marked, and both
    /// declarations where they read otherwise or are two members, as <see cref="Naming"/> writes
    /// them.
    /// </summary>
    private static string NotAMethod(AssemblyComparer.MemberSide inA, AssemblyComparer.MemberSide inB, string memberName)
    {
        if (inA.Member is not (IProperty or IEvent))
            return $"{ReferenceFormatter.FormatSymbol(inA.Member)} has no body.";
        var (name, clause) = inB is null
            ? (ReferenceFormatter.FormatSymbol(inA.Member), "")
            : Naming(inA, inB, memberName, inA.Member is IProperty ? "properties" : "events");
        return $"{name} is {(inA.Member is IProperty ? "a property" : "an event")}{clause}; " +
            $"compare its accessors, {Accessors(inA.Member, inB?.Member ?? inA.Member)}.";

        // The accessors in the order of their roles, a getter before a setter and add before
        // remove before raise. Where one is marked, "and" stands before the last.
        static string Accessors(IMember a, IMember b)
        {
            var named = AccessorsOf(a).Zip(AccessorsOf(b))
                .Where(pair => pair.First is not null || pair.Second is not null)
                .Select(pair => (Name: (pair.First ?? pair.Second).Name,
                    Mark: pair.First is null ? ", which only B has" : pair.Second is null ? ", which only A has" : ""))
                .ToList();
            var names = named.Select(accessor => accessor.Name + accessor.Mark).ToList();
            return named.Any(accessor => accessor.Mark.Length > 0) && names.Count > 1
                ? $"{string.Join(", ", names.SkipLast(1))}, and {names[^1]}"
                : string.Join(", ", names);
        }

        static IMethod[] AccessorsOf(IMember member) => member switch
        {
            IProperty property => [property.Getter, property.Setter],
            IEvent @event => [@event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor],
            _ => [],
        };
    }

    /// <summary>
    /// The member a name finds in B where it is of the kind of <paramref name="inA"/>, A's member of
    /// the name, with where it was found; null where B lacks the type, or the name finds none there,
    /// several, one of another kind, or one of a class another assembly defines.
    /// </summary>
    private static AssemblyComparer.MemberSide SameKindIn(AssemblyHost host, ITypeDefinition type, ISymbol inA,
        string memberName) =>
        type is not null
        && host.Resolver.FindNearestMembers(type, memberName, explainAbsence: false) is [(IMember member, var origin, _)]
        && !host.Resolver.IsExternal(member)
        && inA is IMember a && AssemblyComparer.KindOf(member) == AssemblyComparer.KindOf(a)
            ? new AssemblyComparer.MemberSide(type, member, origin)
            : null;

    /// <summary>
    /// How the error for a field, property or event that both builds have names it, and the clause
    /// after its kind that names both declarations, as the member list writes them. For one member,
    /// the member as decompile's header names it, and both declarations where they read otherwise.
    /// For two members that the member list counts as a removal and an addition, as after a changed
    /// type or a move to a base class, the name under the type asked about, and both declarations,
    /// each followed by where its build found it when that is not the type asked about.
    /// </summary>
    private static (string Name, string Clause) Naming(AssemblyComparer.MemberSide a, AssemblyComparer.MemberSide b,
        string memberName, string plural)
    {
        if (AssemblyComparer.AreOneMember(a.Member, b.Member))
            return (ReferenceFormatter.FormatSymbol(a.Member), AssemblyComparer.DeclarationsDiffer(a.Member, b.Member)
                ? $", and its declaration changed: {AssemblyComparer.DeclarationChange(a.Member, b.Member)}"
                : "");
        // Two declarations that read alike, as after a type gave way to one of the same name in
        // another namespace, are written in full, and so are two in which a name stands for two
        // different types, as where that change comes with another.
        var (aDeclaration, bDeclaration) = ReferenceFormatter.Sides(a, b,
            side => Declaration(side, full: false), side => Declaration(side, full: true),
            side => AssemblyComparer.TypesNamed(side.Member));
        return ($"{ReferenceFormatter.FormatTypeName(a.Type)}.{memberName}",
            $", and A and B have different {plural} of that name: {aDeclaration} → {bDeclaration}");

        static string Declaration(AssemblyComparer.MemberSide side, bool full) =>
            SignatureFormatter.FormatMember(side.Member, full) +
            (side.Origin.Kind == "declared" ? "" : " " + side.Origin.Format());
    }

    /// <summary>
    /// The constructors of a field's class that set it in one build, in the reference form: those
    /// that certainly do, and those that may, since what they do with the field's address is not
    /// known, as when they pass it to a method of another assembly. A constant is set by none,
    /// since its value is in its declaration, and in a reference assembly no constructor holds code
    /// that says.
    /// </summary>
    private sealed record FieldSetters(
        string DeclaringType, IReadOnlyList<string> Certain, IReadOnlyList<string> Possible, bool IsConstant = false,
        bool InReferenceAssembly = false);

    /// <summary>
    /// The field a name finds on <paramref name="type"/> in the other build, as decompile looks one
    /// up, with where it was found; null where that build lacks the type or the name finds no field
    /// there. One that a class of another assembly declares ends in the error that names that
    /// assembly, as a method does, since this build holds none of its code.
    /// </summary>
    private static AssemblyComparer.MemberSide FieldIn(AssemblyHost host, ITypeDefinition type, string memberName)
    {
        if (type is null || host.Resolver.FindNearestMembers(type, memberName) is not [(IField field, var origin, _)])
            return null;
        // The lookup refuses such a field with the error that names its assembly.
        if (host.Resolver.IsExternal(field))
            host.Resolver.ResolveMember(type, memberName);
        return new AssemblyComparer.MemberSide(type, field, origin);
    }

    /// <summary>
    /// The constructors of <paramref name="field"/>'s class that set it in one build, read from
    /// their IL as <c>assigned_by</c> reads a field's writers; null where the build has no such
    /// field.
    /// </summary>
    /// <remarks>
    /// The compiler puts a static field's initializer into the static constructor, and an instance
    /// field's into each instance constructor that does not call another of its class, so the
    /// constructors that store a field are where it gets the value it starts with.
    /// </remarks>
    private static FieldSetters SettersOf(AssemblyHost host, IField field)
    {
        if (field is null)
            return null;
        if (field.IsConst)
            return new FieldSetters(ReferenceFormatter.FormatTypeName(field.DeclaringType), [], [], IsConstant: true);
        if (host.IsReferenceAssembly)
            return new FieldSetters(ReferenceFormatter.FormatTypeName(field.DeclaringType), [], [], InReferenceAssembly: true);
        var constructors = field.DeclaringTypeDefinition.DeclaredMethods()
            .Where(method => method.Name is ".ctor" or ".cctor");
        var setters = host.FindFieldAccesses(field, FieldAccessScanner.AccessKind.Write, constructors);
        var name = ReferenceFormatter.NamesFor(setters.Select(setter => setter.Symbol));
        List<string> Named(bool certain) => setters
            .Where(setter => (setter.Uncertainty is null) == certain)
            .Select(setter => name(setter.Symbol))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new FieldSetters(ReferenceFormatter.FormatTypeName(field.DeclaringType), Named(true), Named(false));
    }

    /// <summary>
    /// The error for a field asked for by name, under <paramref name="name"/>: a field has no body,
    /// and the error names the constructors that set it, in each build that has it, as
    /// <see cref="SettersOf"/> finds them. Where they differ between the builds, it names them for
    /// each, a build where the field is a constant says so instead, and a build that is a reference
    /// assembly says that it holds no code. Where the field's
    /// declaration changed or the builds have two fields of the name, <paramref name="clause"/>
    /// names both declarations, as <see cref="Naming"/> writes them. Where only one build has the
    /// field, <paramref name="elsewhere"/> follows the clause that says so.
    /// </summary>
    private static string FieldHasNoBody(string name, string clause, FieldSetters a, FieldSetters b, string elsewhere)
    {
        if (a is null || b is null)
        {
            var (side, setters) = a is null ? ("B", b) : ("A", a);
            return $"{name} is a field, which has no body, and only {side} has it{elsewhere}; {Setting(side, setters)}.";
        }
        var subject = $"{name} is a field, which has no body{clause}";
        if (a.InReferenceAssembly && b.InReferenceAssembly)
            return $"{subject}; {HoldsNoCode(true, true)}.";
        var (inA, inB) = (Setting("A", a), Setting("B", b));
        return inA == inB
            ? $"{subject}; {inA}."
            : $"{subject}; {Part("A", a)}, and {Part("B", b)}.";

        // Where no constructor sets the field, its value comes from code elsewhere or is the
        // default, and assigned_by lists the code that sets it. A constant's value is in its
        // declaration, and assigned_by refuses a constant, so a field that is a constant in one
        // build is named that way there, which also keeps the two builds' clauses apart. A
        // reference assembly holds no code that could say, and a question on its code would be
        // answered from stubs, so the build is named as one.
        static string Setting(string build, FieldSetters setters) =>
            setters.InReferenceAssembly ? HoldsNoCode(build)
            : Clause(setters) +
                (!setters.IsConstant && setters.Certain.Count + setters.Possible.Count == 0
                    ? ", and assigned_by lists what does"
                    : "");

        static string Part(string build, FieldSetters setters) =>
            setters.InReferenceAssembly ? HoldsNoCode(build) : $"in {build} {Clause(setters)}";

        static string Clause(FieldSetters setters) => (setters.Certain.Count, setters.Possible.Count) switch
        {
            _ when setters.IsConstant => "it is a constant",
            (0, 0) => $"no constructor of {setters.DeclaringType} sets it",
            (_, 0) => $"{Names(setters.Certain)} {Verb(setters.Certain)} it",
            (0, _) => $"{Names(setters.Possible)} may set it",
            _ => $"{Names(setters.Certain)} {Verb(setters.Certain)} it, and {Names(setters.Possible)} may set it",
        };

        static string Verb(IReadOnlyList<string> names) => names.Count == 1 ? "sets" : "set";

        static string Names(IReadOnlyList<string> names) =>
            names.Count == 1 ? names[0] : $"{string.Join(", ", names.SkipLast(1))} and {names[^1]}";
    }
}
