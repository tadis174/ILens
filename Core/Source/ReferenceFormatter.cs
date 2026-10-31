using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using SyntaxIdentifier = ICSharpCode.Decompiler.CSharp.Syntax.Identifier;

namespace ILens;

/// <summary>
/// Renders symbols and types as call-site references — fully qualified names,
/// no accessibility/modifiers. Used by analyze tool output.
/// For declaration-style signatures see <see cref="SignatureFormatter"/>.
/// </summary>
public static class ReferenceFormatter
{
    /// <summary>
    /// Format a single symbol to its display name.
    /// </summary>
    public static string FormatSymbol(ISymbol symbol) => FormatSymbol(symbol, full: false);

    /// <summary>
    /// <see cref="FormatSymbol(ISymbol)"/>, with <paramref name="full"/> every type in it written
    /// in full (see <see cref="FormatTypeRef(IType, bool)"/>).
    /// </summary>
    public static string FormatSymbol(ISymbol symbol, bool full)
    {
        return symbol switch
        {
            IMethod method => FormatMethodReference(method, full),
            IProperty prop => $"{FormatTypeName(prop.DeclaringType, full)}.{prop.Name}{FormatIndices(prop, full)}",
            IField field => $"{FormatTypeName(field.DeclaringType, full)}.{field.Name}",
            IEvent evt => $"{FormatTypeName(evt.DeclaringType, full)}.{evt.Name}",
            IType type => FormatTypeName(type, full),
            MethodUseScanner.UnresolvedType type => FormatTypeName(type.FullName),
            MethodUseScanner.CompilerUse use => FormatSymbol(use.Symbol, full),
            GlobalAttributeTarget target => $"{target.Keyword} {target.Name}",
            IEntity entity => entity.FullName,
            _ => symbol.ToString() ?? "(unknown)"
        };
    }

    /// <summary>
    /// The text each of <paramref name="items"/> is written as among the others: its short form,
    /// or its full form where another item shares the short form and not the full one, as two
    /// classes of one name in two namespaces do. So no two lines of an answer read alike for
    /// different members, and every other line keeps the form that costs a reader fewer tokens.
    /// Items with the same full form are taken for one member, as a list that merges lines
    /// alike merges them. The full form is only made for items whose short form another shares.
    /// </summary>
    /// <remarks>
    /// The rule is the one Roslyn's own diagnostics follow, which qualify two symbols only where
    /// they would display alike (its <c>SymbolDistinguisher</c>).
    /// </remarks>
    public static Func<T, string> Distinguishing<T>(IEnumerable<T> items, Func<T, string> shortForm,
        Func<T, string> fullForm)
    {
        var alike = items
            .GroupBy(shortForm, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any() && group.Select(fullForm).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return item =>
        {
            var text = shortForm(item);
            return alike.Contains(text) ? fullForm(item) : text;
        };
    }

    /// <summary>
    /// The texts the two sides of one line are written as, such as a member's declaration in A and
    /// in B: their short forms, or both their full forms where the short forms read alike and the
    /// full ones do not, or where a name in them stands for two different types
    /// (<see cref="ShortFormHidesNamesakes"/>), as when a class gave way to one of the same name in
    /// another namespace beside another change. That test reads <paramref name="tested"/>'s text
    /// for each side, where one is given: the types its short form writes by their short names, in
    /// full, without what the line writes in full in either form, such as the member's own name.
    /// Otherwise it reads the full forms.
    /// </summary>
    public static (string A, string B) Sides<T>(T a, T b, Func<T, string> shortForm, Func<T, string> fullForm,
        Func<T, string> tested = null)
    {
        var (aShort, bShort) = (shortForm(a), shortForm(b));
        var (aFull, bFull) = (fullForm(a), fullForm(b));
        tested ??= fullForm;
        return (aShort == bShort && aFull != bFull) || ShortFormHidesNamesakes(tested(a), tested(b))
            ? (aFull, bFull)
            : (aShort, bShort);
    }

    /// <summary>
    /// <paramref name="line"/> without <paramref name="name"/>, a member's own name, where the line
    /// writes it as one qualified name: the name of an explicit interface implementation, which a
    /// declaration writes after its interface in either form, <c>Shop.IPainted.Color</c>, and which
    /// would otherwise count as a type of the short name <c>Color</c>. A name the line does not
    /// write that way, a plain one or one after a generic interface, is left as it stands. The name
    /// is looked for as the line writes it (<see cref="FormatIdentifier"/>), <c>Shop.IOps.@do</c>.
    /// </summary>
    public static string WithoutName(string line, string name)
    {
        var written = FormatIdentifier(name);
        return QualifiedName.Replace(line, match => match.Value == written ? "" : match.Value);
    }

    /// <summary>
    /// Whether the short form of <paramref name="fullLines"/> would let one name stand for two
    /// different types (<see cref="NamesakesAmong"/>). Two declarations that also differ in another
    /// part would then read as if that type stayed the same, which <see cref="Distinguishing{T}"/>,
    /// comparing whole lines, does not see.
    /// </summary>
    public static bool ShortFormHidesNamesakes(params string[] fullLines) => NamesakesAmong(fullLines).Count > 0;

    /// <summary>
    /// The qualified names <paramref name="fullLines"/> hold whose name, with its count of type
    /// arguments, ends another qualified name among them, as <c>Origin</c> ends
    /// <c>Shop.Alpha.Origin</c> and <c>Shop.Beta.Origin</c>, where <c>IEnumerable&lt;T&gt;</c> and
    /// <c>IEnumerable</c> read apart: the types the short form of those lines would write alike.
    /// The test reads the text, so a name that only the classes enclosing it tell apart counts as
    /// well, and a line that holds it is written in full where its short form would have read
    /// apart. Each name carries its count of type arguments after a backtick
    /// (<see cref="QualifiedNames"/>).
    /// </summary>
    public static IReadOnlySet<string> NamesakesAmong(IEnumerable<string> fullLines) =>
        fullLines
            .SelectMany(QualifiedNames)
            .Distinct(StringComparer.Ordinal)
            .GroupBy(name => name[(name.LastIndexOf('.') + 1)..], StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .SelectMany(group => group)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="fullLine"/> names one of <paramref name="namesakes"/>, the types
    /// <see cref="NamesakesAmong"/> found among the lines it is read with.
    /// </summary>
    public static bool NamesOneOf(string fullLine, IReadOnlySet<string> namesakes) =>
        namesakes.Count > 0 && QualifiedNames(fullLine).Any(namesakes.Contains);

    /// <summary>
    /// The qualified names <paramref name="line"/> holds, each with the count of the type
    /// arguments that follow it after a backtick, as <c>System.Collections.Generic.IEnumerable`1</c>.
    /// </summary>
    private static IEnumerable<string> QualifiedNames(string line)
    {
        foreach (Match match in QualifiedName.Matches(line))
        {
            var end = match.Index + match.Length;
            var count = 0;
            if (end < line.Length && line[end] == '<')
            {
                count = 1;
                for (int i = end, depth = 0; i < line.Length; i++)
                {
                    if (line[i] == '<') depth++;
                    else if (line[i] == '>' && --depth == 0) break;
                    else if (line[i] == ',' && depth == 1) count++;
                }
            }
            yield return $"{match.Value}`{count}";
        }
    }

    // A segment may begin with the '@' a declaration writes before a keyword (FormatIdentifier).
    private static readonly Regex QualifiedName = new(@"@?[A-Za-z_]\w*(?:\.@?[A-Za-z_]\w*)+", RegexOptions.Compiled);

    /// <summary>
    /// <see cref="Distinguishing{T}"/> for symbols named by <see cref="FormatSymbol"/>: the name
    /// each of <paramref name="symbols"/> is written by on a line of one answer.
    /// </summary>
    public static Func<ISymbol, string> NamesFor(IEnumerable<ISymbol> symbols) =>
        Distinguishing(symbols, FormatSymbol, symbol => FormatSymbol(symbol, full: true));

    /// <summary>
    /// The name <paramref name="member"/> is written by where an answer names it alone, as the
    /// member it was asked about: <paramref name="format"/>'s short form, or its full form where
    /// one of <paramref name="namesakes"/>, the members of its name the lookup sees, reads alike
    /// in the short form and not in the full one.
    /// </summary>
    public static string NameAmong<T>(T member, IEnumerable<T> namesakes, Func<T, bool, string> format) =>
        Distinguishing(namesakes.Append(member), item => format(item, false), item => format(item, true))(member);

    /// <summary>
    /// Symbols as a comma-joined list in an error or a line, each named as
    /// <see cref="NamesFor"/> names it among the others.
    /// </summary>
    public static string FormatList(IEnumerable<ISymbol> symbols)
    {
        var list = symbols.ToList();
        var name = NamesFor(list);
        return string.Join(", ", list.Select(name));
    }

    /// <summary>
    /// An indexer's index types in brackets, <c>[int, int]</c>, and nothing for any other
    /// property. Several indexers share one name, <c>Item</c> unless <c>[IndexerName]</c>
    /// renames it, so the name alone does not say which is meant. The test is
    /// <c>Parameters.Count</c>, not <c>IsIndexer</c>, which ILSpy sets only for the name the
    /// declaring type's default member attribute gives.
    /// </summary>
    public static string FormatIndices(IProperty property, bool full = false) =>
        property.Parameters.Count == 0
            ? ""
            : $"[{string.Join(", ", property.Parameters.Select(parameter => FormatParameterType(parameter, full)))}]";

    /// <summary>
    /// Format a method as a fully-qualified call-site reference with its parameter
    /// types in parentheses, e.g. <c>System.String.IndexOf(char, int)</c>, and a generic
    /// method's type parameters after its name, <c>HarmonyLib.Traverse.Field&lt;T&gt;(string)</c>.
    /// Uses C# keywords for well-known framework types and unfolds generics. A conversion
    /// operator is followed by its return type, <c>System.Decimal.op_Explicit(decimal) → int</c>:
    /// C# names one by the type it converts to, and a type may declare one for each such type,
    /// all of one name and parameter, as <c>System.Decimal</c> declares eleven.
    /// </summary>
    public static string FormatMethodReference(IMethod method) => FormatMethodReference(method, full: false);

    /// <summary>
    /// <see cref="FormatMethodReference(IMethod)"/>, with <paramref name="full"/> every type in it
    /// written in full.
    /// </summary>
    public static string FormatMethodReference(IMethod method, bool full) =>
        FormatMethodReference(method, full, withReturnType: IsConversionOperator(method.Name));

    /// <summary>
    /// <see cref="FormatMethodReference(IMethod, bool)"/>, followed by the return type for every
    /// method with <paramref name="withReturnType"/>, and for none without it.
    /// </summary>
    public static string FormatMethodReference(IMethod method, bool full, bool withReturnType)
    {
        var parameters = string.Join(", ", method.Parameters.Select(parameter => FormatParameterType(parameter, full)));
        var reference = $"{FormatTypeName(method.DeclaringType, full)}.{method.Name}{FormatTypeArguments(method, full)}({parameters})";
        return withReturnType ? $"{reference} → {FormatReturnType(method, full)}" : reference;
    }

    /// <summary>
    /// True for the name of a conversion operator, <c>op_Implicit</c>, <c>op_Explicit</c> or
    /// <c>op_CheckedExplicit</c>, also behind the interface name an explicit implementation's
    /// name starts with.
    /// </summary>
    public static bool IsConversionOperator(string methodName) =>
        methodName[(methodName.LastIndexOf('.') + 1)..] is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit";

    /// <summary>
    /// A generic method's type parameters in angle brackets, as C# declares them, <c>&lt;T&gt;</c>,
    /// or for a method made for type arguments those arguments, <c>&lt;int&gt;</c>; nothing for
    /// a method that is not generic. A method and a generic method of one name and parameters
    /// would otherwise print alike, as would a method made for two type arguments, and the
    /// parameter list would name a type parameter declared nowhere.
    /// </summary>
    public static string FormatTypeArguments(IMethod method, bool full = false)
    {
        var count = method.TypeParameters.Count;
        if (count == 0)
            return "";
        IReadOnlyList<IType> arguments = method.TypeArguments.Count == count ? method.TypeArguments : method.TypeParameters;
        return $"<{string.Join(", ", arguments.Select(argument => FormatTypeRef(argument, full)))}>";
    }

    /// <summary>
    /// A line saying that code can call <paramref name="implemented"/>, the interface members a
    /// member implements, in the member's place, or <c>null</c> when there are none. A call
    /// through an interface names the interface's member and reaches the implementation at run
    /// time. An answer listing what names the implementation can therefore be empty however
    /// often that happens, and without this line it would read as proof of disuse. Those calls
    /// are not added to the answer, since each could as well reach another implementation of
    /// the interface.
    /// </summary>
    /// <remarks>
    /// The line says "can call" for every implementation, the one C# names after the interface
    /// included: code names that one too, where the compiler has an iterator's non-generic
    /// GetEnumerator call the generic one, and a member that implements under a name of its
    /// own, as in VB, is called by that name like any other.
    /// </remarks>
    public static string FormatImplementationRoute(IEnumerable<IMember> implemented)
    {
        var members = implemented.ToList();
        var name = NamesFor(members);
        var names = members.Select(name).Distinct().Order(StringComparer.Ordinal).ToList();
        return names.Count switch
        {
            0 => null,
            1 => $"Implementation: code can call {names[0]} instead; ask about that member for its uses.",
            _ => $"Implementation: code can call {string.Join(", ", names)} instead; ask about those members for their uses.",
        };
    }

    /// <summary>
    /// A line saying that code can call <paramref name="member"/> through the declaration it
    /// overrides, or <c>null</c> for a member that overrides nothing. C# compiles a call to a
    /// virtual member as a call to the declaration that introduced its slot rather than to an
    /// override (<see cref="SymbolResolver.SlotDeclarationOf"/>), so an answer listing only what
    /// names the override can be empty however often code reaches it, and would read as proof
    /// of disuse. The line says "can call": a compiler also names the override in a virtual
    /// call, a C# compiler for a method with a type parameter of its own and for an override
    /// with a more specific return type, and those calls are listed above the line. With
    /// <paramref name="listsCalls"/>, the answer holds those calls whose receiver's type is the
    /// override's class or derives from it, the calls the decompiled C# shows as calls to the
    /// override; otherwise it holds only what names the override, and the line points to
    /// call_sites for those calls. Those calls can be counted only when the assembly defines
    /// that declaration.
    /// </summary>
    public static string FormatOverrideRoute(IMember member, bool listsCalls)
    {
        if (!Overriding.Of(member))
            return null;

        var slot = SymbolResolver.SlotDeclarationOf(member);
        if (Overriding.Of(slot))
        {
            var from = member.DeclaringTypeDefinition is { } declaringType
                && NearestExternalBase(declaringType) is { } external
                ? $"the {FormatTypeName(external)} member"
                : "the member";
            return $"Override: code can call {from} it overrides instead, which another assembly defines, " +
                (listsCalls ? "so those calls are not counted here." : "so only references to this override are listed.");
        }

        var receivers = $"those calls whose receiver's type is {FormatTypeName(member.DeclaringType)} or derives from it";
        if (listsCalls)
            return $"Override: code can call {FormatSymbol(slot)} instead; listed are {receivers}.";

        IMethod[] accessors = member switch
        {
            IProperty property => [property.Getter, property.Setter],
            IEvent @event => [@event.AddAccessor, @event.RemoveAccessor],
            _ => [],
        };
        var declared = accessors.Where(accessor => accessor != null).Select(accessor => accessor.Name).ToList();
        var on = declared.Count > 0 ? " on " + string.Join(" and ", declared) : "";
        return $"Override: code can call {FormatSymbol(slot)} instead, so only references to this override are listed; " +
            $"'{AnalysisKind.CallSites.ToSchemaName()}'{on} lists {receivers}.";
    }

    /// <summary>
    /// The nearest class <paramref name="type"/> derives from that its assembly does not define,
    /// or <c>null</c> when every base class is the assembly's own.
    /// </summary>
    /// <remarks>
    /// A class's direct base types name the class it derives from first and its interfaces
    /// after it, so the walk takes only the first. An interface from an assembly ILens does not
    /// load is an unknown type, which nothing marks as an interface.
    /// </remarks>
    private static IType NearestExternalBase(ITypeDefinition type)
    {
        const int maxDepth = 64;  // a longer chain is taken for a cycle in malformed metadata
        var current = type;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var baseClass = current.DirectBaseTypes.FirstOrDefault();
            if (baseClass is null || baseClass.Kind == TypeKind.Interface)
                return null;
            if (baseClass.GetDefinition() is not { } definition || definition.ParentModule != type.ParentModule)
                return baseClass;
            current = definition;
        }
        return null;
    }

    /// <summary>
    /// A parameter's type with the keyword C# passes it by, <c>ref int</c>, <c>out int</c>,
    /// <c>in int</c> or <c>ref readonly int</c>, and the type alone for one passed by value.
    /// </summary>
    public static string FormatParameterType(IParameter parameter) => FormatParameterType(parameter, full: false);

    /// <inheritdoc cref="FormatParameterType(IParameter)"/>
    public static string FormatParameterType(IParameter parameter, bool full) =>
        PassingKeyword(ParameterPassing.Of(parameter)) + FormatTypeRef(parameter.Type, full);

    /// <summary>
    /// The keyword a parameter is passed by, with the space after it, and nothing for one
    /// passed by value. A parameter whose keyword is not known
    /// (<see cref="ParameterPassing.KeywordIsUnknown"/>) is written <c>ref</c>.
    /// </summary>
    public static string PassingKeyword(ParameterPassing passing) =>
        passing.KeywordIsUnknown ? "ref " : RefKeyword(passing.Keyword);

    /// <summary>
    /// A method's or property's return type as C# declares it: <c>ref T</c> or
    /// <c>ref readonly T</c> for one that returns by reference, the type alone otherwise.
    /// </summary>
    public static string FormatReturnType(IMember member, bool full = false) =>
        ReturnKeyword(member) + FormatTypeRef(member.ReturnType, full);

    /// <summary>
    /// The keyword a method or property returns by, with the space after it: <c>ref</c> or
    /// <c>ref readonly</c> for one that returns by reference, and nothing otherwise.
    /// </summary>
    public static string ReturnKeyword(IMember member)
    {
        var type = member.ReturnType;
        while (type is ModifiedType modified)
            type = modified.ElementType;
        if (type is not ByReferenceType)
            return "";
        var readOnly = member switch
        {
            IMethod method => method.ReturnTypeIsRefReadOnly,
            IProperty property => property.ReturnTypeIsRefReadOnly,
            _ => false,
        };
        return readOnly ? "ref readonly " : "ref ";
    }

    /// <summary>
    /// A type's fully qualified name with each generic segment's type parameters: a definition
    /// prints them unbound, as in <c>typeof</c> — <c>System.Func&lt;,&gt;</c>,
    /// <c>System.Collections.Generic.Dictionary&lt;,&gt;.Enumerator</c> — and a constructed
    /// type with its arguments, each written as <see cref="FormatTypeRef"/> writes a parameter
    /// type, <c>System.Collections.Generic.List&lt;int&gt;</c>, so that a line names a type
    /// argument and a parameter type alike. Every name <see cref="ParsedTypeName"/> reads back,
    /// which counts the arguments and does not resolve them.
    /// </summary>
    /// <remarks>
    /// Print type names through this rather than <c>FullName</c>, which drops the arity and so
    /// names <c>Task</c> and <c>Task&lt;TResult&gt;</c> alike: a caller who passes such a name
    /// back gets whichever type the lookup prefers, not necessarily the one printed.
    /// </remarks>
    public static string FormatTypeName(IType type) => FormatTypeName(type, TypeForm.Short);

    /// <summary>
    /// <see cref="FormatTypeName(IType)"/>, with <paramref name="full"/> its type arguments written
    /// in full as well (see <see cref="FormatTypeRef(IType, bool)"/>).
    /// </summary>
    public static string FormatTypeName(IType type, bool full) => FormatTypeName(type, full ? TypeForm.Full : TypeForm.Short);

    /// <summary>
    /// A type as a comparison of two assemblies tells it apart: the full name
    /// <see cref="FormatTypeName(IType)"/> writes, with its type arguments written in full as
    /// well, and with a type parameter written by its owner
    /// and position, <c>`0</c> for a class's first and <c>``0</c> for a method's, and without
    /// the modifiers C# writes as a keyword, which <see cref="FormatTypeRef"/> leaves out as
    /// well. Types the short form writes alike differ here: two classes of one name in two
    /// namespaces or in two enclosing classes, and a method's type parameter and its class's of
    /// one name. A renamed type parameter does not. Every type argument of a construction is
    /// written, also those of a class whose name does not count them, which a name written
    /// through <see cref="FormatSegments"/> leaves out: without them, a field whose type is such
    /// a class with arguments in one build and without them in the other would be one member.
    /// ILSpy's <c>ReflectionName</c> would not do: a function pointer's is <c>delegate*</c>
    /// without its signature, and it keeps the keyword modifiers, such as the
    /// <c>modreq(InAttribute)</c> of an <c>in</c> parameter of a virtual method.
    /// </summary>
    public static string FormatTypeIdentity(IType type) => FormatTypeName(type, TypeForm.Identity);

    /// <summary>
    /// How <see cref="FormatTypeName(IType, TypeForm)"/> writes a type's arguments: as parameter
    /// types are written, in full, or as a comparison of two assemblies tells them apart.
    /// </summary>
    private enum TypeForm { Short, Full, Identity }

    private static string FormatTypeName(IType type, TypeForm form)
    {
        var identity = form == TypeForm.Identity;
        string Format(IType inner) => FormatTypeName(inner, form);
        switch (type)
        {
            case ParameterizedType constructed:
                var arguments = constructed.TypeArguments
                    .Select(argument => form switch
                    {
                        TypeForm.Identity => Format(argument),
                        TypeForm.Full => FormatTypeRef(argument, full: true),
                        _ => FormatTypeRef(argument),
                    })
                    .ToList();
                return FullTypeNameOf(constructed.GenericType) is { } generic
                    ? FormatSegments(generic, arguments, qualified: true, uncounted: identity)
                    : $"{constructed.GenericType.FullName}<{string.Join(", ", arguments)}>";
            case TupleType tuple:
                return Format(tuple.UnderlyingType);
            case ArrayType:
                return FormatArray(type, Format);
            case ByReferenceType byRef:
                return Format(byRef.ElementType);
            case ModifiedType modified when identity && IsKeywordModifier(modified.Modifier):
                return Format(modified.ElementType);
            case TypeWithElementType decorated:
                return Format(decorated.ElementType) + decorated.NameSuffix;
            case FunctionPointerType functionPointer:
                return FormatFunctionPointer(functionPointer, Format);
            case ITypeParameter typeParameter:
                return identity ? typeParameter.ReflectionName : typeParameter.Name;
        }
        return FullTypeNameOf(type) is { } name
            ? FormatTypeName(name)
            : type.FullName;
    }

    /// <summary>
    /// A named type's name as <see cref="FormatTypeName(IType)"/> prints its definition, for a
    /// type known only by name, such as one the assembly references but does not define.
    /// </summary>
    public static string FormatTypeName(FullTypeName name) =>
        FormatSegments(name, null, qualified: true);

    /// <summary>
    /// A named type's name with its nesting and the arity of each level: from its definition,
    /// or for a type in an assembly ILens does not load, from the reference to it. Null for a
    /// type without such a name, such as a type parameter or an array.
    /// </summary>
    /// <remarks>
    /// Code with nullable annotations wraps a reference type in its annotation, which hides a
    /// type from an unloaded assembly from both lookups, so the annotation is dropped first.
    /// </remarks>
    public static FullTypeName? FullTypeNameOf(IType type)
    {
        type = type.ChangeNullability(Nullability.Oblivious);
        return (type.GetDefinition() ?? type as ITypeDefinitionOrUnknown)?.FullTypeName;
    }

    /// <summary>
    /// <paramref name="name"/> segment by segment, with each segment's own type parameters
    /// taken in order from <paramref name="arguments"/>, or left unbound when there are none.
    /// <paramref name="qualified"/> puts the namespace in front, and <paramref name="uncounted"/>
    /// writes the arguments the segments do not count after the innermost one, in braces.
    /// </summary>
    /// <remarks>
    /// A segment's count of type parameters is the one its name carries, as ILSpy reads it from
    /// the backtick suffix. The F# compiler names the classes it generates without that suffix,
    /// so such a class counts none while it has some, and its arguments are written only where
    /// <paramref name="uncounted"/> asks for them. The braces keep such a construction apart from
    /// one of a class whose name counts the same arguments, X`2 beside X, which the runtime takes
    /// for two classes. A name printed through here leaves them out: the class's name without
    /// them is the one a type lookup finds, since a name written with arguments is read as
    /// counting them.
    /// </remarks>
    private static string FormatSegments(FullTypeName name, IReadOnlyList<string> arguments, bool qualified,
        bool uncounted = false)
    {
        var top = name.TopLevelTypeName;
        var text = new System.Text.StringBuilder();
        if (qualified && !string.IsNullOrEmpty(top.Namespace))
            text.Append(top.Namespace).Append('.');
        var used = 0;
        AppendSegment(top.Name, top.TypeParameterCount);
        for (var level = 0; level < name.NestingLevel; level++)
        {
            text.Append('.');
            AppendSegment(name.GetNestedTypeName(level), name.GetNestedTypeAdditionalTypeParameterCount(level));
        }
        if (uncounted && arguments != null && used < arguments.Count)
            text.Append('{').AppendJoin(", ", arguments.Skip(used)).Append('}');
        return text.ToString();

        void AppendSegment(string segment, int arity)
        {
            text.Append(segment);
            if (arity == 0)
                return;
            text.Append('<');
            if (arguments == null)
                text.Append(',', arity - 1);
            else
                text.AppendJoin(", ", arguments.Skip(used).Take(arity));
            text.Append('>');
            used += arity;
        }
    }

    /// <summary>
    /// Format a list of methods as comma-joined call-site references, each named as
    /// <see cref="NamesFor"/> names it among the others. Used by <see cref="SymbolResolver"/> to
    /// list overload candidates in error messages.
    /// </summary>
    public static string FormatMethodList(IEnumerable<IMethod> methods) => FormatList(methods);

    /// <summary>
    /// Format a resolved method as a full signature — parameter types and return type —
    /// anchored on a caller-supplied declaring-type name, e.g.
    /// <c>Shapes.Circle.Equals(Circle) → bool</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="FormatMethodReference"/> this does not use the method's own
    /// <c>DeclaringType</c>: <c>decompile</c> anchors its header on the type the
    /// caller asked about, or, for an extension method, on the class that declares it, which the
    /// overload that takes the asked type chooses, and reports an inherited or extension origin
    /// separately. The
    /// return type is included because it is part of what distinguishes the overload that
    /// resolution actually picked — a bare <c>// Shapes.Circle.Equals</c> header is ambiguous
    /// by construction, and would let a caller mistake one overload for another.
    /// </remarks>
    public static string FormatResolvedSignature(IMethod method, string declaringTypeName, bool full = false) =>
        $"{declaringTypeName}.{method.Name}{FormatSignatureTypes(method, full)}";

    /// <summary>
    /// What <see cref="FormatResolvedSignature(IMethod, string, bool)"/> writes after a method's
    /// name, <c>&lt;int&gt;(string, Badge) → bool</c>: the type arguments of a method made for
    /// them, the parameter types and the return type, the part of the line that names types by
    /// their short names in its short form.
    /// </summary>
    public static string FormatSignatureTypes(IMethod method, bool full)
    {
        var parameters = string.Join(", ", method.Parameters.Select(parameter => FormatParameterType(parameter, full)));
        return $"{FormatTypeArguments(method, full)}({parameters}) → {FormatReturnType(method, full)}";
    }

    /// <summary>
    /// <see cref="FormatResolvedSignature(IMethod, string, bool)"/> for a method found on
    /// <paramref name="askedType"/>: anchored on that type, which a member it inherits is named
    /// under as well, and for an extension method on the class that declares it. The type an
    /// extension method extends has no method of that signature, so a line anchored there,
    /// <c>Shop.Till.Open(Till)</c>, would name a method that does not exist. Without an asked
    /// type, as where compare names the other build's method beside an extension method found
    /// through a type whose library was not read, a method is anchored on its own class.
    /// </summary>
    public static string FormatResolvedSignature(IMethod method, ITypeDefinition askedType, bool full = false) =>
        FormatResolvedSignature(method,
            FormatTypeName(method.IsExtensionMethod || askedType is null ? method.DeclaringType : askedType), full);

    /// <summary>
    /// What the mark of an extension method found through a type names, as in
    /// <c>[extension method on Shop.Till]</c>: the type the method extends
    /// (<see cref="TypeWalker.ExtendedType"/>), and a receiver that is a type parameter, or an
    /// array of one, with its conditions as a declaration writes them
    /// (<see cref="FormatConditions"/>), the types qualified, <c>T where T : Shop.IShape</c> or
    /// <c>T[] where T : struct</c>. It says why the type asked through leads to the method, also
    /// where no code shows the conditions.
    /// </summary>
    public static string FormatExtendedType(IMethod extensionMethod)
    {
        var extended = TypeWalker.ExtendedType(extensionMethod);
        var element = extended;
        while (element is ArrayType array)
            element = array.ElementType;
        var written = FormatTypeName(extended);
        if (element is not ITypeParameter parameter)
            return written;

        var conditions = FormatConditions(parameter, FormatTypeName);
        return conditions.Count == 0
            ? written
            : $"{written} where {parameter.Name} : {string.Join(", ", conditions)}";
    }

    /// <summary>
    /// The conditions on <paramref name="parameter"/> as C# writes them in its where clause and the
    /// decompiled code shows them: class, struct, unmanaged or notnull first, then the types, each
    /// written by <paramref name="writeType"/>, then new() unless struct, and last
    /// <c>allows ref struct</c> (<see cref="TypeWalker.AllowsRefStruct"/>), where C# requires it.
    /// Neither <c>class?</c> nor a '?' on a type it names is written, as ILens writes no annotation
    /// of a reference type. Empty for a type parameter without conditions.
    /// </summary>
    public static List<string> FormatConditions(ITypeParameter parameter, Func<IType, string> writeType)
    {
        var conditions = new List<string>();
        if (parameter.HasReferenceTypeConstraint)
            conditions.Add("class");
        else if (parameter.HasValueTypeConstraint)
            conditions.Add(parameter.HasUnmanagedConstraint ? "unmanaged" : "struct");
        else if (parameter.NullabilityConstraint == Nullability.NotNullable)
            conditions.Add("notnull");
        conditions.AddRange(TypeWalker.WrittenConstraints(parameter).Select(writeType));
        if (parameter.HasDefaultConstructorConstraint && !parameter.HasValueTypeConstraint)
            conditions.Add("new()");
        if (TypeWalker.AllowsRefStruct(parameter))
            conditions.Add("allows ref struct");
        return conditions;
    }

    /// <summary>
    /// The where clauses a declaration ends with for <paramref name="parameters"/>, one for each
    /// type parameter with a condition and each after a space,
    /// <c> where T : class where U : struct, IShape</c>; empty where none has a condition.
    /// </summary>
    public static string FormatWhereClauses(IEnumerable<ITypeParameter> parameters, Func<IType, string> writeType) =>
        string.Concat(parameters
            .Select(parameter => (Name: FormatIdentifier(parameter.Name), Conditions: FormatConditions(parameter, writeType)))
            .Where(entry => entry.Conditions.Count > 0)
            .Select(entry => $" where {entry.Name} : {string.Join(", ", entry.Conditions)}"));

    /// <summary>
    /// Render an <see cref="IType"/> as a compact C#-style type reference,
    /// recursively unfolding generics and arrays. Uses C# keywords for the
    /// well-known framework types (e.g., <c>float</c>, <c>string</c>) and
    /// short names elsewhere, and C#'s form for a nullable value type, <c>int?</c> for
    /// <c>Nullable&lt;int&gt;</c>, and for an array, <c>int[,]</c> or <c>int[][,]</c> (see
    /// <see cref="FormatArray"/>), as the decompiled code writes them. A type nested in a generic
    /// type takes that type's arguments, so it is written with the types enclosing it, each
    /// carrying its own, as C# writes <c>Dictionary&lt;TKey, TValue&gt;.Entry</c>.
    /// </summary>
    /// <remarks>
    /// Every type built from other types is written with them, since they are what tells two
    /// such types apart: a pointer, a function pointer's signature, and the type a modifier
    /// is attached to. A reference is written as the type it refers to, and a modifier C# writes
    /// as a keyword, <c>ref readonly</c>, <c>in</c>, <c>volatile</c> or <c>init</c>, is left out:
    /// the keyword belongs to the parameter, return or member, and
    /// <see cref="FormatParameterType"/> and <see cref="FormatReturnType"/> write it. Any other
    /// modifier keeps its <c>modreq</c> or <c>modopt</c> form. A tuple is written as the
    /// <c>ValueTuple&lt;...&gt;</c> it is. ILSpy reads one as a tuple only where
    /// <c>System.ValueTuple</c> resolves, such as in an assembly that defines it, and as a
    /// constructed type elsewhere; writing the <c>ValueTuple&lt;...&gt;</c> makes both print alike.
    /// </remarks>
    public static string FormatTypeRef(IType type) => FormatTypeRef(type, full: false);

    /// <summary>
    /// <see cref="FormatTypeRef(IType)"/>, or with <paramref name="full"/> every named type in it
    /// written with its namespace and the types enclosing it, as
    /// <c>System.Collections.Generic.List&lt;Shop.Alpha.Widget&gt;</c>, where the short form
    /// writes <c>List&lt;Widget&gt;</c>. A type with a C# keyword keeps its keyword, which names
    /// one type, and a type parameter its name. An answer writes a line in full where two of its
    /// lines would otherwise read alike (<see cref="Distinguishing{T}"/>), and compare also where a
    /// short name in the lines it reads together would stand for two different types
    /// (<see cref="Sides{T}"/>, <see cref="NamesakesAmong"/>).
    /// </summary>
    public static string FormatTypeRef(IType type, bool full)
    {
        string Format(IType inner) => FormatTypeRef(inner, full);
        if (NullableUnderlying(type) is { } underlying)
        {
            return Format(underlying) + "?";
        }
        if (type is ParameterizedType pt)
        {
            var args = pt.TypeArguments.Select(Format).ToList();
            if (FullTypeNameOf(pt.GenericType) is { } name && (full || TakesEnclosingArguments(name)))
                return FormatSegments(name, args, qualified: full);
            return $"{TypeKeyword(pt.GenericType) ?? (full ? pt.GenericType.FullName : pt.GenericType.Name)}<{string.Join(", ", args)}>";
        }
        if (type is TupleType tuple)
        {
            return Format(tuple.UnderlyingType);
        }
        if (type is ArrayType)
        {
            return FormatArray(type, Format);
        }
        if (type is ByReferenceType byRef)
        {
            return Format(byRef.ElementType);
        }
        if (type is ModifiedType modified && IsKeywordModifier(modified.Modifier))
        {
            return Format(modified.ElementType);
        }
        if (type is TypeWithElementType decorated)
        {
            return Format(decorated.ElementType) + decorated.NameSuffix;
        }
        if (type is FunctionPointerType functionPointer)
        {
            return FormatFunctionPointer(functionPointer, Format);
        }
        if (TypeKeyword(type) is { } keyword)
            return keyword;
        return full && type is not ITypeParameter && FullTypeNameOf(type) is { } fullName
            ? FormatTypeName(fullName)
            : type.Name;
    }

    /// <summary>
    /// An array as C# writes it and the decompiled code shows it: its element written by
    /// <paramref name="format"/>, then a pair of brackets for each array around it, the outermost
    /// first, with a comma for each dimension past the first. So an array of two-dimensional
    /// arrays is <c>int[][,]</c>, which reflection writes <c>System.Int32[,][]</c>, innermost first:
    /// read as C#, that order names a two-dimensional array of arrays.
    /// </summary>
    private static string FormatArray(IType type, Func<IType, string> format)
    {
        var brackets = new System.Text.StringBuilder();
        while (type is ArrayType array)
        {
            brackets.Append('[').Append(',', array.Dimensions - 1).Append(']');
            type = array.ElementType;
        }
        return format(type) + brackets;
    }

    /// <summary>
    /// True for the modifiers C# writes as a keyword: <c>InAttribute</c> on an <c>in</c>
    /// parameter or a <c>ref readonly</c> return, <c>IsVolatile</c> on a <c>volatile</c> field,
    /// and <c>IsExternalInit</c> on an <c>init</c> accessor's return.
    /// </summary>
    private static bool IsKeywordModifier(IType modifier) =>
        (modifier.Namespace, modifier.Name) is ("System.Runtime.InteropServices", "InAttribute")
            or ("System.Runtime.CompilerServices", "IsVolatile")
            or ("System.Runtime.CompilerServices", "IsExternalInit");

    /// <summary>
    /// A function pointer as C# writes it, <c>delegate* unmanaged[Cdecl]&lt;ref int, void&gt;</c>:
    /// the calling convention, then the parameter types and last the return type, each written
    /// by <paramref name="format"/>.
    /// </summary>
    private static string FormatFunctionPointer(FunctionPointerType pointer, Func<IType, string> format)
    {
        var conventions = new List<string>();
        if (pointer.CallingConvention is not (SignatureCallingConvention.Default or SignatureCallingConvention.Unmanaged))
        {
            conventions.Add(pointer.CallingConvention switch
            {
                SignatureCallingConvention.CDecl => "Cdecl",
                SignatureCallingConvention.StdCall => "Stdcall",
                SignatureCallingConvention.ThisCall => "Thiscall",
                SignatureCallingConvention.FastCall => "Fastcall",
                SignatureCallingConvention.VarArgs => "Varargs",
                var other => other.ToString(),
            });
        }
        // C# names a convention by its CallConv type without the prefix: CallConvCdecl is Cdecl.
        conventions.AddRange(pointer.CustomCallingConventions.Select(convention =>
            convention.Name.StartsWith("CallConv", StringComparison.Ordinal) && convention.Name.Length > "CallConv".Length
                ? convention.Name["CallConv".Length..]
                : format(convention)));

        var unmanaged = pointer.CallingConvention == SignatureCallingConvention.Default ? ""
            : conventions.Count == 0 ? " unmanaged"
            : $" unmanaged[{string.Join(", ", conventions)}]";
        var parameters = pointer.ParameterTypes.Select((parameter, i) =>
            RefKeyword(pointer.ParameterReferenceKinds[i]) + format(parameter));
        var returnType = pointer.ReturnType is ByReferenceType
            ? RefKeyword(pointer.ReturnIsRefReadOnly ? ReferenceKind.RefReadOnly : ReferenceKind.Ref) + format(pointer.ReturnType)
            : format(pointer.ReturnType);
        return $"delegate*{unmanaged}<{string.Join(", ", parameters.Append(returnType))}>";
    }

    private static string RefKeyword(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Ref => "ref ",
        ReferenceKind.Out => "out ",
        ReferenceKind.In => "in ",
        ReferenceKind.RefReadOnly => "ref readonly ",
        _ => "",
    };

    /// <summary>
    /// The T of a <c>Nullable&lt;T&gt;</c>, or null for any other type. ILens writes such a type
    /// as C# does, <c>int?</c>, and reads that form back (<see cref="TypeMatcher"/>).
    /// </summary>
    public static IType NullableUnderlying(IType type) =>
        type is ParameterizedType { TypeArguments.Count: 1 } constructed
            && constructed.GenericType.FullName == "System.Nullable"
            ? constructed.TypeArguments[0]
            : null;

    private static bool TakesEnclosingArguments(FullTypeName name) =>
        name.NestingLevel > 0
        && name.TypeParameterCount > name.GetNestedTypeAdditionalTypeParameterCount(name.NestingLevel - 1);

    /// <summary>
    /// The C# keyword for a well-known framework type, or <c>null</c> if the type
    /// has no keyword form. Public so <see cref="TypeMatcher"/> can recognize keyword
    /// patterns the way this formatter renders them.
    /// </summary>
    /// <remarks>
    /// ILSpy reads most <c>System.IntPtr</c> and <c>System.UIntPtr</c> in a signature as special
    /// types already named <c>nint</c> and <c>nuint</c>. The two rows here cover the rest, such as
    /// a member signature of another assembly, which ILens decodes itself.
    /// </remarks>
    public static string TypeKeyword(IType type) => KeywordFor(type.Namespace, type.Name);

    /// <summary>
    /// The C# keyword for the type <paramref name="name"/> in <paramref name="namespace"/>,
    /// such as <c>int</c> for <c>System.Int32</c>, or null when it has none.
    /// </summary>
    public static string KeywordFor(string @namespace, string name) =>
        @namespace == "System" ? name switch
        {
            "Boolean" => "bool",
            "Byte"    => "byte",
            "SByte"   => "sbyte",
            "Char"    => "char",
            "Decimal" => "decimal",
            "Double"  => "double",
            "Single"  => "float",
            "Int32"   => "int",
            "UInt32"  => "uint",
            "Int64"   => "long",
            "UInt64"  => "ulong",
            "Int16"   => "short",
            "UInt16"  => "ushort",
            "IntPtr"  => "nint",
            "UIntPtr" => "nuint",
            "Object"  => "object",
            "String"  => "string",
            "Void"    => "void",
            _ => null,
        } : null;

    /// <summary>
    /// A name a declaration declares, as C# writes it there: with '@' before one of the 77 reserved
    /// keywords, <c>object @object</c>, and for an explicit implementation, which is named after its
    /// interface, before the part after the last dot, <c>Shop.IOps.@do</c>. ILSpy's own test
    /// decides (<see cref="CSharpOutputVisitor.IsKeyword"/>), given a context outside any query and
    /// any async member, where it holds for those keywords alone, as the decompiled code writes such
    /// a declaration. Without a context it also holds for await and the query keywords, which C#
    /// takes in a declaration without '@', in the parameters of an async method as well. The
    /// reference form writes a name as it stands.
    /// </summary>
    public static string FormatIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        var start = name.LastIndexOf('.') + 1;
        return CSharpOutputVisitor.IsKeyword(name[start..], SyntaxIdentifier.Null) ? name.Insert(start, "@") : name;
    }
}
