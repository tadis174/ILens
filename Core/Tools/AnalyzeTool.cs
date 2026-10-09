using System.ComponentModel;
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
        "  Property: used_by, read_by, assigned_by, uses, overridden_by, implemented_by\n" +
        "  Field:    read_by, assigned_by\n" +
        "  Event:    used_by, overridden_by, implemented_by\n" +
        "call_sites lists every call to a method, one per line with what it passes, and covers all " +
        "overloads unless a hint selects one. Arguments are matched to parameters as the compiler " +
        "bound them, so named, reordered and omitted arguments are resolved, and a call the IL holds " +
        "but the decompiled C# does not show is reported, not dropped.\n" +
        "used_by / read_by / assigned_by / uses on a property route to its accessors — read_by to " +
        "the getter, assigned_by to the setter, used_by to both, and uses to both for the " +
        "outgoing direction (what the accessor bodies call). used_by on an event covers its add, " +
        "remove, and invoke accessors plus, for a field-like event, the field holding the " +
        "subscriber list — so raise sites are reported and not just subscribers. The result " +
        "header names what was consulted. read_by on a write-only property, or assigned_by on a " +
        "read-only one, errors rather than returning an empty result.\n" +
        "applied_to requires an Attribute-derived type — it errors on a non-attribute type. " +
        "implemented_by on a type requires an interface — it errors on a non-interface type.\n" +
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
        [Description("With kind call_sites, show only the argument passed for this parameter; overloads without it are left out.")] string parameter_name = null,
        [Description("With parameter_name, keep only the calls whose argument is not provably the parameter's default.")] bool non_default_only = false,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        // A parameter that does not apply would otherwise be dropped silently, and the answer to
        // a broader question returned as if it were the one asked.
        if (kind != AnalysisKind.CallSites && (!string.IsNullOrEmpty(parameter_name) || non_default_only))
            throw new ArgumentException(
                $"{nameof(parameter_name)} and {nameof(non_default_only)} apply only to kind " +
                $"'{AnalysisKind.CallSites.ToSchemaName()}'.");
        if (string.IsNullOrEmpty(member_name) && (parameter_count.HasValue || parameter_types != null))
            throw new ArgumentException(
                $"{nameof(parameter_count)} and {nameof(parameter_types)} select a method overload, " +
                $"so they need {nameof(member_name)}.");

        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;
        var resolver = host.Resolver;
        var (type, external) = resolver.ResolveTypeOrExternal(type_name);
        if (external != null)
            return AnalyzeExternal(host, external, kind, member_name, parameter_count, parameter_types,
                include_compiler_generated, limit);

        // call_sites covers every overload of the name unless a hint selects one, so it resolves
        // the name itself rather than through the single-member resolution below.
        if (kind == AnalysisKind.CallSites)
        {
            if (string.IsNullOrEmpty(member_name))
                ValidateKind(kind, SymbolCategory.Type);
            return CallSiteReport.Format(host, type, member_name, parameter_count, parameter_types,
                parameter_name, non_default_only, limit);
        }

        // Default to type-level analysis; member resolution overrides if member_name is set.
        // IsNullOrEmpty (not just `is null`) so an LLM passing "" for "omit" works as expected.
        ISymbol symbol = type;
        var category = SymbolCategory.Type;
        var originSuffix = "";
        if (!string.IsNullOrEmpty(member_name))
        {
            var (resolved, origin, resolvedCategory) =
                resolver.ResolveMember(type, member_name, parameter_count, parameter_types);
            symbol = resolved;
            category = resolvedCategory;
            originSuffix = " " + origin.Format();
        }

        ValidateKind(kind, category);

        // AppliedTo asks "what is this attribute applied to?" — only meaningful for an
        // Attribute-derived type. ValidateKind checks only the symbol category (Type),
        // not attribute-ness; without this check a non-attribute type would yield an
        // empty result indistinguishable from "applied nowhere". This guard is specific
        // to AppliedTo: its name presupposes the input kind and the precondition is a
        // crisp base-type check, unlike the murkier preconditions of the other kinds.
        if (kind == AnalysisKind.AppliedTo && !IsAttribute(type))
            throw new ArgumentException(
                $"Analysis kind 'applied_to' requires an attribute type; " +
                $"{ReferenceFormatter.FormatTypeName(type)} does not derive from System.Attribute.");

        // ImplementedBy on a type only makes sense for an interface. The same empty-result
        // ambiguity as AppliedTo applies — "implementers of System.String" would silently
        // return nothing — and the precondition is again a crisp kind check.
        if (kind == AnalysisKind.ImplementedBy && category == SymbolCategory.Type
            && type.Kind != TypeKind.Interface)
            throw new ArgumentException(
                $"Analysis kind 'implemented_by' on a type requires an interface; " +
                $"{ReferenceFormatter.FormatTypeName(type)} has kind {type.Kind}.");

        if (symbol is IField field && AccessKindOf(kind) is { } accessKind)
            return FormatAccessAnswer(host, kind, $"{ReferenceFormatter.FormatSymbol(field)}{originSuffix}",
                host.FindFieldAccesses(field, accessKind), include_compiler_generated, limit);

        var route = RouteFor(symbol, kind);

        IReadOnlyList<ISymbol> results;
        var viaSuffix = "";
        if (route != null)
        {
            results = route.Run(host);
            viaSuffix = $" (via {string.Join(", ", route.Via)})";
        }
        // ILSpyX ships no type-level "Implemented By" analyzer (only member-level), so
        // RunAnalyzer would return empty for this case. AssemblyHost.FindImplementingTypes
        // synthesizes the result by walking the main module.
        else if (kind == AnalysisKind.ImplementedBy && category == SymbolCategory.Type)
        {
            results = host.FindImplementingTypes(type);
        }
        else
        {
            results = Ask(host, kind, symbol);
        }

        var display = ReferenceFormatter.FormatSymbol(symbol);
        var reachedThrough = AnalysisDispatch.AsksWhoUses(kind) && symbol is IMember member
            ? ReferenceFormatter.FormatExplicitRoute(member)
              ?? ReferenceFormatter.FormatOverrideRoute(member, listsCalls: false)
            : null;

        return FormatAnswer(kind, $"{display}{originSuffix}{viaSuffix}", results, include_compiler_generated, limit) +
            (reachedThrough is null ? "" : "\n" + reachedThrough);
    }

    /// <summary>
    /// The answer to <paramref name="kind"/> about <paramref name="subject"/>: a header counting
    /// the results in the kind's own words ("12 users of X.Y():"), then one result per line,
    /// sorted. Results that print alike are one line, and the count is of lines. What the
    /// compiler generated is left out unless <paramref name="includeCompilerGenerated"/>.
    /// </summary>
    private static string FormatAnswer(AnalysisKind kind, string subject, IReadOnlyList<ISymbol> results,
        bool includeCompilerGenerated, int limit) =>
        ListOutput.Format(
            results
                .Where(result => includeCompilerGenerated || !IsCompilerGenerated(result))
                .Select(ReferenceFormatter.FormatSymbol)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList(),
            limit, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject));

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
        return ListOutput.Format(
            sources.Zip(accesses, (source, access) => (Source: source, access.Uncertainty))
                .Where(result => includeCompilerGenerated || !IsCompilerGenerated(result.Source))
                .GroupBy(result => ReferenceFormatter.FormatSymbol(result.Source), result => result.Uncertainty)
                .OrderBy(member => member.Key, StringComparer.Ordinal)
                .Select(member => FieldAccessScanner.Uncertainty.Merge(member) is { } uncertainty
                    ? $"{member.Key} (may {verb}: {uncertainty})"
                    : member.Key)
                .ToList(),
            limit, AnalysisDispatch.CountedAs(kind), AnalysisDispatch.About(kind, subject));
    }

    /// <summary>Whether a kind asks who reads or who writes, or null for any other kind.</summary>
    private static FieldAccessScanner.AccessKind? AccessKindOf(AnalysisKind kind) => kind switch
    {
        AnalysisKind.ReadBy => FieldAccessScanner.AccessKind.Read,
        AnalysisKind.AssignedBy => FieldAccessScanner.AccessKind.Write,
        _ => null,
    };

    /// <summary>
    /// True for a result the compiler generated, by the rules of <see cref="CompilerGeneratedFilter"/>.
    /// A type only referenced from another assembly cannot be told and counts as written in source.
    /// </summary>
    private static bool IsCompilerGenerated(ISymbol result) => result switch
    {
        IMember member => CompilerGeneratedFilter.IsCompilerGenerated(member),
        ITypeDefinition type => CompilerGeneratedFilter.IsCompilerGenerated(type),
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
        /// unaffected.
        /// </summary>
        public IReadOnlyList<ISymbol> Run(AssemblyHost host)
        {
            var self = Steps
                .Select(step => ReferenceFormatter.FormatSymbol(step.Symbol))
                .Append(ReferenceFormatter.FormatSymbol(Subject))
                .ToHashSet(StringComparer.Ordinal);

            return Steps
                .SelectMany(step => Ask(host, step.Kind, step.Symbol))
                .Where(result => !self.Contains(ReferenceFormatter.FormatSymbol(result)))
                .OrderBy(ReferenceFormatter.FormatSymbol, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// The answer to <paramref name="kind"/> about <paramref name="symbol"/>: the ILSpyX
    /// analyzer's, or for who reads or writes a field, <see cref="FieldAccessScanner"/>'s, with
    /// a method not known to read or write it counted as one that does. For a kind that asks who
    /// uses the symbol, each use found in compiler-generated code is reported under the member it
    /// was written in, before a route filters out the member itself, so a use inside a lambda in
    /// the member's own accessor is still recognized as the member using itself.
    /// </summary>
    private static IReadOnlyList<ISymbol> Ask(AssemblyHost host, AnalysisKind kind, ISymbol symbol)
    {
        var results = symbol is IField field && AccessKindOf(kind) is { } accessKind
            ? host.FindFieldAccesses(field, accessKind).Select(access => access.Symbol).ToList()
            : host.RunAnalyzer(AnalysisDispatch.HeaderFor(kind), symbol, AnalysisDispatch.AnalyzerFor(kind, symbol));
        return AnalysisDispatch.AsksWhoUses(kind) ? host.AttributeToSource(results) : results;
    }

    /// <summary>
    /// How a usage question about a property or event is answered, or <c>null</c> when an
    /// ILSpyX analyzer answers the (symbol, kind) pair directly.
    /// </summary>
    /// <remarks>
    /// These questions are answered for methods, types, and fields only — "Used By" by ILSpyX's
    /// <c>MethodUsedByAnalyzer</c> / <c>MethodVirtualUsedByAnalyzer</c> / <c>TypeUsedByAnalyzer</c>,
    /// "Uses" by its <c>MethodUsesAnalyzer</c>, and who reads or writes a field by
    /// <see cref="FieldAccessScanner"/>. A property or event therefore has a native route in neither
    /// direction: not to the call sites that reference it — the question most often asked of
    /// it, since in decompiled code most of the interesting surface is properties rather than
    /// fields — and not to what its own code calls either.
    ///
    /// Both directions are answered through the accessors, which is where the bodies live and
    /// what call sites actually reference, plus for a field-like event the field holding its
    /// subscriber list. That is the same substitution a decompiler makes when it presents a
    /// property or an event. <c>OverriddenBy</c> and <c>ImplementedBy</c> are left alone;
    /// ILSpyX does ship property- and event-level analyzers for those headers.
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
                // body for the analyzer to walk and contributes nothing, which is the honest
                // answer for one.
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
            var backingField = SymbolResolver.BackingFieldOf(evt);
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
    /// The line ImplementedBy adds for a type from another assembly: the assembly's metadata
    /// does not show which interfaces another assembly's classes implement.
    /// </summary>
    private const string ImplementedThroughOtherAssemblies =
        "Types deriving from a class in another assembly may also implement it through that class; " +
        "this assembly does not show which.";

    /// <summary>
    /// <see cref="Analyze"/> for a type the assembly does not define, answered from the
    /// assembly's own references to it by <see cref="ReferencedTypeScanner"/>. A question those
    /// references cannot answer is an error saying why.
    /// </summary>
    private static string AnalyzeExternal(AssemblyHost host, SymbolResolver.ExternalTypeName type,
        AnalysisKind kind, string member_name, int? parameter_count, string[] parameter_types,
        bool include_compiler_generated, int limit)
    {
        if (string.IsNullOrEmpty(member_name))
        {
            ValidateKind(kind, SymbolCategory.Type);
            var (found, unknown) = host.ScanReferences(type, scan =>
            {
                var results = kind switch
                {
                    AnalysisKind.UsedBy => scan.UsedBy(),
                    AnalysisKind.InstantiatedBy => scan.InstantiatedBy(),
                    AnalysisKind.ExposedBy => scan.ExposedBy(),
                    AnalysisKind.ExtensionMethods => scan.ExtensionMethods(),
                    AnalysisKind.AppliedTo => scan.AppliedTo(),
                    AnalysisKind.ImplementedBy => scan.ImplementedBy(),
                    _ => throw new InvalidOperationException($"No type-level scan for analysis kind '{kind}'."),
                };
                return (results, results.Count == 0 && !IsNamedAnywhere(scan, kind));
            });
            var caveat = kind == AnalysisKind.ImplementedBy ? "\n" + ImplementedThroughOtherAssemblies : "";
            return (unknown
                ? FormatEmptyAnswer(kind, type.Display, NotReferenced)
                : FormatExternalAnswer(host, kind, type.Display, found, include_compiler_generated, limit)) + caveat;
        }

        SymbolResolver.ValidateDisambiguationArgs(parameter_count, parameter_types);
        // Checked before the member is looked up: a member nothing references gets an empty
        // answer, which would otherwise pass for an answer to a question no member can take.
        if (AnalysisDispatch.AppliesTo(kind).All(category => category == SymbolCategory.Type))
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is valid only for a type; omit {nameof(member_name)} to analyze {type.Display} itself.");
        if (kind is AnalysisKind.Uses or AnalysisKind.Implements)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' reads the member's body, which is in the assembly that defines " +
                $"{type.Display}, not in this one. Analyze that assembly instead.");
        if (kind == AnalysisKind.CallSites)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is not available on a member of {type.Display}, which this " +
                $"assembly does not define; '{AnalysisKind.UsedBy.ToSchemaName()}' lists the methods that call it.");
        if (kind is AnalysisKind.OverriddenBy or AnalysisKind.ImplementedBy)
            throw new ArgumentException(
                $"Analysis kind '{kind.ToSchemaName()}' is not available on a member of {type.Display}, which this " +
                "assembly does not define: an override or implementation here leaves no reference to " +
                "the member it overrides or implements.");

        var (display, answer, fieldAnswer, missing) = host.ScanReferences<
            (string, IReadOnlyList<ISymbol>, IReadOnlyList<FieldAccessScanner.Access>, string)>(type, scan =>
        {
            var members = scan.ReferencedMembers();
            var member = ResolveReferencedMember(members, type.Display, member_name, parameter_count, parameter_types);
            if (member == null)
                return ($"{type.Display}.{member_name}", null, null, DescribeMissingMember(scan, members, type, member_name));

            ValidateKind(kind, member.Category);
            if (member.Category == SymbolCategory.Field)
                return (member.Display, null, scan.AccessesOf(member.Parts, AccessKindOf(kind).Value), null);
            if (member.Category == SymbolCategory.Method)
                return (member.Display, scan.CallersOf(member.Parts), null, null);

            // A property or event is used through its accessors, as for a type the assembly
            // defines; only the accessors this assembly references can be asked about.
            var accessors = kind switch
            {
                AnalysisKind.ReadBy => member.Parts.Where(p => p.Name.StartsWith("get_", StringComparison.Ordinal)).ToList(),
                AnalysisKind.AssignedBy => member.Parts.Where(p => p.Name.StartsWith("set_", StringComparison.Ordinal)).ToList(),
                _ => member.Parts,
            };
            var via = accessors.Count == 0 ? "" : $" (via {string.Join(", ", accessors.Select(a => a.Name).Distinct())})";
            return (member.Display + via, scan.CallersOf(accessors), null, null);
        });

        if (missing is not null)
            return FormatEmptyAnswer(kind, display, missing);
        return fieldAnswer is not null
            ? FormatAccessAnswer(host, kind, display, fieldAnswer, include_compiler_generated, limit)
            : FormatExternalAnswer(host, kind, display, answer, include_compiler_generated, limit);
    }

    /// <summary>
    /// True if the assembly names the type anywhere, so an empty answer is an ordinary one.
    /// A reference row settles it at once; a core type written as a type code, or a type named
    /// only by a <c>typeof</c> in an attribute, takes a scan for any use.
    /// </summary>
    private static bool IsNamedAnywhere(ReferencedTypeScanner scan, AnalysisKind askedAlready) =>
        scan.IsReferenced || (askedAlready != AnalysisKind.UsedBy && scan.UsedBy().Count > 0);

    /// <summary>
    /// <see cref="FormatAnswer"/> for results <see cref="ReferencedTypeScanner"/> found, which
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
    private sealed record ReferencedMemberGroup(
        SymbolCategory Category, string Display, IReadOnlyList<ReferencedTypeScanner.ReferencedMember> Parts);

    /// <summary>
    /// The member of a type from another assembly that <paramref name="name"/> names, by the
    /// rules <see cref="SymbolResolver.ResolveMember"/> applies to a type the assembly defines:
    /// a hint selects one method overload, and without one a name found as more than one kind
    /// of member, or as several overloads, is an error listing them. A property or event is
    /// recognized by its accessors, the only part of it another assembly references. Returns
    /// null when the assembly references no member of that name.
    /// </summary>
    private static ReferencedMemberGroup ResolveReferencedMember(
        IReadOnlyList<ReferencedTypeScanner.ReferencedMember> members, string typeDisplay, string name,
        int? parameter_count, string[] parameter_types)
    {
        var methods = members.Where(m => !m.IsField && m.Name == name).ToList();
        var fields = members.Where(m => m.IsField && m.Name == name).ToList();
        var propertyAccessors = Named("get_" + name).Concat(Named("set_" + name)).ToList();
        var eventAccessors = Named("add_" + name).Concat(Named("remove_" + name)).ToList();
        var kinds = new List<SymbolCategory>();
        if (methods.Count > 0) kinds.Add(SymbolCategory.Method);
        if (fields.Count > 0) kinds.Add(SymbolCategory.Field);
        if (propertyAccessors.Count > 0) kinds.Add(SymbolCategory.Property);
        if (eventAccessors.Count > 0) kinds.Add(SymbolCategory.Event);

        if (parameter_count.HasValue || parameter_types != null)
        {
            if (methods.Count == 0 && kinds.Count > 0)
                throw new ArgumentException(
                    $"Member '{name}' on {typeDisplay} is referenced here only as " +
                    $"{string.Join(" or ", kinds.Select(k => k == SymbolCategory.Event ? "an event" : "a " + k.ToString().ToLowerInvariant()))}, " +
                    $"not as a method; {nameof(parameter_count)} and {nameof(parameter_types)} select a method overload, so omit them.");
            if (methods.Count == 0)
                return null;
            var satisfying = methods.Where(m => SatisfiesHint(m, parameter_count, parameter_types)).ToList();
            if (satisfying.Count == 1)
                return Method(satisfying[0]);
            throw new ArgumentException(satisfying.Count == 0
                ? $"No overload of '{name}' on {typeDisplay} matches " +
                  (parameter_types != null ? $"parameter types ({string.Join(", ", parameter_types)})" : $"{parameter_count} parameter(s)") +
                  $". Available: {List(methods)}."
                : $"'{name}' on {typeDisplay} has {satisfying.Count} matching overloads; the candidates " +
                  $"share these parameter types and cannot be narrowed further: {List(satisfying)}.");
        }

        if (kinds.Count == 0)
            return null;
        if (kinds.Count > 1)
            throw new ArgumentException(
                $"Member '{name}' on {typeDisplay} is ambiguous — it exists as more than one member kind " +
                $"({string.Join(", ", kinds)}). Pass a {nameof(parameter_count)} / {nameof(parameter_types)} hint if you mean the method.");
        if (kinds[0] == SymbolCategory.Method && methods.Count > 1)
            throw new ArgumentException(
                $"'{name}' on {typeDisplay} has {methods.Count} matching overloads; specify {nameof(parameter_count)} " +
                $"or {nameof(parameter_types)} to disambiguate: {List(methods)}.");

        return kinds[0] switch
        {
            SymbolCategory.Method => Method(methods[0]),
            SymbolCategory.Field => new(SymbolCategory.Field, $"{typeDisplay}.{name}", fields),
            SymbolCategory.Property => new(SymbolCategory.Property, $"{typeDisplay}.{name}", propertyAccessors),
            _ => new(SymbolCategory.Event, $"{typeDisplay}.{name}", eventAccessors),
        };

        IEnumerable<ReferencedTypeScanner.ReferencedMember> Named(string accessorName) =>
            members.Where(m => !m.IsField && m.Name == accessorName);
        ReferencedMemberGroup Method(ReferencedTypeScanner.ReferencedMember method) =>
            new(SymbolCategory.Method, $"{typeDisplay}.{Signature(method)}", [method]);
        string List(IEnumerable<ReferencedTypeScanner.ReferencedMember> overloads) =>
            string.Join(", ", overloads.Select(m => $"{typeDisplay}.{Signature(m)}"));
    }

    private static string Signature(ReferencedTypeScanner.ReferencedMember method) =>
        $"{method.Name}({string.Join(", ", method.ParameterTypes.Select(ReferenceFormatter.FormatTypeRef))})";

    /// <summary>
    /// True if the method satisfies the hint: its parameters match the type patterns position
    /// for position, or else it has the stated number of parameters.
    /// </summary>
    private static bool SatisfiesHint(ReferencedTypeScanner.ReferencedMember method, int? parameter_count,
        string[] parameter_types) =>
        parameter_types != null
            ? method.ParameterTypes.Count == parameter_types.Length
              && method.ParameterTypes.Zip(parameter_types).All(pair => TypeMatcher.Matches(pair.First, pair.Second))
            : method.ParameterTypes.Count == parameter_count;

    /// <summary>
    /// Why a member name the assembly references nothing by finds nothing. It names the members
    /// of the type the assembly does reference, since no other tool can list them and a
    /// misspelled or miscased name would otherwise read as an unused member.
    /// </summary>
    private static string DescribeMissingMember(ReferencedTypeScanner scan,
        IReadOnlyList<ReferencedTypeScanner.ReferencedMember> members, SymbolResolver.ExternalTypeName type,
        string member_name)
    {
        if (members.Count == 0)
            return scan.IsReferenced || scan.UsedBy().Count > 0
                ? $"This assembly references no member of {type.Display}."
                : NotReferenced;
        var names = members.Select(m => MemberNameOf(m.Name)).Distinct().Order(StringComparer.Ordinal);
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

    /// <summary>
    /// True if <paramref name="type"/> is <c>System.Attribute</c> or derives from it.
    /// <c>GetAllBaseTypeDefinitions</c> includes the type itself plus every supertype.
    /// </summary>
    private static bool IsAttribute(ITypeDefinition type) =>
        type.GetAllBaseTypeDefinitions().Any(t => t.FullName == "System.Attribute");
}
