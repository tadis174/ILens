using System.ComponentModel;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class AnalyzeTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "analyze", ReadOnly = true),
     Description("Run cross-reference analysis on a type or member. " +
        "Omit memberName to analyze the type itself.\n" +
        "Valid kinds by symbol category:\n" +
        "  Type:     UsedBy, InstantiatedBy, ExposedBy, ExtensionMethods, AppliedTo, ImplementedBy\n" +
        "  Method:   UsedBy, OverriddenBy, ImplementedBy, Uses, Implements\n" +
        "  Property: UsedBy, ReadBy, AssignedBy, Uses, OverriddenBy, ImplementedBy\n" +
        "  Field:    ReadBy, AssignedBy\n" +
        "  Event:    UsedBy, OverriddenBy, ImplementedBy\n" +
        "UsedBy / ReadBy / AssignedBy / Uses on a property route to its accessors — ReadBy to " +
        "the getter, AssignedBy to the setter, UsedBy to both, and Uses to both for the " +
        "outgoing direction (what the accessor bodies call). UsedBy on an event covers its add, " +
        "remove, and invoke accessors plus, for a field-like event, the field holding the " +
        "subscriber list — so raise sites are reported and not just subscribers. The result " +
        "header names what was consulted. ReadBy on a write-only property, or AssignedBy on a " +
        "read-only one, errors rather than returning an empty result.\n" +
        "AppliedTo requires an Attribute-derived type — it errors on a non-attribute type. " +
        "ImplementedBy on a type requires an interface — it errors on a non-interface type.")]
    public static string Analyze(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.String' or 'System.IO.File'.")] string typeName,
        [Description("Analysis kind. See tool description for which kinds apply to which symbol category.")] AnalysisKind kind,
        [Description("Member name. Omit to analyze the type itself.")] string memberName = null,
        [Description("Method overload disambiguator (only relevant when memberName resolves to a method). If parameterTypes is also given, the two must agree.")] int? parameterCount = null,
        [Description("Ordered parameter-type patterns to disambiguate same-arity method overloads (only relevant when memberName resolves to a method). Same loose matching as find_methods.")] string[] parameterTypes = null,
        [Description("Cap on result lines. Default 50.")] int? limit = null)
    {
        var host = registry.GetOrLoad(assembly);
        var resolver = host.Resolver;
        var type = resolver.ResolveType(typeName);

        // Default to type-level analysis; member resolution overrides if memberName is set.
        // IsNullOrEmpty (not just `is null`) so an LLM passing "" for "omit" works as expected.
        ISymbol symbol = type;
        var category = SymbolCategory.Type;
        var originSuffix = "";
        if (!string.IsNullOrEmpty(memberName))
        {
            var (resolved, origin, resolvedCategory) =
                resolver.ResolveMember(type, memberName, parameterCount, parameterTypes);
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
                $"Analysis kind 'AppliedTo' requires an attribute type; " +
                $"{type.FullName} does not derive from System.Attribute.");

        // ImplementedBy on a type only makes sense for an interface. The same empty-result
        // ambiguity as AppliedTo applies — "implementers of System.String" would silently
        // return nothing — and the precondition is again a crisp kind check.
        if (kind == AnalysisKind.ImplementedBy && category == SymbolCategory.Type
            && type.Kind != TypeKind.Interface)
            throw new ArgumentException(
                $"Analysis kind 'ImplementedBy' on a type requires an interface; " +
                $"{type.FullName} has kind {type.Kind}.");

        var header = AnalysisDispatch.HeaderFor(kind);
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
            results = host.RunAnalyzer(header, symbol);
        }

        var display = ReferenceFormatter.FormatSymbol(symbol);

        return $"{header} — {display}{originSuffix}{viaSuffix}:\n" +
            ReferenceFormatter.FormatSymbolList(results, limit ?? DefaultLimit);
    }

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
        /// <see cref="ReferenceFormatter.FormatSymbolList"/> collapses the duplicate lines.
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
                .SelectMany(step =>
                    host.RunAnalyzer(AnalysisDispatch.HeaderFor(step.Kind), step.Symbol))
                .Where(result => !self.Contains(ReferenceFormatter.FormatSymbol(result)))
                .OrderBy(ReferenceFormatter.FormatSymbol, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// How a usage question about a property or event is answered, or <c>null</c> when an
    /// ILSpyX analyzer answers the (symbol, kind) pair directly.
    /// </summary>
    /// <remarks>
    /// ILSpyX exports these analyzers for methods, types, and fields only — "Used By" via
    /// <c>MethodUsedByAnalyzer</c> / <c>MethodVirtualUsedByAnalyzer</c> / <c>TypeUsedByAnalyzer</c>,
    /// "Read By" and "Assigned By" via the two field-access analyzers, and "Uses" via
    /// <c>MethodUsesAnalyzer</c>. A property or event therefore has a native route in neither
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
    /// one is never read, so "(no results)" would be true but unreadable — indistinguishable
    /// from a property that simply has no call sites, which is the opposite conclusion.
    /// </summary>
    private static IMethod RequireAccessor(IMethod accessor, IProperty property,
        AnalysisKind kind, string role, string shape) =>
        accessor ?? throw new ArgumentException(
            $"Analysis kind '{kind}' on a property resolves to its {role}, and " +
            $"{property.DeclaringType.FullName}.{property.Name} is {shape}. " +
            "Use kind 'UsedBy' for every call site of the property.");

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
                $"{member.DeclaringType.FullName}.{member.Name} declares no accessors, " +
                "so it has no call sites to analyze.");
        return present;
    }

    private static void ValidateKind(AnalysisKind kind, SymbolCategory category)
    {
        if (AnalysisDispatch.AppliesTo(kind).Contains(category))
            return;

        var valid = string.Join(", ", AnalysisDispatch.KindsFor(category));
        throw new ArgumentException(
            $"Analysis kind '{kind}' is not valid for {category}. " +
            $"Valid kinds for {category}: {valid}.");
    }

    /// <summary>
    /// True if <paramref name="type"/> is <c>System.Attribute</c> or derives from it.
    /// <c>GetAllBaseTypeDefinitions</c> includes the type itself plus every supertype.
    /// </summary>
    private static bool IsAttribute(ITypeDefinition type) =>
        type.GetAllBaseTypeDefinitions().Any(t => t.FullName == "System.Attribute");
}
