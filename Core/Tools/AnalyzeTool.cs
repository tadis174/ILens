using System.ComponentModel;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class AnalyzeTool
{
    [McpServerTool(Name = "analyze", ReadOnly = true),
     Description("Run cross-reference analysis on a type or member. " +
        "Omit member_name to analyze the type itself.\n" +
        "Valid kinds by symbol category:\n" +
        "  Type:     used_by, instantiated_by, exposed_by, extension_methods, applied_to, implemented_by\n" +
        "  Method:   used_by, call_sites, overridden_by, implemented_by, uses, implements\n" +
        "  Property: used_by, read_by, assigned_by, uses, overridden_by, implemented_by, implements\n" +
        "  Field:    read_by, assigned_by\n" +
        "  Event:    used_by, overridden_by, implemented_by, implements\n" +
        "call_sites lists every call to a method, beneath the member that makes it, with what it passes, and covers all " +
        "overloads unless a hint selects one. Arguments are matched to parameters as the compiler " +
        "bound them, so named, reordered and omitted arguments are resolved, and a call the IL holds " +
        "but the decompiled C# does not show is reported, not dropped.\n" +
        "uses lists members and the types code names outright, not those of locals or signatures; " +
        "used_by on a type is the full test.\n" +
        "used_by / read_by / assigned_by / uses on a property route to its accessors — read_by to " +
        "the getter, assigned_by to the setter, used_by to both, and uses to both for the " +
        "outgoing direction (what the accessor bodies call). used_by on an event covers its add, " +
        "remove, and invoke accessors plus, for a field-like event, the field holding the " +
        "subscriber list — so raise sites are reported and not just subscribers. The result " +
        "header names what was consulted. read_by on a write-only property, or assigned_by on a " +
        "read-only one, errors rather than returning an empty result.\n" +
        "applied_to requires an Attribute-derived type — it errors on a non-attribute type. " +
        "implemented_by requires an interface or a member of one — it errors otherwise.\n" +
        "A type defined in another assembly is answered from this assembly's references to it: " +
        "every Type kind, and used_by / read_by / assigned_by on a member, named on the type that " +
        "declares it. applied_to then cannot check for an attribute, and implemented_by errors only " +
        "when this assembly derives a class from the type.")]
    public static string Analyze(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(ParameterDescriptions.TypeName)] string type_name,
        [Description("Analysis kind. See tool description for which kinds apply to which symbol category.")] AnalysisKind kind,
        [Description(ParameterDescriptions.MemberName)] string member_name = null,
        [Description(ParameterDescriptions.ParameterCount)] int? parameter_count = null,
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null,
        [Description(ParameterDescriptions.ReturnType)] string return_type = null,
        [Description("With kind call_sites, show only the argument passed for this parameter; overloads without it are left out.")] string parameter_name = null,
        [Description("With parameter_name, keep only the calls whose argument is not provably the parameter's default.")] bool non_default_only = false,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        var hint = new SymbolResolver.MethodHint(parameter_count, TypeMatcher.SplitPatterns(parameter_types), return_type);
        // A name with the '@' C# writes before a keyword, as a declaration line writes it, means the keyword.
        member_name = SymbolResolver.ReadName(member_name);
        parameter_name = SymbolResolver.ReadName(parameter_name);

        // A parameter that does not apply would otherwise be dropped silently, and the answer to
        // a broader question returned as if it were the one asked.
        if (kind != AnalysisKind.CallSites && (!string.IsNullOrEmpty(parameter_name) || non_default_only))
            throw new ArgumentException(
                $"{nameof(parameter_name)} and {nameof(non_default_only)} apply only to kind " +
                $"'{AnalysisKind.CallSites.ToSchemaName()}'.");
        if (string.IsNullOrEmpty(member_name) && hint.IsGiven)
            throw new ArgumentException(
                $"{nameof(parameter_count)}, {nameof(parameter_types)} and {nameof(return_type)} select a " +
                $"method overload, so they need {nameof(member_name)}.");

        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;

        // A reference assembly holds no code, so no name of it can be answered by a kind that
        // reads code, and the check comes before any name is resolved.
        if (host.IsReferenceAssembly && AnalysisDispatch.ReadsCode(kind))
        {
            var others = AnalysisDispatch.ReadingDeclarations().Select(other => other.ToSchemaName()).ToList();
            throw AssemblyHost.ReferenceAssemblyError($"'{kind.ToSchemaName()}' cannot be answered from it",
                $"ask the build the library runs as. {string.Join(", ", others[..^1])} and {others[^1]} " +
                "read declarations alone and answer here.");
        }

        var resolver = host.Resolver;

        // call_sites looks for a method by name, through a type of this assembly as through any
        // other, so a property, field or event of the name does not stand before an extension
        // method there.
        var methodsOnly = kind == AnalysisKind.CallSites;

        // An array has no members of its own in an assembly. The question names one for the
        // extension methods code can call on it: their list, or one of them by name, where a
        // member of System.Array or System.Object that comes first is the question asked through
        // the class that declares it.
        if (resolver.ReadArray(type_name) is { } array)
        {
            if (string.IsNullOrEmpty(member_name))
            {
                if (kind != AnalysisKind.ExtensionMethods)
                    throw SymbolResolver.ArrayNotTaken(type_name);
                var (methods, notCheckedMethods) = resolver.ArrayExtensionMethods(array);
                return FormatAnswer(kind, array.Display, methods, include_compiler_generated, limit) +
                    (notCheckedMethods is null ? "" : "\n" + notCheckedMethods);
            }
            var found = resolver.FindExtensionThroughArray(array, member_name, hint, methodsOnly);
            if (found.Extension is { } throughArray)
                return AnalyzeExtension(host, array.Display, throughArray, kind, parameter_name, non_default_only,
                    include_compiler_generated, limit);
            if (found.MemberOwner is { } owner)
                return Analyze(registry, assembly, owner, kind, member_name, parameter_count, parameter_types,
                    return_type, parameter_name, non_default_only, include_compiler_generated, limit);
            throw resolver.ArrayMemberNotFound(array, member_name, hint);
        }

        var (type, external) = resolver.ResolveTypeOrExternal(type_name);
        if (external != null)
        {
            // An extension method this assembly declares for a type of another assembly holds its
            // code here, so it is answered as through its own class; any other member of such a
            // type is known from this assembly's references to it.
            if (!string.IsNullOrEmpty(member_name)
                && resolver.FindExtensionThrough(external, member_name, hint, methodsOnly) is { } extension)
                return AnalyzeExtension(host, external.Display, extension, kind, parameter_name, non_default_only,
                    include_compiler_generated, limit);
            return AnalyzeExternal(host, external, kind, member_name, hint, include_compiler_generated, limit);
        }

        // call_sites covers every overload of the name unless a hint selects one, so it resolves
        // the name itself rather than through the single-member resolution below.
        if (kind == AnalysisKind.CallSites)
        {
            if (string.IsNullOrEmpty(member_name))
                ValidateKind(kind, SymbolCategory.Type);
            return CallSiteReport.Format(host, type, member_name, hint, parameter_name, non_default_only, limit);
        }

        // Default to type-level analysis; member resolution overrides if member_name is set.
        // IsNullOrEmpty (not just `is null`) so an LLM passing "" for "omit" works as expected.
        ISymbol symbol = type;
        var category = SymbolCategory.Type;
        var originSuffix = "";
        if (!string.IsNullOrEmpty(member_name))
        {
            var (resolved, origin, resolvedCategory) = resolver.ResolveMember(type, member_name, hint);
            symbol = resolved;
            category = resolvedCategory;
            originSuffix = " " + origin.Format();
        }

        // An extension method found while a base class of the type was not read may stand where
        // a member of that class would answer, which the last line says.
        var notChecked = symbol is IMethod foundMethod && resolver.ExtensionNotChecked(type, foundMethod) is { } line
            ? "\n" + line
            : "";
        return Answer(host, type, symbol, category, originSuffix, member => resolver.Namesakes(type, member),
            kind, include_compiler_generated, limit) + notChecked;
    }

    /// <summary>
    /// <see cref="Analyze"/> for an extension method found through a type of another assembly or
    /// an array type, <paramref name="typeDisplay"/> (<see cref="SymbolResolver.FindExtensionThrough"/>,
    /// <see cref="SymbolResolver.FindExtensionThroughArray"/>): every kind as for the method asked
    /// for through its own class, named as an extension method found through a type of this
    /// assembly is, with the line that says what the lookup could not read at the end.
    /// </summary>
    private static string AnalyzeExtension(AssemblyHost host, string typeDisplay,
        SymbolResolver.ExtensionThrough extension, AnalysisKind kind, string parameter_name, bool non_default_only,
        bool include_compiler_generated, int limit)
    {
        var notChecked = extension.NotChecked is null ? "" : "\n" + extension.NotChecked;
        if (kind == AnalysisKind.CallSites)
            return CallSiteReport.Format(host, [extension.Method], typeDisplay, _ => extension.Namesakes,
                parameter_name, non_default_only, limit) + notChecked;
        return Answer(host, type: null, extension.Method, SymbolCategory.Method, " " + extension.Origin.Format(),
            _ => extension.Namesakes, kind, include_compiler_generated, limit) + notChecked;
    }

    /// <summary>
    /// The answer to <paramref name="kind"/> about <paramref name="symbol"/>, which the question
    /// resolved to: <paramref name="type"/> itself, or a member found on it with where it was
    /// found, <paramref name="originSuffix"/>. <paramref name="namesakes"/> gives the members a
    /// member's name in the header is told apart from. <paramref name="type"/> serves the
    /// questions about a type alone, so a member found without one passes null.
    /// </summary>
    private static string Answer(AssemblyHost host, ITypeDefinition type, ISymbol symbol, SymbolCategory category,
        string originSuffix, Func<IMember, IEnumerable<IMember>> namesakes, AnalysisKind kind,
        bool include_compiler_generated, int limit)
    {
        ValidateKind(kind, category);

        // A symbol the compiler generated was asked for by name, so what the compiler generated
        // around it belongs to the answer: who instantiates a lambda's class is that class's
        // own static constructor, and leaving it out would read as "nothing does".
        include_compiler_generated |= IsCompilerGenerated(symbol);

        // AppliedTo asks "what is this attribute applied to?" — only meaningful for an
        // Attribute-derived type. ValidateKind checks only the symbol category (Type),
        // not attribute-ness; without this check a non-attribute type would yield an
        // empty result indistinguishable from "applied nowhere". This guard is specific
        // to AppliedTo: its name presupposes the input kind and the precondition is a
        // crisp base-type check, unlike the murkier preconditions of the other kinds.
        // A base class ILens cannot load leaves the check open; the question is then
        // answered, and an empty answer says below that the type was not checked.
        UnreadBase uncheckedBase = null;
        if (kind == AnalysisKind.AppliedTo)
        {
            var (derives, unread) = host.DerivesFromAttribute(type);
            if (!derives && unread is null)
                throw new ArgumentException(
                    $"Analysis kind 'applied_to' requires an attribute type; " +
                    $"{ReferenceFormatter.FormatTypeName(type)} does not derive from System.Attribute.");
            uncheckedBase = unread;
        }

        // ImplementedBy on a type only makes sense for an interface. The same empty-result
        // ambiguity as AppliedTo applies — "implementers of System.String" would silently
        // return nothing — and the precondition is again a crisp kind check.
        if (kind == AnalysisKind.ImplementedBy && category == SymbolCategory.Type
            && type.Kind != TypeKind.Interface)
            throw new ArgumentException(
                $"Analysis kind 'implemented_by' on a type requires an interface; " +
                $"{ReferenceFormatter.FormatTypeName(type)} has kind {type.Kind}.");

        // The same for a member: only a member of an interface is implemented. Asked of a
        // class's virtual member, "No implementations" would read as "nothing fills it in".
        if (kind == AnalysisKind.ImplementedBy
            && symbol is IMember { DeclaringTypeDefinition: { Kind: not TypeKind.Interface } declaredBy } notOfAnInterface)
            throw new ArgumentException(
                $"Analysis kind 'implemented_by' on a member requires a member of an interface; " +
                $"{ReferenceFormatter.FormatSymbol(notOfAnInterface)} is declared by the " +
                $"{declaredBy.Kind.ToString().ToLowerInvariant()} {ReferenceFormatter.FormatTypeName(declaredBy)}." +
                (notOfAnInterface.IsVirtual || notOfAnInterface.IsAbstract || Overriding.Of(notOfAnInterface)
                    ? $" '{AnalysisKind.OverriddenBy.ToSchemaName()}' lists its overrides."
                    : ""));

        if (symbol is IField field && AccessKindOf(kind) is { } accessKind)
        {
            if (field.IsConst)
                throw ConstantError(kind, field, declaredIn: null);
            var accesses = host.FindFieldAccesses(field, accessKind);
            string notCheckedSets = null;
            // An attribute that sets the field by name writes it, and no method body shows it.
            if (accessKind == FieldAccessScanner.AccessKind.Write)
            {
                var (carriers, undecidedSets) = host.FindNamedArgumentCarriers(field);
                accesses = [.. accesses, .. carriers.Select(carrier => new FieldAccessScanner.Access(carrier, null))];
                notCheckedSets = NotCheckedAttributes(host, undecidedSets,
                    () => host.AttributeToSource(accesses.Select(access => access.Symbol).ToList()), SetsIt);
            }
            return FormatAccessAnswer(host, kind, $"{ReferenceFormatter.FormatSymbol(field)}{originSuffix}",
                accesses, include_compiler_generated, limit) +
                (notCheckedSets is null ? "" : "\n" + notCheckedSets);
        }

        var route = RouteFor(symbol, kind);

        IReadOnlyList<ISymbol> results;
        // What an interface question found, each with the classes that bind it; null for any
        // other kind.
        IReadOnlyList<InterfaceImplementations.Found> bindings = null;
        var viaSuffix = "";
        string notChecked = null;
        if (route != null)
        {
            results = route.Run(host, out var undecidedSets);
            notChecked = NotCheckedAttributes(host, undecidedSets, () => results, SetsIt);
            viaSuffix = $" (via {string.Join(", ", route.Via)})";
        }
        // Which types implement an interface: AssemblyHost.FindImplementingTypes walks the
        // main module, since ILSpyX ships no analyzer for the question.
        else if (kind == AnalysisKind.ImplementedBy && category == SymbolCategory.Type)
        {
            results = host.FindImplementingTypes(type);
        }
        // Both interface questions are answered from the decompiler's type system, which knows
        // the interfaces and the base classes of other assemblies. A base type it could not
        // read may declare more, which a list does not show, so the answer says so whatever
        // it found.
        else if (kind is AnalysisKind.Implements or AnalysisKind.ImplementedBy)
        {
            (bindings, var unread) = kind == AnalysisKind.Implements
                ? host.FindImplementedInterfaceMembers((IMember)symbol)
                : host.FindImplementations((IMember)symbol);
            results = bindings.Select(found => (ISymbol)found.Member).ToList();
            if (unread.Count > 0)
                notChecked =
                    $"Not checked: what {string.Join(", ", unread.Select(u => u.TypeName))} and " +
                    $"{(unread.Count == 1 ? "its" : "their")} base types declare; " +
                    UnreadLibrary.Reason(unread.Select(u => (u.TypeName, u.Libraries)));
        }
        else
        {
            results = Ask(host, kind, symbol, out var undecided);
            notChecked = NotCheckedAttributes(host, undecided, () => results, symbol is ITypeDefinition ? NamesIt : SetsIt);
        }

        // A method whose declaration was not read is listed with ref for every parameter it
        // takes by reference, whatever keyword it declares, and nothing in its line shows that.
        if (kind == AnalysisKind.Uses)
            notChecked = NotCheckedKeywords(host, results, include_compiler_generated);

        // The header names the member asked about in full where another member of its name would
        // read alike, since it is all that shows which member the answer is about.
        var display = symbol is IMember asked
            ? ReferenceFormatter.NameAmong(asked, namesakes(asked), (m, full) => ReferenceFormatter.FormatSymbol(m, full))
            : ReferenceFormatter.FormatSymbol(symbol);
        IReadOnlyList<string> reachedThrough = AnalysisDispatch.AsksWhoUses(kind) && symbol is IMember member
            ? UsageRoutes.For(host, member, listsCalls: false)
            : [];

        // Anything marked with the type shows it to be an attribute, so only an answer with
        // no result at all, before the generated ones are left out, is in doubt.
        if (uncheckedBase is not null && results.Count == 0)
            notChecked =
                $"Not checked: whether {display} is an attribute; its base class {uncheckedBase.TypeName} " +
                $"was not read, since {uncheckedBase.Reason}";

        var subject = $"{display}{originSuffix}{viaSuffix}";
        return (bindings is null
                ? FormatAnswer(kind, subject, results, include_compiler_generated, limit)
                : FormatBindings(kind, subject, bindings, include_compiler_generated, limit)) +
            string.Concat(reachedThrough.Select(line => "\n" + line)) +
            (notChecked is null ? "" : "\n" + notChecked);
    }

    /// <summary>
    /// The answer to <paramref name="kind"/> about <paramref name="subject"/>: a header counting
    /// the results in the kind's own words ("12 users of X.Y():"), then one result per line,
    /// sorted. Results that print alike are one line, and the count is of lines; results that
    /// would print alike and are different members are written in full
    /// (<see cref="ReferenceFormatter.NamesFor"/>). What the
    /// compiler generated is left out unless <paramref name="includeCompilerGenerated"/>, and
    /// an answer left empty by that alone has the line from <see cref="GeneratedNotShown"/>
    /// under its header.
    /// </summary>
    private static string FormatAnswer(AnalysisKind kind, string subject, IReadOnlyList<ISymbol> results,
        bool includeCompilerGenerated, int limit)
    {
        var lines = Lines(includeCompilerGenerated);
        return ListOutput.Format(lines, limit, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject),
            lines.Count == 0 ? GeneratedNotShown(Lines(withGenerated: true).Count) : null);

        List<string> Lines(bool withGenerated)
        {
            var shown = results.Where(result => withGenerated || !IsCompilerGenerated(result)).ToList();
            var name = ReferenceFormatter.NamesFor(shown);
            return shown.Select(name).Distinct().Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>How many of the classes that bind a member its line names before it counts the rest.</summary>
    private const int BindersNamed = 3;

    /// <summary>
    /// <see cref="FormatAnswer"/> for the two interface questions. A member that only classes
    /// below its own bind ends with those classes, as in
    /// "Shop.Counter.Open() (for Shop.Kiosk, Shop.Stall, Shop.Stand and 2 more)": it
    /// implements the interface member for their objects alone, and without them the line
    /// would read as if its own class had the interface. A member several results print
    /// alike for is one line, without classes when one of them is bound by its own class.
    /// </summary>
    private static string FormatBindings(AnalysisKind kind, string subject,
        IReadOnlyList<InterfaceImplementations.Found> bindings, bool includeCompilerGenerated, int limit)
    {
        var lines = Lines(includeCompilerGenerated);
        return ListOutput.Format(lines, limit, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject),
            lines.Count == 0 ? GeneratedNotShown(Lines(withGenerated: true).Count) : null);

        List<string> Lines(bool withGenerated)
        {
            var shown = bindings.Where(found => withGenerated || !IsCompilerGenerated(found.Member)).ToList();
            var name = ReferenceFormatter.NamesFor(shown.Select(found => found.Member));
            return shown
                .GroupBy(found => name(found.Member))
                .OrderBy(member => member.Key, StringComparer.Ordinal)
                .Select(member => member.Key + BoundBy(member))
                .ToList();
        }

        static string BoundBy(IEnumerable<InterfaceImplementations.Found> member)
        {
            if (member.Any(found => found.BoundBy.Count == 0))
                return "";
            var binders = member.SelectMany(found => found.BoundBy)
                .Select(ReferenceFormatter.FormatTypeName)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
            var more = binders.Count - BindersNamed;
            return $" (for {string.Join(", ", binders.Take(BindersNamed))}{(more > 0 ? $" and {more} more" : "")})";
        }
    }

    /// <summary>
    /// The line under the header of an empty answer when the call with
    /// include_compiler_generated lists <paramref name="generated"/> lines, or null when it
    /// lists none either. Without it "No implementations" reads as "there is none" where an
    /// async method's state machine is one. An answer with results gets no such line: what it leaves out are mostly the fields
    /// and helper methods of closure and state machine classes whose source member it lists,
    /// and the line would stand under about every fifth answer to who uses a type (measured
    /// 2026-10-11 on 2,418 types of five assemblies).
    /// </summary>
    private static string GeneratedNotShown(int generated) =>
        ListOutput.NotShown(new LeftOut(inaccessible: 0, generated, generatedNeedAll: false),
            AccessibilityFilter.All, empty: true);

    /// <summary>
    /// <see cref="FormatAnswer"/> for the methods that read or write a field. A member not known
    /// to read, or to write, it ends with what received the field's address, as in
    /// "UI.Panel.Draw(int) (may write: calls UnityEngine.Rect.get_width() on it)".
    /// A member several results report under is known to when any of them is.
    /// </summary>
    private static string FormatAccessAnswer(AssemblyHost host, AnalysisKind kind, string subject,
        IReadOnlyList<FieldAccessScanner.Access> accesses, bool includeCompilerGenerated, int limit)
    {
        var sources = host.AttributeToSource(accesses.Select(access => access.Symbol).ToList());
        var verb = kind == AnalysisKind.ReadBy ? "read" : "write";
        var lines = Lines(includeCompilerGenerated);
        return ListOutput.Format(lines, limit, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject),
            lines.Count == 0 ? GeneratedNotShown(Lines(withGenerated: true).Count) : null);

        List<string> Lines(bool withGenerated)
        {
            var shown = sources
                .Zip(accesses, (source, access) => (Source: source, access.Uncertainty))
                .Where(result => withGenerated || !IsCompilerGenerated(result.Source))
                .ToList();
            var name = ReferenceFormatter.NamesFor(shown.Select(result => result.Source));
            return shown
                .GroupBy(result => name(result.Source), result => result.Uncertainty)
                .OrderBy(member => member.Key, StringComparer.Ordinal)
                .Select(member => FieldAccessScanner.Uncertainty.Merge(member) is { } uncertainty
                    ? $"{member.Key} (may {verb}: {uncertainty})"
                    : member.Key)
                .ToList();
        }
    }

    /// <summary>Whether a kind asks who reads or who writes, or null for any other kind.</summary>
    private static FieldAccessScanner.AccessKind? AccessKindOf(AnalysisKind kind) => kind switch
    {
        AnalysisKind.ReadBy => FieldAccessScanner.AccessKind.Read,
        AnalysisKind.AssignedBy => FieldAccessScanner.AccessKind.Write,
        _ => null,
    };

    /// <summary>
    /// True for a symbol the compiler generated, by the rules of <see cref="CompilerGeneratedFilter"/>.
    /// A type only referenced from another assembly cannot be told and counts as written in source.
    /// What only the compiler's code in an async method uses counts as generated, whatever the
    /// symbol is.
    /// </summary>
    private static bool IsCompilerGenerated(ISymbol symbol) => symbol switch
    {
        IMember member => CompilerGeneratedFilter.IsCompilerGenerated(member),
        ITypeDefinition type => CompilerGeneratedFilter.IsCompilerGenerated(type),
        MethodUseScanner.CompilerUse => true,
        _ => false,
    };

    /// <summary>
    /// The empty answer to <paramref name="kind"/> about <paramref name="subject"/> ("No users
    /// of X.Y()"), followed by <paramref name="reason"/>, which says why nothing was found.
    /// </summary>
    private static string FormatEmptyAnswer(AnalysisKind kind, string subject, string reason) =>
        ListOutput.Header(0, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject)) + "\n" + reason;

    /// <summary>
    /// A usage question about a property or event, restated as questions ILSpyX can answer:
    /// the underlying symbols to analyze and how they are named back to the caller.
    /// </summary>
    private sealed record SyntheticRoute(
        ISymbol Subject,
        IReadOnlyList<(ISymbol Symbol, AnalysisKind Kind)> Steps,
        IReadOnlyList<string> Via)
    {
        /// <summary>
        /// Run every step and merge. Each analyzer sorts its own output, but concatenating
        /// sorted lists is not itself sorted, so the merged list is re-sorted for a stable,
        /// readable order. A method that touches the member two ways — reading and writing a
        /// property, subscribing and raising an event — comes back once per step;
        /// <see cref="FormatAnswer"/> collapses the duplicate lines.
        ///
        /// The member being analyzed is dropped from its own results. ILSpyX reports a use
        /// that sits inside an accessor as a use by the property or event that accessor
        /// belongs to, so every field-like event lists itself — its add and remove accessors
        /// touch the subscriber list by construction. That is an artifact of asking the
        /// question about the accessors, not a call site anyone can act on, and it reads as
        /// if the event used itself. The outgoing direction needs the same filter for the
        /// mirror-image reason: a getter that calls its own setter would otherwise make the
        /// property appear in the list of what it uses. A reference to or from a
        /// <em>different</em> member of the same type reports under that member's name and is
        /// unaffected. The member is recognized by its definition, see
        /// <see cref="DefinitionOf"/>.
        ///
        /// What carries an attribute that sets the property by name is not such an artifact
        /// and stays, the property itself included when it carries one.
        /// <paramref name="undecided"/> is what carries an attribute whose bytes did not decide
        /// whether it sets the property.
        /// </summary>
        public IReadOnlyList<ISymbol> Run(AssemblyHost host, out IReadOnlyList<AttributeCarriers.Undecided> undecided)
        {
            var self = Steps
                .Select(step => DefinitionOf(step.Symbol))
                .Append(DefinitionOf(Subject))
                .ToHashSet();

            var open = new List<AttributeCarriers.Undecided>();
            var results = Steps
                .SelectMany(step =>
                {
                    var (inCode, asAttribute) = AskApart(host, step.Kind, step.Symbol, out var ofStep);
                    open.AddRange(ofStep);
                    return inCode
                        .Where(result => DefinitionOf(result) is not { } definition || !self.Contains(definition))
                        .Concat(asAttribute);
                })
                .OrderBy(ReferenceFormatter.FormatSymbol, StringComparer.Ordinal)
                .ToList();
            undecided = open;
            return results;
        }

        /// <summary>
        /// The definition a symbol stands for, as its file and row, or null for one without a
        /// row. The printed name does not serve: inside a generic class the code names the
        /// class's own member on the constructed type, <c>Box&lt;T&gt;.set_Value(T)</c>, which
        /// prints unlike the definition, <c>Box&lt;&gt;.set_Value(T)</c>. A member of another
        /// assembly can have the same row number, so the file belongs to the comparison.
        /// </summary>
        private static (MetadataFile File, EntityHandle Row)? DefinitionOf(ISymbol symbol) =>
            symbol is IEntity { MetadataToken.IsNil: false, ParentModule.MetadataFile: { } file } entity
                ? (file, entity.MetadataToken)
                : null;
    }

    /// <summary>
    /// The answer to <paramref name="kind"/> about <paramref name="symbol"/>: the ILSpyX
    /// analyzer's, or for who reads or writes a field, <see cref="FieldAccessScanner"/>'s, with
    /// a method not known to read or write it counted as one that does, or for who uses a
    /// type, <see cref="TypeUsageScanner"/>'s, since the analyzer for that question fails
    /// on an attribute the metadata library's decoder cannot read, or for what a method uses,
    /// <see cref="MethodUseScanner"/>'s, since the analyzer for that question resolves what a
    /// body names without the method's type parameters, or for who uses a method,
    /// <see cref="AssemblyHost.FindMethodUsers"/>'s, since the analyzers for that question
    /// count the users of a member the method hides, or for a type's extension methods,
    /// <see cref="AssemblyHost.FindExtensionMethods"/>'s, since the analyzer for that question
    /// looks in static classes alone and passes over a receiver passed by reference. Who uses
    /// a constructor is that answer and what carries an attribute applied through that
    /// constructor, and who uses a property's setter is that answer and what carries an
    /// attribute that sets the property by name: uses no method body shows. Where an attribute is
    /// applied to the assembly or to the module, which the analyzer answers alike with the
    /// module, the answer names the <see cref="GlobalAttributeTarget"/> that carries it. For a
    /// kind that asks who uses the symbol, each use found in compiler-generated code is
    /// reported under the member it was written in, before a route filters out the member
    /// itself, so a use inside a lambda in the member's own accessor is still recognized as
    /// the member using itself. <paramref name="undecided"/> is what carries an attribute whose
    /// bytes did not decide whether it names the type asked about, or sets the property whose
    /// setter is asked about.
    /// </summary>
    private static IReadOnlyList<ISymbol> Ask(AssemblyHost host, AnalysisKind kind, ISymbol symbol,
        out IReadOnlyList<AttributeCarriers.Undecided> undecided)
    {
        var (inCode, asAttribute) = AskApart(host, kind, symbol, out undecided);
        return [.. inCode, .. asAttribute];
    }

    /// <summary>
    /// <see cref="Ask"/> with the uses only an attribute shows, of a constructor or of a
    /// setter, apart from the others.
    /// </summary>
    private static (IReadOnlyList<ISymbol> InCode, IReadOnlyList<ISymbol> AsAttribute) AskApart(
        AssemblyHost host, AnalysisKind kind, ISymbol symbol, out IReadOnlyList<AttributeCarriers.Undecided> undecided)
    {
        undecided = [];
        var type = symbol as ITypeDefinition;
        IReadOnlyList<ISymbol> results;
        if (type is not null && kind == AnalysisKind.UsedBy)
            (results, undecided) = host.FindTypeUsers(type);
        else if (symbol is IField field && AccessKindOf(kind) is { } accessKind)
            results = host.FindFieldAccesses(field, accessKind).Select(access => access.Symbol).ToList();
        else if (kind == AnalysisKind.Uses)
            results = host.FindUsedSymbols((IMethod)symbol);
        else if (kind == AnalysisKind.UsedBy && symbol is IMethod asked)
            results = host.FindMethodUsers(asked);
        else if (kind == AnalysisKind.OverriddenBy && symbol is IMember member)
            results = host.FindOverrides(member);
        else if (type is not null && kind == AnalysisKind.ExtensionMethods)
            results = host.FindExtensionMethods(type);
        else
            results = host.RunAnalyzer(AnalysisDispatch.HeaderFor(kind), symbol, AnalysisDispatch.AnalyzerFor(kind));

        // Applying an attribute uses one of its constructors, and setting a property by name
        // in one uses the property's setter. No method body shows either.
        IReadOnlyList<ISymbol> asAttribute = [];
        if (kind == AnalysisKind.UsedBy && symbol is IMethod { IsStatic: false } method)
        {
            if (method.IsConstructor)
                asAttribute = host.FindAttributeCarriers(method);
            else if (method is { AccessorKind: System.Reflection.MethodSemanticsAttributes.Setter, AccessorOwner: IProperty property })
                (asAttribute, undecided) = host.FindNamedArgumentCarriers(property);
        }

        if (type is not null && kind == AnalysisKind.AppliedTo)
            results = [.. results.Where(result => result is not IModule), .. host.FindGlobalAttributeTargets(type)];
        return AnalysisDispatch.AsksWhoUses(kind)
            ? (host.AttributeToSource(results), host.AttributeToSource(asAttribute))
            : (results, asAttribute);
    }

    /// <summary>What an attribute left undecided about a type, and about a field or property.</summary>
    private const string NamesIt = "names it", SetsIt = "sets it";

    /// <summary>
    /// The lines an answer ends with for the attributes whose bytes did not decide whether
    /// they name the type asked about, or set the member asked about, which
    /// <paramref name="unknown"/> says (see <see cref="AttributeArgumentReader"/>): one line
    /// for each attribute class and enum, or null when there is none.
    /// <paramref name="results"/> gives what the answer lists, as it names them; a carrier it
    /// lists for another use gets no line.
    /// </summary>
    private static string NotCheckedAttributes(AssemblyHost host,
        IReadOnlyList<AttributeCarriers.Undecided> undecided, Func<IReadOnlyList<ISymbol>> results, string unknown)
    {
        if (undecided.Count == 0)
            return null;
        // A carrier is told from a listed member by its full name, which tells apart two members
        // whose short names read alike.
        var listed = results().Select(result => ReferenceFormatter.FormatSymbol(result, full: true)).ToHashSet(StringComparer.Ordinal);
        var lines = undecided
            .Select(attributes => (attributes.Attribute, attributes.Enum, Carriers: Unlisted(host.AttributeToSource(attributes.Carriers))))
            .Where(attributes => attributes.Carriers.Count > 0)
            .Select(attributes =>
                $"Not checked: whether {attributes.Attribute} on {string.Join(", ", attributes.Carriers)} {unknown}; " +
                $"its bytes fit more than one size of {attributes.Enum}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);

        List<string> Unlisted(IReadOnlyList<ISymbol> carriers)
        {
            var unlisted = carriers.Where(carrier => !listed.Contains(ReferenceFormatter.FormatSymbol(carrier, full: true))).ToList();
            var name = ReferenceFormatter.NamesFor(unlisted);
            return unlisted.Select(name).Distinct().Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// The lines an answer ends with for the methods it names whose parameters are written
    /// <c>ref</c> for want of a keyword (<see cref="ParameterPassing.KeywordIsUnknown"/>), or
    /// null when it names none: one line for the types of each library that were not read,
    /// naming the types those methods are called on and why they were not read
    /// (<see cref="UnreadLibrary"/>), and one for each type that was read and does not declare
    /// the method as called, as a library of another version does. The methods are those an
    /// answer to what a method uses lists, or the method of another assembly's type that an
    /// answer's header names.
    /// </summary>
    private static string NotCheckedKeywords(AssemblyHost host, IReadOnlyList<ISymbol> results,
        bool includeCompilerGenerated)
    {
        var types = results
            .Where(result => includeCompilerGenerated || !IsCompilerGenerated(result))
            .Select(result => result is MethodUseScanner.CompilerUse use ? use.Symbol : result)
            .OfType<IMethod>()
            .Where(method => method.Parameters.Any(parameter => ParameterPassing.Of(parameter).KeywordIsUnknown))
            .Select(method => (method.DeclaringType as ParameterizedType)?.GenericType ?? method.DeclaringType)
            .Select(type => (Name: ReferenceFormatter.FormatTypeName(type), Type: type))
            .DistinctBy(type => type.Name)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
        if (types.Count == 0)
            return null;

        var notRead = types
            .Where(type => type.Type.GetDefinition() is null)
            .Select(type => (type.Name, Libraries: ReferenceFormatter.FullTypeNameOf(type.Type) is { } name
                ? host.UnreadLibrariesOf(name)
                : []))
            .GroupBy(type => type.Libraries, SameLibraries)
            .Select(library => Line(library.Select(type => type.Name).ToList(),
                UnreadLibrary.Reason(library.Select(type => (type.Name, type.Libraries)))));
        var loaded = types
            .Where(type => type.Type.GetDefinition() is not null)
            .Select(type => Line([type.Name], $"{type.Name} as loaded declares no such method"));
        return string.Join("\n", notRead.Concat(loaded));

        static string Line(IReadOnlyList<string> on, string reason) =>
            $"Not checked: whether ref on a method of {string.Join(", ", on)} is out, in or ref readonly; {reason}";
    }

    /// <summary>Lists of libraries that name the same libraries in the same state.</summary>
    private static readonly IEqualityComparer<IReadOnlyList<UnreadLibrary>> SameLibraries =
        EqualityComparer<IReadOnlyList<UnreadLibrary>>.Create(
            (left, right) => left.SequenceEqual(right),
            libraries => libraries.Aggregate(0, (hash, library) => HashCode.Combine(hash, library)));

    /// <summary>
    /// How a usage question about a property or event is answered, or <c>null</c> when an
    /// ILSpyX analyzer answers the (symbol, kind) pair directly.
    /// </summary>
    /// <remarks>
    /// These questions are answered for methods, types, and fields only — who uses a method by
    /// <see cref="AssemblyHost.FindMethodUsers"/>, who uses a type
    /// by <see cref="TypeUsageScanner"/>, what a method uses by <see cref="MethodUseScanner"/>,
    /// and who reads or writes a field by
    /// <see cref="FieldAccessScanner"/>. A property or event therefore has a native route in neither
    /// direction: not to the call sites that reference it — the question most often asked of
    /// it, since in decompiled code most of the interesting surface is properties rather than
    /// fields — and not to what its own code calls either.
    ///
    /// Both directions are answered through the accessors, which is where the bodies live and
    /// what call sites actually reference, plus for a field-like event the field holding its
    /// subscriber list. That is the same substitution a decompiler makes when it presents a
    /// property or an event. <c>OverriddenBy</c> and <c>ImplementedBy</c> are left alone:
    /// <see cref="AssemblyHost.FindOverrides"/> answers the first for a property or an event
    /// as it stands, and <see cref="AssemblyHost.FindImplementations"/> the second.
    /// </remarks>
    private static SyntheticRoute RouteFor(ISymbol symbol, AnalysisKind kind)
    {
        if (symbol is IProperty property)
        {
            return kind switch
            {
                AnalysisKind.ReadBy => Route(property, AnalysisKind.UsedBy,
                    [RequireAccessor(property.Getter, property, kind, "getter", "write-only")]),
                AnalysisKind.AssignedBy => Route(property, AnalysisKind.UsedBy,
                    [RequireAccessor(property.Setter, property, kind, "setter", "read-only")]),
                // These two reach the accessors unchanged, unlike the pair above, which are
                // read and assign questions an accessor can only answer as "who calls me?".
                // Uses is the one outgoing direction: "what does this property call?" is
                // "what do its accessors call?". An abstract or interface accessor has no
                // body to read and contributes nothing, which is the honest answer for one.
                AnalysisKind.UsedBy or AnalysisKind.Uses => Route(property, kind,
                    DeclaredAccessors(property, property.Getter, property.Setter)),
                _ => null,
            };
        }

        // An event has no read/assign split — subscribing, unsubscribing, and raising are all
        // "use", so UsedBy unions them and no other kind needs rerouting. Raising reads the
        // subscriber list directly, so omitting the backing field would answer "who uses this
        // event?" with the subscribers alone and leave every raise site out.
        //
        // Uses is deliberately not offered here. A field-like event's accessors are generated,
        // so the answer would be the compiler's own subscriber-list bookkeeping — noise, not a
        // fact about the code anyone wrote.
        if (symbol is IEvent evt && kind == AnalysisKind.UsedBy)
        {
            var accessors = DeclaredAccessors(
                evt, evt.AddAccessor, evt.RemoveAccessor, evt.InvokeAccessor);
            var backingField = CompilerGeneratedFilter.BackingFieldOf(evt);
            if (backingField is null)
                return Route(evt, AnalysisKind.UsedBy, accessors);

            return new SyntheticRoute(evt,
                [.. accessors.Select(a => ((ISymbol)a, AnalysisKind.UsedBy)),
                 (backingField, AnalysisKind.ReadBy),
                 (backingField, AnalysisKind.AssignedBy)],
                [.. accessors.Select(a => a.Name), "the subscriber list"]);
        }

        return null;
    }

    /// <summary>
    /// A route that asks one question of each accessor and nothing else. The question is not
    /// always the kind the caller asked for — <c>ReadBy</c> and <c>AssignedBy</c> arrive here
    /// as <c>UsedBy</c>, because what an accessor can answer is who calls it.
    /// </summary>
    private static SyntheticRoute Route(
        ISymbol subject, AnalysisKind question, IReadOnlyList<IMethod> accessors) =>
        new(subject,
            [.. accessors.Select(a => ((ISymbol)a, question))],
            [.. accessors.Select(a => a.Name)]);

    /// <summary>
    /// The accessor a read or assign question resolves to, or an error naming why the
    /// property cannot answer it. A read-only property is never assigned and a write-only
    /// one is never read, so an empty answer would be true but unreadable — indistinguishable
    /// from a property that simply has no call sites, which is the opposite conclusion.
    /// </summary>
    private static IMethod RequireAccessor(IMethod accessor, IProperty property,
        AnalysisKind kind, string role, string shape) =>
        accessor ?? throw new ArgumentException(
            $"Analysis kind '{kind.ToSchemaName()}' on a property resolves to its {role}, and " +
            $"{ReferenceFormatter.FormatSymbol(property)} is {shape}. " +
            "Use kind 'used_by' for every call site of the property.");

    /// <summary>
    /// The error for a read or assign question about a constant, a decimal constant included.
    /// The compiler copies a constant's value into each place that uses it, so no instruction
    /// names the constant, and an empty answer would read as proof that nothing uses it. An
    /// enum value points to who uses its enum, the nearest question the compiled code answers:
    /// the methods that work with the enum through a variable, parameter or member of its type,
    /// whichever of its values they use.
    /// </summary>
    /// <param name="declaredIn">
    /// The assembly the constant was read from, when it is not the one inspected.
    /// </param>
    private static ArgumentException ConstantError(AnalysisKind kind, IField constant, string declaredIn)
    {
        var name = ReferenceFormatter.FormatSymbol(constant);
        var where = declaredIn is null ? "" : $" in {declaredIn}";
        var message = kind == AnalysisKind.AssignedBy
            ? $"{name} is a constant{where}: only its declaration sets it, and the compiler copies its " +
              "value into each place that uses it."
            : $"{name} is a constant{where}: the compiler copies its value into each place that uses it, " +
              "so the compiled code does not show which code reads it.";
        if (constant.DeclaringTypeDefinition is { Kind: TypeKind.Enum } enumType)
            message += $" Kind '{AnalysisKind.UsedBy.ToSchemaName()}' on {ReferenceFormatter.FormatTypeName(enumType)} " +
                "lists the methods that work with that enum through a variable, parameter or member of its type.";
        return new ArgumentException(message);
    }

    /// <summary>
    /// The accessors that exist among <paramref name="candidates"/>. An empty result means
    /// malformed metadata — a C# property declares at least one accessor and a C# event
    /// declares add and remove — so it is reported rather than passed off as "used nowhere".
    /// </summary>
    private static IReadOnlyList<IMethod> DeclaredAccessors(
        IMember member, params IMethod[] candidates)
    {
        var present = candidates.Where(a => a != null).ToList();
        if (present.Count == 0)
            throw new ArgumentException(
                $"{ReferenceFormatter.FormatSymbol(member)} declares no accessors, " +
                "so it has no call sites to analyze.");
        return present;
    }

    /// <summary>
    /// Why a question about a type nothing in the assembly names finds nothing: not in a
    /// reference row, a signature, a method body or an attribute. Saying so below the empty
    /// answer keeps a misspelled name from reading as an unused type.
    /// </summary>
    private const string NotReferenced =
        "This assembly neither defines nor references a type of that name.";

    /// <summary>
    /// The line an answer about a type nothing in the assembly names ends with: the assembly it
    /// forwards the type to, which keeps the empty answer from reading as a type that is not
    /// there at all, or else <see cref="NotReferenced"/>, followed by the types the caller may
    /// have meant, where any lies in another namespace or class
    /// (<see cref="SymbolResolver.Meant(string, string)"/>). <paramref name="subject"/> names the
    /// type in the line, as "it" under an answer about the type itself.
    /// </summary>
    private static string NotNamedHere(SymbolResolver resolver, SymbolResolver.ExternalTypeName type, string subject) =>
        type.Forward is { } target
            ? $"This assembly forwards {subject} to {target}."
            : resolver.Meant(type.Display) is { } meant
                ? $"{NotReferenced} Types {meant}."
                : NotReferenced;

    /// <summary>
    /// The line an answer about a member nothing references ends with when no assembly ILens
    /// found defines the type. A constant leaves no reference either, so without the assembly
    /// that declares it, the empty answer does not show that nothing uses the member.
    /// </summary>
    private static string ConstantNotRuledOut(SymbolResolver.ExternalTypeName type, string member_name) =>
        $"ILens cannot tell whether {member_name} is a constant, whose value the compiler copies into each " +
        $"use: no assembly it found defines {type.Display}" +
        (type.Origin is null ? "." : $", which comes from {type.Origin}.");

    /// <summary>
    /// The line ImplementedBy adds for a type from another assembly: the assembly's metadata
    /// does not show which interfaces another assembly's classes implement.
    /// </summary>
    private const string ImplementedThroughOtherAssemblies =
        "Types deriving from a class in another assembly may also implement it through that class; " +
        "this assembly does not show which.";

    /// <summary>
    /// <see cref="Analyze"/> for a type the assembly does not define, answered from the
    /// assembly's own references to it by <see cref="TypeUsageScanner"/>. A question those
    /// references cannot answer is an error saying why.
    /// </summary>
    private static string AnalyzeExternal(AssemblyHost host, SymbolResolver.ExternalTypeName type,
        AnalysisKind kind, string member_name, SymbolResolver.MethodHint hint,
        bool include_compiler_generated, int limit)
    {
        if (string.IsNullOrEmpty(member_name))
        {
            ValidateKind(kind, SymbolCategory.Type);
            var (found, unknown, undecided) = host.ScanReferences(type, scan =>
            {
                var results = kind switch
                {
                    AnalysisKind.UsedBy => scan.UsedBy(),
                    AnalysisKind.InstantiatedBy => scan.InstantiatedBy(),
                    AnalysisKind.ExposedBy => scan.ExposedBy(),
                    AnalysisKind.ExtensionMethods => scan.ExtensionMethods(host.Resolver.ExtensionMethods),
                    AnalysisKind.AppliedTo => scan.AppliedTo(),
                    AnalysisKind.ImplementedBy => scan.ImplementedBy(),
                    _ => throw new InvalidOperationException($"No type-level scan for analysis kind '{kind}'."),
                };
                // An attribute that may name the type keeps an empty answer from saying that
                // nothing in the assembly names it.
                var mayNameIt = kind == AnalysisKind.UsedBy ? scan.UndecidedUses : [];
                return (results, results.Count == 0 && mayNameIt.Count == 0 && !IsNamedAnywhere(scan, kind), mayNameIt);
            });
            var caveat = kind == AnalysisKind.ImplementedBy ? "\n" + ImplementedThroughOtherAssemblies : "";
            var notChecked = NotCheckedAttributes(host, undecided, () => host.AttributeToSource(found), NamesIt);
            return (unknown
                ? FormatEmptyAnswer(kind, type.Display, NotNamedHere(host.Resolver, type, "it"))
                : FormatExternalAnswer(host, kind, type.Display, found, include_compiler_generated, limit)) + caveat +
                (notChecked is null ? "" : "\n" + notChecked);
        }

        SymbolResolver.ValidateDisambiguationArgs(hint);
        // Checked before the member is looked up: a member nothing references gets an empty
        // answer, which would otherwise pass for an answer to a question no member can take.
        if (AnalysisDispatch.AppliesTo(kind).All(category => category == SymbolCategory.Type))
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is valid only for a type; omit {nameof(member_name)} to analyze {type.Display} itself.");
        if (kind is AnalysisKind.Uses or AnalysisKind.Implements)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' " +
                (kind == AnalysisKind.Uses ? "reads the member's body" : "needs the class that declares the member") +
                $", which is in the assembly that defines {type.Display}, not in this one. Analyze that assembly instead.");
        if (kind == AnalysisKind.CallSites)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is not available on a member of {type.Display}, which this " +
                $"assembly does not define; '{AnalysisKind.UsedBy.ToSchemaName()}' lists what uses it.");
        if (kind is AnalysisKind.OverriddenBy or AnalysisKind.ImplementedBy)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is not available on a member of {type.Display}, which this " +
                "assembly does not define: an override or implementation here leaves no reference to " +
                "the member it overrides or implements.");

        var (display, answer, fieldAnswer, missing, undecidedSets, notInherited, named) = host.ScanReferences<
            (string, IReadOnlyList<ISymbol>, IReadOnlyList<FieldAccessScanner.Access>, string,
            IReadOnlyList<AttributeCarriers.Undecided>, string, IMethod)>(type, scan =>
        {
            var members = scan.ReferencedMembers();
            var setByName = scan.SetByName.Members();
            var member = ResolveReferencedMember(members, setByName,
                type.Display, type.LookupNames[0].Name, member_name, hint);
            // An attribute sets a field or a property, the latter through its setter, so only a
            // question about who writes or uses one of them is about attributes.
            var asksWhoSets = kind is AnalysisKind.AssignedBy or AnalysisKind.UsedBy;
            if (member == null)
                return ($"{type.Display}.{member_name}", null, null,
                    DescribeMissingMember(host.Resolver, scan, members, setByName, type, member_name), [],
                    asksWhoSets ? NotCheckedInherited(scan, type, member_name, SetterPropertyOf(member_name)) : null,
                    null);

            ValidateKind(kind, member.Category);
            IReadOnlyList<ISymbol> carriers = [];
            IReadOnlyList<AttributeCarriers.Undecided> open = [];
            if (asksWhoSets && member.SetByName is { } named)
                (carriers, open) = (scan.CarriersOf(named), scan.SetByName.UndecidedOn(named));
            var notRead = !asksWhoSets ? null : member.Category == SymbolCategory.Method
                ? NotCheckedInherited(scan, type, SetterPropertyOf(member_name))
                : NotCheckedInherited(scan, type, member_name);

            if (member.Category == SymbolCategory.Field)
                return (member.Display, null,
                    [.. scan.AccessesOf(member.Parts, AccessKindOf(kind).Value),
                     .. carriers.Select(carrier => new FieldAccessScanner.Access(carrier, null))],
                    null, open, notRead, null);
            // The header names the method by its first row, a setter only attributes use by none.
            if (member.Category == SymbolCategory.Method)
                return (member.Display, [.. scan.CallersOf(member.Parts), .. scan.CarriersOf(member.Parts), .. carriers],
                    null, null, open, notRead, member.Parts.FirstOrDefault()?.Called);

            // A property or event is used through its accessors, as for a type the assembly
            // defines; only the accessors this assembly references can be asked about.
            var accessors = kind switch
            {
                AnalysisKind.ReadBy => member.Parts.Where(p => p.Name.StartsWith("get_", StringComparison.Ordinal)).ToList(),
                AnalysisKind.AssignedBy => member.Parts.Where(p => p.Name.StartsWith("set_", StringComparison.Ordinal)).ToList(),
                _ => member.Parts,
            };
            var via = accessors.Count == 0 ? "" : $" (via {string.Join(", ", accessors.Select(a => a.Name).Distinct())})";
            return (member.Display + via, [.. scan.CallersOf(accessors), .. carriers], null, null, open, notRead, null);
        });
        var trailing = notInherited is null ? "" : "\n" + notInherited;

        if (missing is not null)
        {
            // A hint names a method, which is never a constant.
            if (hint.IsGiven)
                return FormatEmptyAnswer(kind, display, missing) + trailing;

            // A constant leaves no reference to find, so whether the member is one is read from
            // the assembly that declares the type.
            var (typeFound, declared) = host.FindDeclaredField(type.LookupNames, member_name);
            if (declared is { IsConst: true })
            {
                ValidateKind(kind, SymbolCategory.Field);
                throw ConstantError(kind, declared, declared.ParentModule.AssemblyName);
            }
            return FormatEmptyAnswer(kind, display,
                typeFound ? missing : missing + "\n" + ConstantNotRuledOut(type, member_name)) + trailing;
        }

        var listed = fieldAnswer?.Select(access => access.Symbol).ToList() ?? answer;
        if (NotCheckedAttributes(host, undecidedSets, () => host.AttributeToSource(listed), SetsIt) is { } notDecided)
            trailing = "\n" + notDecided + trailing;
        // The header writes ref for a parameter whose keyword the method's declaration would say.
        if (named is not null && NotCheckedKeywords(host, [named], includeCompilerGenerated: true) is { } keywords)
            trailing = "\n" + keywords + trailing;
        return (fieldAnswer is not null
            ? FormatAccessAnswer(host, kind, display, fieldAnswer, include_compiler_generated, limit)
            : FormatExternalAnswer(host, kind, display, answer, include_compiler_generated, limit)) + trailing;
    }

    /// <summary>
    /// The property whose setter <paramref name="memberName"/> names, or null for a name that
    /// is no setter's.
    /// </summary>
    private static string SetterPropertyOf(string memberName) =>
        memberName.Length > SetterPrefix.Length && memberName.StartsWith(SetterPrefix, StringComparison.Ordinal)
            ? memberName[SetterPrefix.Length..]
            : null;

    private const string SetterPrefix = "set_";

    /// <summary>
    /// The lines an answer about a member of a type from another assembly ends with for the
    /// attribute classes that set a member of one of <paramref name="names"/> and whose own
    /// class or a base class of it ILens could not read: whether such a class inherits the name
    /// from the type asked about is recorded in its assembly alone. One line for each name and
    /// each library whose classes were not read, with the reason (<see cref="UnreadLibrary"/>),
    /// or null when there is none.
    /// </summary>
    private static string NotCheckedInherited(TypeUsageScanner scan, SymbolResolver.ExternalTypeName type,
        params string[] names)
    {
        var lines = names
            .Where(name => name is not null)
            .SelectMany(name => scan.SetByName.NotRead(name)
                .OrderBy(unread => unread.Unread.TypeName, StringComparer.Ordinal)
                .GroupBy(unread => unread.Unread.Libraries, SameLibraries)
                .Select(library =>
                {
                    var classes = library.Select(unread => unread.Class).Distinct().Order(StringComparer.Ordinal).ToList();
                    var reason = UnreadLibrary.Reason(
                        library.Select(unread => (unread.Unread.TypeName, unread.Unread.Libraries)));
                    return (Reason: reason,
                        Line: $"Not checked: whether {string.Join(", ", classes)} {(classes.Count == 1 ? "inherits" : "inherit")} " +
                            $"{name} from {type.Display}; {reason}");
                })
                .OrderBy(line => line.Reason, StringComparer.Ordinal)
                .Select(line => line.Line))
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// True if the assembly names the type anywhere, so an empty answer is an ordinary one.
    /// A reference row settles it at once; a core type written as a type code, or a type named
    /// only by a <c>typeof</c> in an attribute, takes a scan for any use.
    /// </summary>
    private static bool IsNamedAnywhere(TypeUsageScanner scan, AnalysisKind askedAlready) =>
        scan.IsReferenced || (askedAlready != AnalysisKind.UsedBy && scan.UsedBy().Count > 0);

    /// <summary>
    /// <see cref="FormatAnswer"/> for results <see cref="TypeUsageScanner"/> found, which
    /// name the method a use sits in even when the compiler generated it; a kind that asks who
    /// uses the symbol reports such a use under the member it was written in.
    /// </summary>
    private static string FormatExternalAnswer(AssemblyHost host, AnalysisKind kind, string subject,
        IReadOnlyList<ISymbol> results, bool includeCompilerGenerated, int limit) =>
        FormatAnswer(kind, subject, AnalysisDispatch.AsksWhoUses(kind) ? host.AttributeToSource(results) : results,
            includeCompilerGenerated, limit);

    /// <summary>A member of a type from another assembly, as the assembly's references show it.</summary>
    /// <param name="Parts">
    /// The referenced members it stands for: one method, the rows of one field, or a property's
    /// or event's accessors.
    /// </param>
    /// <param name="SetByName">
    /// The field or property attributes set by name that it stands for: the member itself, or
    /// for a setter its property. Null when no attribute sets it that way.
    /// </param>
    private sealed record ReferencedMemberGroup(
        SymbolCategory Category, string Display, IReadOnlyList<TypeUsageScanner.ReferencedMember> Parts,
        AttributeArgumentReader.NamedArgument? SetByName = null);

    /// <summary>
    /// The member of a type from another assembly that <paramref name="name"/> names, by the
    /// rules <see cref="SymbolResolver.ResolveMember"/> applies to a type the assembly defines:
    /// a hint selects one method overload, and without one a name found as more than one kind
    /// of member, or as several overloads, is an error listing them. A property or event is
    /// recognized by its accessors, the only part of it another assembly references. A field
    /// or property among <paramref name="setByName"/>, which attributes set by name, is known
    /// from their bytes, whether or not a row references it, and so is such a property's setter.
    /// The type's own name, <paramref name="simpleTypeName"/>, means its constructors when no
    /// member of that name is referenced. Returns null when the assembly references no member
    /// of that name.
    /// </summary>
    private static ReferencedMemberGroup ResolveReferencedMember(
        IReadOnlyList<TypeUsageScanner.ReferencedMember> members,
        IReadOnlyList<AttributeArgumentReader.NamedArgument> setByName, string typeDisplay, string simpleTypeName,
        string name, SymbolResolver.MethodHint hint)
    {
        // Type parameters in brackets select a generic method by their count, and a name without
        // brackets the methods without type parameters where the name has one, unless a hint
        // none of those satisfies asks for a generic one, as SymbolResolver reads a method name.
        if (!members.Any(m => m.Name == name) && SymbolResolver.ReadTypeArgumentCount(name) is (var bare, { } count))
        {
            var generic = members.Where(m => !m.IsField && m.Name == bare && m.TypeParameterCount == count).ToList();
            if (generic.Count == 0)
                return null;
            return ResolveReferencedMember(generic, [], typeDisplay, simpleTypeName, bare, hint);
        }
        // The members of the name a header is told apart from, before any of them is left out.
        var namesakes = members.Where(m => !m.IsField && m.Name == name).ToList();
        // A hint is held exactly first, wherever a fit is found, and only then loosely, as
        // SymbolResolver holds one.
        var exact = hint.HasPatterns && namesakes.Any(m => SatisfiesHint(m, hint, exact: true));
        var plain = namesakes.Where(m => m.TypeParameterCount == 0).ToList();
        if (plain.Count > 0 && (!hint.IsGiven || plain.Any(m => SatisfiesHint(m, hint, exact))))
            members = members.Where(m => m.IsField || m.Name != name || m.TypeParameterCount == 0).ToList();

        var methods = members.Where(m => !m.IsField && m.Name == name).ToList();
        var fields = members.Where(m => m.IsField && m.Name == name).ToList();
        var propertyAccessors = Named("get_" + name).Concat(Named("set_" + name)).ToList();
        var eventAccessors = Named("add_" + name).Concat(Named("remove_" + name)).ToList();
        var fieldSet = SetByName(isField: true, name);
        var propertySet = SetByName(isField: false, name);
        var setterSet = SetterPropertyOf(name) is { } property ? SetByName(isField: false, property) : null;
        var kinds = new List<SymbolCategory>();
        if (methods.Count > 0 || setterSet is not null) kinds.Add(SymbolCategory.Method);
        if (fields.Count > 0 || fieldSet is not null) kinds.Add(SymbolCategory.Field);
        if (propertyAccessors.Count > 0 || propertySet is not null) kinds.Add(SymbolCategory.Property);
        if (eventAccessors.Count > 0) kinds.Add(SymbolCategory.Event);

        if (kinds.Count == 0 && name == simpleTypeName && members.Any(m => !m.IsField && m.Name == ".ctor"))
            return ResolveReferencedMember(members, setByName, typeDisplay, simpleTypeName, ".ctor", hint);

        if (hint.IsGiven)
        {
            var others = kinds.Where(k => k != SymbolCategory.Method).ToList();
            if (methods.Count == 0 && others.Count > 0)
                throw new ArgumentException(
                    $"Member '{name}' on {typeDisplay} is referenced here only as " +
                    $"{string.Join(" or ", others.Select(k => k == SymbolCategory.Event ? "an event" : "a " + k.ToString().ToLowerInvariant()))}, " +
                    $"not as a method; {HintParameters} select a method overload, so omit them.");
            // A setter only attributes use has no reference row whose parameters a hint could
            // be held against, and "not referenced" would read as nothing using it.
            if (methods.Count == 0 && setterSet is { } setOnly)
                throw new ArgumentException(
                    $"'{name}' on {typeDisplay} is known here only from the attributes that set {setOnly.Name} by name, " +
                    $"which hold no parameter list to select by; omit {HintParameters}.");
            if (methods.Count == 0)
                return null;
            var satisfying = methods.Where(m => SatisfiesHint(m, hint, exact)).ToList();
            if (satisfying.Count == 1)
                return Method(satisfying[0]);
            throw new ArgumentException(satisfying.Count == 0
                ? $"No overload of '{name}' on {typeDisplay} matches {hint.Describe()}. Available: {List(methods)}."
                : $"'{name}' on {typeDisplay} has {satisfying.Count} matching overloads; {Advice(satisfying)}: {List(satisfying)}.");
        }

        if (kinds.Count == 0)
            return null;
        if (kinds.Count > 1)
            throw new ArgumentException(
                $"Member '{name}' on {typeDisplay} is ambiguous — it exists as more than one member kind " +
                $"({string.Join(", ", kinds)}). Pass a {ParameterNames.ParameterCount} / {ParameterNames.ParameterTypes} hint if you mean the method.");
        if (kinds[0] == SymbolCategory.Method && methods.Count > 1)
            throw new ArgumentException(
                $"'{name}' on {typeDisplay} has {methods.Count} matching overloads; {Advice(methods)}: {List(methods)}.");

        return kinds[0] switch
        {
            SymbolCategory.Method when methods.Count == 0 => new(SymbolCategory.Method, $"{typeDisplay}.{name}", [], setterSet),
            SymbolCategory.Method => Method(methods[0]),
            SymbolCategory.Field => new(SymbolCategory.Field, $"{typeDisplay}.{name}", fields, fieldSet),
            SymbolCategory.Property => new(SymbolCategory.Property, $"{typeDisplay}.{name}", propertyAccessors, propertySet),
            _ => new(SymbolCategory.Event, $"{typeDisplay}.{name}", eventAccessors),
        };

        IEnumerable<TypeUsageScanner.ReferencedMember> Named(string accessorName) =>
            members.Where(m => !m.IsField && m.Name == accessorName);
        AttributeArgumentReader.NamedArgument? SetByName(bool isField, string memberName) =>
            setByName.Contains(new(isField, memberName)) ? new(isField, memberName) : null;
        // The header names the method in full where another method of its name would read alike.
        ReferencedMemberGroup Method(TypeUsageScanner.ReferencedMember method) =>
            new(SymbolCategory.Method,
                $"{typeDisplay}.{ReferenceFormatter.NameAmong(method, namesakes, (m, full) => Signature(m, full))}",
                [method], setterSet);
        string List(IReadOnlyList<TypeUsageScanner.ReferencedMember> overloads)
        {
            var line = ReferenceFormatter.Distinguishing(overloads,
                m => $"{typeDisplay}.{Signature(m, full: false)}", m => $"{typeDisplay}.{Signature(m, full: true)}");
            return string.Join(", ", overloads.Select(line));
        }
        string Advice(IReadOnlyList<TypeUsageScanner.ReferencedMember> candidates) =>
            SymbolResolver.OverloadAdvice(candidates
                .Select(m => (new string(',', m.TypeParameterCount), Parameters(m, full: true),
                    m.ReturnType is null ? "" : ReferenceFormatter.FormatTypeRef(m.ReturnType, full: true)))
                .ToList(), hint);
    }

    /// <summary>The parameters a hint selects a method overload by, as an error names them.</summary>
    private const string HintParameters =
        $"{ParameterNames.ParameterCount}, {ParameterNames.ParameterTypes} and {ParameterNames.ReturnType}";

    /// <summary>
    /// A referenced method's name and parameter list. The types are those of the reference
    /// row, and a parameter passed by reference takes the keyword of the method's declaration,
    /// which the row does not hold, or <c>ref</c> where no declaration was read. A generic
    /// method's type parameters are written unbound, <c>Make&lt;&gt;</c>, as the type's are, since
    /// their names are in the other assembly and the row numbers them. A conversion operator is
    /// followed by its return type, as <see cref="ReferenceFormatter.FormatMethodReference(IMethod)"/>
    /// writes one, and with <paramref name="full"/> every type is written in full.
    /// </summary>
    private static string Signature(TypeUsageScanner.ReferencedMember method, bool full) =>
        $"{method.Name}{(method.TypeParameterCount > 0 ? $"<{new string(',', method.TypeParameterCount - 1)}>" : "")}" +
        $"({Parameters(method, full)})" +
        (ReferenceFormatter.IsConversionOperator(method.Name) && method.ReturnType is { } returnType
            ? " → " + ReferenceFormatter.FormatTypeRef(returnType, full)
            : "");

    /// <summary>A referenced method's parameter list, as <see cref="Signature"/> writes it.</summary>
    private static string Parameters(TypeUsageScanner.ReferencedMember method, bool full) =>
        string.Join(", ", method.ParameterTypes.Select((type, index) =>
            (type is ByReferenceType ? Keyword(method.Called, index) : "") + ReferenceFormatter.FormatTypeRef(type, full)));

    private static string Keyword(IMethod called, int index) =>
        called is not null && index < called.Parameters.Count
            && ParameterPassing.Of(called.Parameters[index]) is { ByReference: true } passing
            ? ReferenceFormatter.PassingKeyword(passing)
            : "ref ";

    /// <summary>
    /// True if the method satisfies the hint, exactly or loosely, as
    /// <see cref="SymbolResolver.MethodHint.Fits(IReadOnlyList{IType}, IType, bool)"/> holds it.
    /// </summary>
    private static bool SatisfiesHint(TypeUsageScanner.ReferencedMember method, SymbolResolver.MethodHint hint,
        bool exact) =>
        hint.Fits(method.ParameterTypes, method.ReturnType, exact);

    /// <summary>
    /// Why a member name the assembly references nothing by finds nothing. It names the members
    /// of the type the assembly does reference, those attributes set by name among them, since
    /// no other tool can list them and a misspelled or miscased name would otherwise read as an
    /// unused member.
    /// </summary>
    private static string DescribeMissingMember(SymbolResolver resolver, TypeUsageScanner scan,
        IReadOnlyList<TypeUsageScanner.ReferencedMember> members,
        IReadOnlyList<AttributeArgumentReader.NamedArgument> setByName, SymbolResolver.ExternalTypeName type,
        string member_name)
    {
        if (members.Count == 0 && setByName.Count == 0)
            return scan.IsReferenced || scan.UsedBy().Count > 0
                ? $"This assembly references no member of {type.Display}."
                : NotNamedHere(resolver, type, type.Display);
        var names = members.Select(m => MemberNameOf(m.Name)).Concat(setByName.Select(m => m.Name))
            .Distinct().Order(StringComparer.Ordinal);
        return $"This assembly references no member {member_name} of {type.Display}. " +
            $"It references: {string.Join(", ", names)}.";

        static string MemberNameOf(string name)
        {
            foreach (var prefix in new[] { "get_", "set_", "add_", "remove_" })
            {
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal))
                    return name[prefix.Length..];
            }
            return name;
        }
    }

    private static void ValidateKind(AnalysisKind kind, SymbolCategory category)
    {
        if (AnalysisDispatch.AppliesTo(kind).Contains(category))
            return;

        var valid = string.Join(", ", AnalysisDispatch.KindsFor(category).Select(k => k.ToSchemaName()));
        throw new ArgumentException(
            $"Analysis kind '{kind.ToSchemaName()}' is not valid for {category}. " +
            $"Valid kinds for {category}: {valid}.");
    }
}
