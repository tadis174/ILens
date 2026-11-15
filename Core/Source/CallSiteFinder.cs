using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using IL = ICSharpCode.Decompiler.IL;

namespace ILens;

/// <summary>
/// Finds every call to a set of methods and reads, from the decompiled C#, what each call
/// passes for each parameter.
/// </summary>
/// <remarks>
/// The calls are located twice, independently: counted in the IL, where every call is an
/// instruction naming its target, and found in the decompiled C#, which is where the arguments
/// can be read. A caller whose two
/// counts differ is reported as a shortfall rather than shown with fewer calls, because an
/// answer such as "none of these calls passes X" is only worth its claim if every call was
/// looked at.
///
/// C# writes many calls without call syntax, and each such form is read where the decompiler
/// leaves a mark of the method on the tree: a property, indexer or event access and a property
/// pattern, which call an accessor; an operator and a conversion, written or not; the three
/// calls of a foreach; the Add call behind an entry of a collection initializer; the
/// Deconstruct call of a deconstruction; and the base constructor call a constructor does not
/// write. These forms are frequent: asked about the methods of a game assembly and of a
/// library, 98 % and more of the callers that call a method without call syntax do so in one
/// of them, most often in an operator or conversion or in a base constructor call. Five forms
/// are not read and stay shortfalls: the creation of a delegate, which calls the delegate's
/// constructor and passes nothing a caller wrote, the <c>true</c> and <c>false</c> operators a
/// condition calls, which no node of the tree names, a property read through a pointer, an
/// indexer with a params array, and the copy method of a record, which the compiler
/// generates and a <c>with</c> expression calls. A member that fails to decompile is a
/// shortfall as well.
///
/// A virtual call runs through a slot, whichever declaration of the slot its instruction names,
/// so both counts book it under the declaration that introduced the slot where the assembly
/// declares it, and a call through a slot another assembly introduced under the declaration it
/// names. A C# compiler names the slot's declaration, except in a call of a method with a type
/// parameter of its own, where it names the nearest override the receiver's class sees, and in
/// a call on an object of a class that overrides with a more specific return type, where it
/// names that override; the VB compiler names the nearest override in any virtual call, and the
/// F# compiler in some. For an overriding target the calls through its slot are therefore
/// counted and found as well. One of them is a call to the target when the decompiler resolves
/// it to the target or to an override of it, which it does when the receiver's type is the
/// target's class or derives from it. The others only take part in comparing the two counts.
///
/// Which method a call names, and whether it is virtual, is read from the call instruction the
/// tree carries for it: an invocation's on its node, and for a call without call syntax where
/// the decompiler leaves it, as <see cref="AccessorBehind"/> says for an accessor, in the
/// instructions of a foreach and on a collection initializer's entry. A deconstruction's
/// pattern holds the method it calls but not whether the call is virtual, which it is for a
/// Deconstruct that can be overridden. The written form says neither: a decompiler can leave out <c>base.</c> where it
/// takes the plain name to mean the same method, C# has no form for a call Visual Basic writes
/// through <c>MyClass</c>, which calls an override of the calling class without virtual
/// dispatch, and an access reads alike whichever declaration of its slot the instruction names.
/// The setter of a compound assignment is called as its getter is, whose instruction is the
/// only one the decompiler keeps (<see cref="NearestAccessor"/>). Only a call the tree carries
/// no instruction for is read from its written form, as virtual unless it goes through
/// <c>base.</c>: the getter an initializer calls to fill what a property holds,
/// <c>P = { a }</c>.
/// </remarks>
public static class CallSiteFinder
{
    /// <summary>
    /// The length past which the text of a call, or of an argument shown alone, is shortened,
    /// as <see cref="Shorten"/> says.
    /// </summary>
    public const int LineLength = 300;

    /// <summary>
    /// One call: the member whose source contains it, the overload it calls, the call as C#,
    /// the argument for each parameter, in parameter order, and whether the call's text was
    /// shortened to <see cref="LineLength"/>.
    /// </summary>
    public sealed record CallSite(
        ISymbol Caller, IMethod Target, string Call, IReadOnlyList<Argument> Arguments, bool Shortened);

    /// <summary>
    /// What a call passes for one parameter. <see cref="IsDefault"/> holds only when the
    /// argument is provably the parameter's default: a constant equal to it.
    /// <see cref="Shortened"/> says that <see cref="Text"/> was shortened to
    /// <see cref="LineLength"/>.
    /// </summary>
    public sealed record Argument(IParameter Parameter, string Text, bool IsDefault, bool Shortened = false);

    /// <summary>A call's or an argument's text, and whether <see cref="Line"/> shortened it.</summary>
    private readonly record struct Written(string Text, bool Shortened);

    /// <summary>
    /// A caller whose IL holds <see cref="Expected"/> calls, of which the decompiled C# shows
    /// <see cref="Found"/>. <see cref="Failure"/> names the exception when decompiling the
    /// caller failed outright, and is <c>null</c> otherwise. <see cref="Through"/> is the
    /// declaration the calls are counted under when it is not a target but the declaration an
    /// overriding target overrides, and <c>null</c> otherwise.
    /// </summary>
    public sealed record Shortfall(ISymbol Caller, int Expected, int Found, string Failure, IMethod Through);

    /// <summary>
    /// The calls found and the callers whose calls were not all shown, with the name each caller
    /// is written by, <see cref="CallerName"/>: in full where another member the answer names, a
    /// caller with calls shown or one in a not-shown block, would read alike.
    /// </summary>
    public sealed record Result(IReadOnlyList<CallSite> Calls, IReadOnlyList<Shortfall> Shortfalls,
        Func<ISymbol, string> CallerName);

    /// <summary>
    /// Every call to <paramref name="targets"/> in the assembly, ordered by caller and, within
    /// a caller, by position in its decompiled source.
    /// </summary>
    public static Result Find(AssemblyHost host, IReadOnlyList<IMethod> targets)
    {
        // The declarations that introduced the targets' slots: a virtual call is counted under
        // the one whose slot it runs through. An override whose slot was introduced in another
        // assembly has none here, so the calls through that slot cannot be counted.
        var slots = targets
            .Select(SymbolResolver.SlotDeclarationOf)
            .Where(SymbolResolver.IntroducesSlot)
            .DistinctBy(slot => slot.MetadataToken)
            .ToList();
        // Those that are no targets themselves, the declarations the overriding targets override.
        var overridden = slots
            .Where(slot => !targets.Any(target => target.MetadataToken == slot.MetadataToken))
            .ToDictionary(slot => slot.MetadataToken);
        var bodies = host.CountCallsTo([.. targets, .. overridden.Values], slots);
        var sources = host.AttributeToSource(bodies.Select(body => (ISymbol)body.Method).ToList());

        // Several bodies can belong to one source member, such as a method and its lambdas.
        // Decompiling the member once shows them all, so their counts are summed, separately for
        // each method the calls name.
        var expected = new Dictionary<EntityHandle, (IEntity Member, Dictionary<EntityHandle, int> Calls)>();
        for (var i = 0; i < bodies.Count; i++)
        {
            var member = (IEntity)sources[i];
            if (!expected.TryGetValue(member.MetadataToken, out var entry))
                expected[member.MetadataToken] = entry = (member, new Dictionary<EntityHandle, int>());
            var named = bodies[i].Target.MetadataToken;
            entry.Calls[named] = entry.Calls.GetValueOrDefault(named) + bodies[i].Calls;
        }

        var targetTokens = targets.Select(target => target.MetadataToken).ToHashSet();
        var namedTokens = targetTokens.Concat(overridden.Keys).ToHashSet();
        var slotTokens = slots.Select(slot => slot.MetadataToken).ToHashSet();
        var calls = new List<CallSite>();
        var shortfalls = new List<Shortfall>();
        // Every caller the answer names, in its list of calls or in a not-shown block, is one of
        // these members, so the names are told apart among them all; the callers are taken in
        // the order of those names.
        var callerName = ReferenceFormatter.NamesFor(expected.Values.Select(entry => entry.Member));
        var callers = expected.Values
            .OrderBy(entry => callerName(entry.Member), StringComparer.Ordinal);
        foreach (var (member, counts) in callers)
        {
            SyntaxTree tree;
            try
            {
                tree = host.DecompileWithPositionalArguments(member);
            }
            catch (Exception ex)
            {
                // One member the decompiler cannot handle must not hide the calls in all the
                // others; its calls are reported as not shown instead.
                Console.Error.WriteLine(
                    $"[ILens] CallSiteFinder: failed to decompile {member.FullName}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                foreach (var (named, count) in counts)
                    shortfalls.Add(new Shortfall(member, count, 0, ex.GetType().Name, overridden.GetValueOrDefault(named)));
                continue;
            }

            var found = FindCalls(tree, member, namedTokens, targetTokens, slotTokens);
            calls.AddRange(found.Where(call => call.Call != null).Select(call => call.Call));
            foreach (var named in counts.Keys.Union(found.Select(call => call.Named)))
            {
                var count = counts.GetValueOrDefault(named);
                var shown = found.Count(call => call.Named == named);
                if (shown != count)
                    shortfalls.Add(new Shortfall(member, count, shown, null, overridden.GetValueOrDefault(named)));
            }
        }

        return new Result(calls, shortfalls, callerName);
    }

    /// <summary>
    /// Every call in <paramref name="tree"/> to a method in <paramref name="namedTokens"/>, each
    /// with the call to show when it is a call to a target, and with <c>null</c> when it is a
    /// call to the declaration an overriding target overrides that does not reach the target.
    /// A virtual call is booked under the declaration that introduced its slot when that is one
    /// of <paramref name="slotTokens"/>, and any other call under the method it names, as the
    /// count in the IL books them.
    /// </summary>
    private static List<(EntityHandle Named, CallSite Call)> FindCalls(
        SyntaxTree tree, ISymbol caller, HashSet<EntityHandle> namedTokens, HashSet<EntityHandle> targetTokens,
        HashSet<EntityHandle> slotTokens)
    {
        var found = new List<(EntityHandle, CallSite)>();
        // The decompiler can copy an annotation onto a second node; it still stands for one call.
        var read = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // The call to show, with its text and what it passes.
        CallSite Site(IMethod target, Func<Written> shown, IEnumerable<Expression> passed, bool expandedParams)
        {
            var line = shown();
            return new CallSite(caller, target, line.Text, MapArguments(target, passed, expandedParams), line.Shortened);
        }

        // The declaration a call of named is booked under, as the count in the IL books it: a
        // virtual call that names an override under the declaration that introduced the slot,
        // where that is one of slotTokens, and any other call under the method it names. exact
        // says that the call runs the method it names and no override of it.
        IMethod BookedUnder(IMethod named, bool exact)
        {
            var declaration = (IMethod)named.MemberDefinition;
            return !exact && Overriding.Of(declaration) && SymbolResolver.SlotDeclarationOf(declaration) is var slot
                && InAssembly(slot) && slotTokens.Contains(slot.MetadataToken)
                ? slot
                : declaration;
        }

        // Books a call of an accessor of the property or event the code names. named is the
        // accessor the call's instruction names, and exact whether the instruction calls it
        // without virtual dispatch; for the setter of a compound assignment they follow from
        // the getter's instruction, and where the tree carries no instruction for the call,
        // from the written form.
        void BookAccessor(IMember accessed, IMethod named, bool exact, Func<Written> shown,
            IEnumerable<Expression> passed)
        {
            // Metadata can declare a property without the accessor its syntax implies;
            // such a call is left to be reported as not shown.
            if (named is null)
                return;
            var declaration = BookedUnder(named, exact);
            if (!InAssembly(declaration) || !namedTokens.Contains(declaration.MetadataToken))
                return;
            var target = exact
                ? (targetTokens.Contains(declaration.MetadataToken) ? declaration : null)
                : TargetReached(accessed, member => AccessorOfKind(member, declaration.AccessorKind), targetTokens);
            found.Add((declaration.MetadataToken, target is null ? null : Site(target, shown, passed, expandedParams: false)));
        }

        // Books a call of the method the code names, which can be an accessor the code names as
        // a method. Compares by metadata token within the scanned assembly: the decompiler has
        // its own type system, so its symbols are never the same instances as the targets.
        // exact says that the call runs the method it names and no override of it. named is the
        // method the call's instruction names, where the tree carries the instruction; without
        // it the call is taken to name what C# compiles its written form to.
        void BookCall(IMethod method, bool exact, Func<Written> shown, IEnumerable<Expression> passed,
            bool expandedParams = false, IMethod named = null)
        {
            var declaration = BookedUnder(named ?? (exact ? method : SymbolResolver.SlotDeclarationOf(method)), exact);
            if (!InAssembly(declaration) || !namedTokens.Contains(declaration.MetadataToken))
                return;
            var callee = exact
                ? (targetTokens.Contains(declaration.MetadataToken) ? declaration : null)
                : TargetReached(method, member => member as IMethod, targetTokens);
            found.Add((declaration.MetadataToken, callee is null ? null : Site(callee, shown, passed, expandedParams)));
        }

        // Marks the instruction behind a call that was read from its node's resolve result or
        // annotation, so that it is not read a second time as an instruction a node carries.
        // The instruction of a cast can stand on what is cast, inside its parentheses.
        void MarkRead(AstNode node, IMethod method)
        {
            foreach (var call in node.Annotations.OfType<IL.CallInstruction>())
            {
                if (call.Method.MemberDefinition.Equals(method.MemberDefinition))
                    read.Add(call);
            }
            var inner = node switch
            {
                CastExpression cast => cast.Expression,
                ParenthesizedExpression parenthesized => parenthesized.Expression,
                _ => null,
            };
            if (inner != null)
                MarkRead(inner, method);
        }

        // The Deconstruct call of a deconstruction and those of the deconstructions nested in
        // it. parts is what the values go to, source the value taken apart where the code
        // writes one.
        void BookDeconstruction(IL.MatchInstruction pattern, AstNode parts, Expression source, Func<Written> shown)
        {
            if (!read.Add(pattern))
                return;
            var elements = PartsOf(parts);
            if (pattern.IsDeconstructCall && pattern.Method is { } deconstruct)
            {
                // An extension method takes the value as its first argument. Where the code
                // writes none, as for the element of a foreach, what it is taken apart into
                // stands for it. The pattern holds the method the IL calls, but not whether the
                // call was virtual, which it is for any Deconstruct that can be overridden.
                var receiver = deconstruct.IsStatic && (source ?? AsExpression(parts)) is { } value
                    ? new[] { value } : [];
                BookCall(SeenBy(pattern.Variable.Type, deconstruct), false, shown,
                    receiver.Concat(elements.Select(AsExpression)), named: deconstruct);
            }
            for (var i = 0; i < pattern.SubPatterns.Count && i < elements.Count; i++)
            {
                if (pattern.SubPatterns[i] is IL.MatchInstruction nested)
                    BookDeconstruction(nested, elements[i], AsExpression(elements[i]), shown);
            }
        }

        foreach (var node in tree.Descendants)
        {
            if (node is Expression access && AccessedMember(access) is { } accessed)
            {
                if (access.Parent is RecursivePatternExpression tested)
                    accessed = SeenBy(tested.Annotation<IL.MatchInstruction>()?.Variable.Type, accessed);
                var throughBase = ThroughBase(access);
                var owner = throughBase ? (IMember)accessed.MemberDefinition : SymbolResolver.SlotDeclarationOf(accessed);
                IL.CallInstruction getter = null;
                foreach (var (accessor, shown, passed) in AccessorCalls(access, owner))
                {
                    // The call's instruction says which accessor it names and whether it is
                    // virtual, as for an invocation. A compound assignment, x.P += 1 or x.P++,
                    // keeps only its getter's instruction, which stands for the setter as well.
                    // Without an instruction the written form decides: the accessor of the
                    // declaration that introduced the slot, called virtually, or through base.
                    // the one of the declaration it names.
                    var behind = accessor is null ? null : AccessorBehind(access, accessor);
                    if (accessor?.AccessorKind == MethodSemanticsAttributes.Getter)
                        getter = behind;
                    var (named, exact) = behind is not null ? (behind.Method, behind is not IL.CallVirt)
                        : getter is not null && accessor is not null
                            ? (NearestAccessor(getter.Method, accessor.AccessorKind), getter is not IL.CallVirt)
                            : (accessor, throughBase);
                    BookAccessor(accessed, named, exact, () => Line(shown), passed);
                }
            }

            var arguments = node switch
            {
                InvocationExpression invocation => invocation.Arguments,
                ObjectCreateExpression creation => creation.Arguments,
                ConstructorInitializer initializer => initializer.Arguments,
                _ => null,
            };
            if (arguments is not null && node.GetResolveResult() is InvocationResolveResult { Member: IMethod called } resolved)
            {
                // The instruction says which declaration the IL names and whether the call is
                // virtual. The written form does not say it reliably: a decompiler can leave out
                // "base." where the plain name means the same method, and a compiler can name an
                // override in a virtual call, as a C# compiler does for a generic method.
                var behind = CallBehind(node, called);
                BookCall(called, behind is null ? ThroughBase(node) : behind is not IL.CallVirt, () => Line(node),
                    arguments, resolved is CSharpInvocationResolveResult { IsExpandedForm: true }, behind?.Method);
                MarkRead(node, called);
            }

            if (OperatorCalled(node, out var operands) is { } @operator)
            {
                BookCall(@operator, false, () => Line(node), operands);
                MarkRead(node, @operator);
            }

            // A conversion the code does not write is shown as the cast that calls it. Where
            // the decompiler writes "a = a + b" as "a += b", the conversion of the sum stands
            // on the assignment.
            if (node is Expression converted
                && converted.Annotation<ImplicitConversionAnnotation>() is { } unwritten
                && unwritten.ConversionResolveResult.Conversion is { IsUserDefined: true, Method: { } conversion }
                && read.Add(unwritten))
            {
                var value = converted is AssignmentExpression compound
                    && AssignmentExpression.GetCorrespondingBinaryOperator(compound.Operator) is { } sum
                    ? new BinaryOperatorExpression(compound.Left.Clone(), sum, compound.Right.Clone())
                    : converted;
                BookCall(conversion, false, () => Line(CastOf(value, unwritten.TargetType)), [value]);
                MarkRead(node, conversion);
            }

            // Under a negation the converted expression has no such mark and carries only the
            // instruction that calls the conversion.
            if (node is Expression operand)
            {
                foreach (var call in operand.Annotations.OfType<IL.CallInstruction>())
                {
                    if (call.Method is { IsOperator: true, Name: "op_Implicit" or "op_Explicit" } && read.Add(call))
                        BookCall(call.Method, false, () => Line(CastOf(operand, call.Method.ReturnType)), [operand]);
                }
            }

            if (node is ForeachStatement loop && loop.Annotation<ForeachAnnotation>() is { } calls && read.Add(calls))
            {
                // The loop calls GetEnumerator on what it runs over and the other two on what
                // that returns.
                var receiver = loop.InExpression.GetResolveResult().Type;
                foreach (var instruction in new[] { calls.GetEnumeratorCall, calls.MoveNextCall, calls.GetCurrentCall })
                {
                    if (OutermostCall(instruction) is not { } call)
                        continue;
                    BookCall(SeenBy(receiver, call.Method), call is not IL.CallVirt, () => HeadLine(loop),
                        call.Method.IsStatic ? [loop.InExpression] : [], named: call.Method);
                    if (instruction == calls.GetEnumeratorCall)
                        receiver = call.Method.ReturnType;
                }
            }

            if (node is ArrayInitializerExpression { Parent: ObjectCreateExpression or NamedExpression } filled)
            {
                var collection = filled.Parent.GetResolveResult().Type;
                foreach (var entry in filled.Elements)
                {
                    // An entry with several arguments names its Add method itself; one with a
                    // single argument is that argument's expression and carries the call.
                    if (entry is ArrayInitializerExpression several)
                    {
                        if (entry.GetResolveResult() is InvocationResolveResult { Member: IMethod add })
                        {
                            var behind = CallBehind(entry, add);
                            BookCall(SeenBy(collection, add), behind is not null and not IL.CallVirt, () => Line(entry),
                                ReceiverOf(filled, add).Concat(several.Elements), named: behind?.Method);
                        }
                    }
                    else if (entry is not NamedExpression && AddCallOf(entry) is { } call && read.Add(call))
                    {
                        BookCall(SeenBy(collection, call.Method), call is not IL.CallVirt,
                            () => Line(entry, written => "{ " + Render(written) + " }"),
                            ReceiverOf(filled, call.Method).Append(entry), named: call.Method);
                    }
                }
            }

            if (node is AssignmentExpression assignment)
            {
                foreach (var deconstruction in assignment.Annotations.OfType<IL.DeconstructInstruction>())
                    BookDeconstruction(deconstruction.Pattern, assignment.Left, assignment.Right, () => Line(assignment));
            }
            else if (node is VariableDesignation designation)
            {
                // Reached only where no assignment carries the deconstruction, as in a foreach.
                foreach (var pattern in designation.Annotations.OfType<IL.MatchInstruction>())
                    BookDeconstruction(pattern, designation, null,
                        () => designation.Parent is ForeachStatement each ? HeadLine(each) : Line(designation.Parent));
            }
        }

        // A constructor calls another constructor before anything else. The decompiler drops
        // ": base()" when it passes nothing, and the call then stands only in the constructor's
        // IL function.
        foreach (var constructor in tree.Descendants.OfType<ConstructorDeclaration>())
        {
            var chained = constructor.Annotation<IL.ILFunction>()?.Descendants.OfType<IL.Call>()
                .FirstOrDefault(call => call.Method.IsConstructor && !call.Method.IsStatic
                    && call.Arguments.Count > 0 && call.Arguments[0].MatchLdThis());
            if (chained != null && !tree.Descendants.Any(node => node.Annotations.Contains(chained)))
                BookCall(chained.Method, false, () => new Written(": base()", false), []);
        }
        return found;
    }

    /// <summary>
    /// The call instruction <paramref name="node"/> carries for its call of
    /// <paramref name="called"/>, the method the decompiler resolved the call to, or <c>null</c>
    /// when it carries none: the one whose method shares that method's slot. The decompiler
    /// resolves a virtual call to the override the receiver's type sees, so the two can be
    /// different declarations of one slot.
    /// </summary>
    private static IL.CallInstruction CallBehind(AstNode node, IMethod called)
    {
        var slot = SymbolResolver.SlotDeclarationOf(called);
        return node.Annotations.OfType<IL.CallInstruction>()
            .FirstOrDefault(call => SymbolResolver.SlotDeclarationOf(call.Method).Equals(slot));
    }

    /// <summary>
    /// The call instruction the tree carries for the call of <paramref name="accessor"/> that
    /// <paramref name="access"/> makes, or <c>null</c> when it carries none: the one whose
    /// method shares the accessor's slot, as <see cref="CallBehind"/> finds it. A getter's
    /// stands on the access, a setter's or an event accessor's on the assignment around it, an
    /// object initializer's setter's on its entry, which is the access, and a property
    /// pattern's getter's in the access's own sub-pattern of the pattern's instruction, as the
    /// call on the value the pattern tests.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: <c>CallBuilder.Build</c>
    /// puts the instruction on what <c>HandleAccessorCall</c> builds, the access for a getter
    /// and the assignment for an accessor that returns nothing; <c>HandleCompoundAssignment</c>
    /// keeps only the getter's, as its target; <c>BuildArrayInitializerExpression</c> puts a
    /// setter's on its <c>NamedExpression</c>, an indexer's setter's on the assignment
    /// <c>BuildDictionaryInitializerExpression</c> builds for an index initializer's entry,
    /// and <c>MakeInitializerAssignment</c> none on the one of an entry that fills what a
    /// property holds; <c>TranslatePattern</c> puts the pattern's instruction on the
    /// <c>RecursivePatternExpression</c>, whose named arguments it writes one for each of the
    /// instruction's sub-patterns, in their order. The cases:
    /// CallSites_OnAnOverrideOfAnotherAssemblysMember_ListsEachCallOnce and
    /// CallSites_OnAPropertyCalledThroughMyClass_ListsEachCallOnce in BaseCallTests, and
    /// Scale's get_Mark and Showcase's set_Item in
    /// FindCallSites_ShowsACallWrittenWithoutCallSyntax in CallSiteTests.
    /// </remarks>
    private static IL.CallInstruction AccessorBehind(Expression access, IMethod accessor)
    {
        if (CallBehind(access, accessor) is { } carried)
            return carried;
        if (access.Parent is AssignmentExpression assignment && assignment.Left == access)
            return CallBehind(assignment, accessor);
        if (access.Parent is not RecursivePatternExpression pattern
            || pattern.Annotation<IL.MatchInstruction>() is not { } match)
            return null;

        // The decompiler writes one named argument for each of the instruction's sub-patterns,
        // in their order, so the access's place picks its own: a pattern can test one property
        // twice, as { Operator: not A, Operator: not B } does, each with a call of its own.
        var place = pattern.SubPatterns.TakeWhile(sub => sub != access).Count();
        var own = pattern.SubPatterns.Count == match.SubPatterns.Count && place < match.SubPatterns.Count
            ? [match.SubPatterns[place]]
            : match.SubPatterns.ToList();
        var slot = SymbolResolver.SlotDeclarationOf(accessor);
        return own
            .SelectMany(sub => sub.Descendants)
            .OfType<IL.CallInstruction>()
            .FirstOrDefault(call => call.Arguments.Count > 0 && call.Arguments[0].MatchLdLocRef(match.Variable)
                && SymbolResolver.SlotDeclarationOf(call.Method).Equals(slot));
    }

    /// <summary>
    /// The accessor of the kind <paramref name="kind"/> that a call of the accessor
    /// <paramref name="method"/> stands for in a compound assignment: the one of its property,
    /// or of the nearest property that one overrides that declares one, as a compiler names
    /// it. The decompiler keeps no instruction for that call.
    /// </summary>
    private static IMethod NearestAccessor(IMethod method, MethodSemanticsAttributes kind)
    {
        for (var member = method.AccessorOwner; member != null; member = Overriding.Overridden(member))
        {
            if (AccessorOfKind(member, kind) is { } accessor)
                return accessor;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="member"/> as a receiver of type <paramref name="receiver"/> runs it: the
    /// override nearest to that type, or the member itself when it is not virtual or the type
    /// has no override of it. The decompiler resolves a call it writes as a call to that
    /// override, which <see cref="TargetReached"/> relies on; a form read from the instruction
    /// behind it, or from a property pattern, names the declaration the IL calls instead.
    /// </summary>
    private static T SeenBy<T>(IType receiver, T member) where T : class, IMember
    {
        if (receiver is null || !(member.IsVirtual || Overriding.Of(member) || member.IsAbstract))
            return member;
        if (member is IMethod { AccessorOwner: { } owner } accessor)
            return (T)(IMember)(AccessorOfKind(SeenBy(receiver, owner), accessor.AccessorKind) ?? accessor);

        var slot = SymbolResolver.SlotDeclarationOf(member);
        // The base types come with the type itself last.
        foreach (var type in receiver.GetNonInterfaceBaseTypes().Reverse())
        {
            if (type.GetDefinition() is not { } definition)
                continue;
            if (definition.Equals(slot.DeclaringTypeDefinition))
                break;
            if (InheritanceHelper.GetDerivedMember(slot, definition) is T seen && Overriding.Of(seen))
                return seen;
        }
        return member;
    }

    /// <summary>
    /// The method an operator expression calls, with its operands in parameter order, or
    /// <c>null</c> for a node that is no such expression or whose operator is built in:
    /// <c>a + b</c>, <c>-a</c>, <c>a += b</c>, <c>++a</c>, and a cast that calls a conversion
    /// operator.
    /// </summary>
    private static IMethod OperatorCalled(AstNode node, out IReadOnlyList<Expression> operands)
    {
        operands = node switch
        {
            BinaryOperatorExpression binary => [binary.Left, binary.Right],
            UnaryOperatorExpression unary => [unary.Expression],
            AssignmentExpression assignment => [assignment.Left, assignment.Right],
            CastExpression cast => [cast.Expression],
            _ => null,
        };
        if (operands is null)
            return null;
        // Every resolve result of the node is asked: where the decompiler writes "a = a + b" as
        // "a += b", the assignment keeps its own and takes the sum's after it.
        return node.Annotations.OfType<ResolveResult>()
            .Select(result => result switch
            {
                InvocationResolveResult { Member: IMethod method } => method,
                OperatorResolveResult { UserDefinedOperatorMethod: { } method } => method,
                ConversionResolveResult { Conversion: { IsUserDefined: true, Method: { } method } } => method,
                _ => null,
            })
            .FirstOrDefault(method => method != null);
    }

    /// <summary>
    /// The call an instruction of a foreach stands for: the instruction itself, or the call
    /// inside what the compiler wraps it in, such as a comparison or a cast. A call among the
    /// arguments of that call is one the tree shows on its own.
    /// </summary>
    private static IL.CallInstruction OutermostCall(IL.ILInstruction instruction) =>
        instruction as IL.CallInstruction
        ?? instruction.Children.Select(OutermostCall).FirstOrDefault(call => call != null);

    /// <summary>
    /// The Add call behind <paramref name="entry"/>, an entry of a collection initializer with
    /// one argument, or <c>null</c>. The entry carries the call as an annotation, beside the
    /// instruction of its own when it is itself a call; the Add call is the one the other is an
    /// argument of. A single call that is the entry's own is no Add call, and neither is an
    /// accessor's: the entry of an index initializer, <c>[0] = item</c>, carries its setter's,
    /// which the access inside it books (<see cref="AccessorBehind"/>), unless the indexer takes
    /// a params array, whose calls are all reported as not shown.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050:
    /// <c>AccessPathElement.GetAccessPath</c> makes an entry an Add call's only where the method
    /// it calls is no accessor. The case: Showcase's set_Item in
    /// FindCallSites_ShowsACallWrittenWithoutCallSyntax in CallSiteTests.
    /// </remarks>
    private static IL.CallInstruction AddCallOf(Expression entry)
    {
        var calls = entry.Annotations.OfType<IL.CallInstruction>().ToList();
        var outer = calls.FirstOrDefault(call => calls.All(other => other.IsDescendantOf(call)));
        if (outer is null || outer.Method.IsAccessor)
            return null;
        return calls.Count == 1 && entry.GetResolveResult() is InvocationResolveResult { Member: { } own }
            && own.MemberDefinition.Equals(outer.Method.MemberDefinition) ? null : outer;
    }

    /// <summary>
    /// What a collection initializer passes as the receiver to an Add method that is an
    /// extension method: the object it fills, written as the creation without its initializer
    /// or as the property that holds it. Nothing for an instance method.
    /// </summary>
    private static IEnumerable<Expression> ReceiverOf(ArrayInitializerExpression initializer, IMethod add)
    {
        if (!add.IsStatic)
            return [];
        return initializer.Parent switch
        {
            NamedExpression named => [new IdentifierExpression(named.Name)],
            ObjectCreateExpression creation =>
                [new ObjectCreateExpression(creation.Type.Clone(), creation.Arguments.Select(argument => argument.Clone()))],
            _ => [],
        };
    }

    /// <summary>
    /// The parts a deconstruction takes a value apart into: the variables of
    /// <c>var (a, b)</c> or the elements of <c>(a, b)</c>. None for a single variable.
    /// </summary>
    private static List<AstNode> PartsOf(AstNode parts) => parts switch
    {
        ParenthesizedVariableDesignation designation => [.. designation.VariableDesignations],
        TupleExpression tuple => [.. tuple.Elements],
        DeclarationExpression declaration => PartsOf(declaration.Designation),
        _ => [],
    };

    /// <summary>A part of a deconstruction as an expression, so that it prints as an argument.</summary>
    private static Expression AsExpression(AstNode part) => part switch
    {
        null => null,
        Expression expression => expression,
        _ => new IdentifierExpression(Render(part)),
    };

    /// <summary>
    /// The target a call calls that runs whichever override its receiver has, or <c>null</c>:
    /// the first member, from <paramref name="resolved"/> back through the declarations it
    /// overrides, whose <paramref name="part"/> is a target. The decompiler resolves a call to the override
    /// the receiver's type sees, so the call is an overriding target's exactly when the
    /// receiver's type is the target's class or derives from it, and every call through a slot
    /// is a call to the declaration that introduced it.
    /// </summary>
    private static IMethod TargetReached(IMember resolved, Func<IMember, IMethod> part,
        HashSet<EntityHandle> targetTokens)
    {
        for (var current = resolved.MemberDefinition; current != null;
             current = Overriding.Overridden(current)?.MemberDefinition)
        {
            if (part(current) is { } method && InAssembly(method) && targetTokens.Contains(method.MetadataToken))
                return method;
        }
        return null;
    }

    /// <summary>
    /// The accessor of property or event <paramref name="member"/> that plays the part
    /// <paramref name="kind"/> names, or <c>null</c> when the member declares none.
    /// </summary>
    private static IMethod AccessorOfKind(IMember member, MethodSemanticsAttributes kind) => (member, kind) switch
    {
        (IProperty property, MethodSemanticsAttributes.Getter) => property.Getter,
        (IProperty property, MethodSemanticsAttributes.Setter) => property.Setter,
        (IEvent @event, MethodSemanticsAttributes.Adder) => @event.AddAccessor,
        (IEvent @event, MethodSemanticsAttributes.Remover) => @event.RemoveAccessor,
        (IEvent @event, MethodSemanticsAttributes.Raiser) => @event.InvokeAccessor,
        _ => null,
    };

    /// <summary>True for a member of the scanned assembly rather than of one it references.</summary>
    private static bool InAssembly(IMember member) => member.ParentModule == member.Compilation.MainModule;

    /// <summary>
    /// True for a call or an access through <c>base.</c>, which C# compiles as a call to exactly
    /// the declaration it names rather than to the declaration that introduced the slot. For a
    /// call whose node carries its instruction, the instruction says so instead
    /// (<see cref="CallBehind"/>).
    /// </summary>
    private static bool ThroughBase(AstNode node) => node switch
    {
        InvocationExpression { Target: MemberReferenceExpression { Target: BaseReferenceExpression } } => true,
        MemberReferenceExpression { Target: BaseReferenceExpression } => true,
        IndexerExpression { Target: BaseReferenceExpression } => true,
        _ => false,
    };

    /// <summary>
    /// The property or event <paramref name="access"/> names, when it is written with property,
    /// indexer or event syntax, which calls an accessor without an invocation: <c>x.P</c>,
    /// <c>P</c>, <c>x[i]</c>, <c>P = v</c> in an object initializer, or <c>P: v</c> in a
    /// property pattern. An indexer with a params array is left out, so its calls stay
    /// reported as not shown: its arguments cannot be told apart from its indices by position.
    /// </summary>
    private static IMember AccessedMember(Expression access)
    {
        if (access is not (MemberReferenceExpression or IdentifierExpression or IndexerExpression
            or NamedExpression or NamedArgumentExpression { Parent: RecursivePatternExpression }))
            return null;
        if (access.GetResolveResult() is not MemberResolveResult { Member: IProperty or IEvent } resolved)
            return null;
        if (resolved.Member is IProperty { Parameters: var indices } && indices.Any(p => p.IsParams))
            return null;
        return resolved.Member;
    }

    /// <summary>
    /// The accessors that <paramref name="access"/> calls, each with the node to show as the
    /// call and its arguments: the indices, then, for a setter, the value assigned. What the
    /// access stands in decides which accessors it calls. As the target of <c>=</c> or in an
    /// object initializer it calls the setter; as the target of a compound assignment,
    /// <c>++</c> or <c>--</c> it calls the getter and then the setter, whose value is the
    /// computed one, <c>x.P + 3</c>; an event's <c>+=</c> and <c>-=</c> call its add and remove
    /// accessors; anywhere else a property is read through its getter. An initializer that
    /// fills what a property holds, <c>P = { a, b }</c>, sets nothing: it calls the getter once
    /// for each entry, and each call is shown with its entry, <c>P = { a }</c>. A property
    /// pattern calls the getter and is shown in the braces a pattern writes it in.
    /// </summary>
    private static IEnumerable<(IMethod Accessor, AstNode Shown, List<Expression> Arguments)> AccessorCalls(
        Expression access, IMember owner)
    {
        var indices = access is IndexerExpression indexer ? indexer.Arguments.ToList() : [];
        List<Expression> With(Expression value) => [.. indices, value];

        if (access is NamedArgumentExpression tested)
        {
            if (owner is IProperty matched)
                yield return (matched.Getter, new RecursivePatternExpression { SubPatterns = { tested.Clone() } }, indices);
            yield break;
        }

        if (access is NamedExpression named)
        {
            if (owner is not IProperty initialized)
                yield break;
            if (named.Expression is not ArrayInitializerExpression held)
            {
                yield return (initialized.Setter, named, With(named.Expression));
                yield break;
            }
            foreach (var entry in held.Elements)
            {
                var withEntry = new NamedExpression(named.Name, new ArrayInitializerExpression(entry.Clone()));
                for (var reads = EntriesIn(entry); reads > 0; reads--)
                    yield return (initialized.Getter, withEntry, indices);
            }
            yield break;
        }

        var assignment = access.Parent is AssignmentExpression a && a.Left == access ? a : null;
        if (owner is IEvent @event)
        {
            if (assignment?.Operator == AssignmentOperatorType.Add)
                yield return (@event.AddAccessor, assignment, [assignment.Right]);
            else if (assignment?.Operator == AssignmentOperatorType.Subtract)
                yield return (@event.RemoveAccessor, assignment, [assignment.Right]);
            yield break;
        }

        // A property that returns by reference, as Span's indexer does, has no setter: every use
        // calls the getter and writes through the reference it returns. Any other property
        // without a setter is written only in a constructor, which uses its backing field.
        var property = (IProperty)owner;
        if (property.ReturnType is ByReferenceType)
        {
            yield return (property.Getter, access, indices);
            yield break;
        }
        var step = access.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.Increment
            or UnaryOperatorType.PostIncrement or UnaryOperatorType.Decrement
            or UnaryOperatorType.PostDecrement } unary ? unary : null;
        if (!property.CanSet && (assignment is not null || step is not null))
            yield break;
        if (assignment?.Operator == AssignmentOperatorType.Assign)
        {
            yield return (property.Setter, assignment, With(assignment.Right));
            yield break;
        }

        Expression computed = null;
        AstNode shown = access;
        if (assignment is not null)
        {
            computed = new BinaryOperatorExpression(access.Clone(),
                AssignmentExpression.GetCorrespondingBinaryOperator(assignment.Operator).Value,
                assignment.Right.Clone());
            shown = assignment;
        }
        else if (step is not null)
        {
            computed = new BinaryOperatorExpression(access.Clone(),
                step.Operator is UnaryOperatorType.Increment or UnaryOperatorType.PostIncrement
                    ? BinaryOperatorType.Add : BinaryOperatorType.Subtract,
                new PrimitiveExpression(1));
            shown = step;
        }

        yield return (property.Getter, shown, indices);
        if (computed is not null)
            yield return (property.Setter, shown, With(computed));
    }

    /// <summary>
    /// The argument for each parameter of <paramref name="method"/>. The decompiler writes the
    /// arguments positionally, so position decides, and it omits a trailing optional argument
    /// only when it equals the parameter's default, so an omitted parameter takes its default.
    /// A named argument is mapped by name, should a decompiler setting ever let one through.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: <c>CallBuilder</c> leaves
    /// out an argument as described, through <c>IsOptionalArgument</c>. The cases:
    /// FindCallSites_NonDefaultOnly_ListsExactlyTheCallsThatPassAnotherValue and
    /// FindCallSites_WithoutParameter_ShowsEachCallWithEveryArgument in CallSiteTests.
    /// </remarks>
    private static IReadOnlyList<Argument> MapArguments(
        IMethod method, IEnumerable<Expression> arguments, bool expandedParams)
    {
        var parameters = method.Parameters;
        var passed = new List<Expression>[parameters.Count];

        var position = 0;
        foreach (var argument in arguments)
        {
            int index;
            var value = argument;
            if (argument is NamedArgumentExpression named)
            {
                index = parameters.ToList().FindIndex(p => p.Name == named.Name);
                value = named.Expression;
            }
            else
            {
                // In the expanded form of a params call the trailing arguments all belong to
                // the params parameter.
                index = expandedParams ? Math.Min(position, parameters.Count - 1) : position;
            }
            position++;

            if (index >= 0 && index < parameters.Count)
                (passed[index] ??= []).Add(value);
        }

        var mapped = new List<Argument>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (passed[i] is not { } values)
            {
                // An empty params expansion passes an empty array. Any other parameter without
                // an argument was omitted because the call passed its default.
                mapped.Add(parameter.IsParams
                    ? new Argument(parameter, "(none)", false)
                    : new Argument(parameter, ConstantFormatter.DefaultOf(parameter), parameter.IsOptional));
                continue;
            }

            var isDefault = values.Count == 1 && IsDefaultValue(parameter, values[0]);
            var text = ArgumentLine(values);
            mapped.Add(new Argument(parameter, text.Text, isDefault, text.Shortened));
        }
        return mapped;
    }

    /// <summary>
    /// True if <paramref name="value"/> is a compile-time constant equal to the default of an
    /// optional <paramref name="parameter"/>. Anything else, a variable included, is not
    /// provably the default, so it counts as a different value.
    /// </summary>
    private static bool IsDefaultValue(IParameter parameter, Expression value)
    {
        if (!parameter.IsOptional)
            return false;

        var result = value.GetResolveResult();
        while (result is ConversionResolveResult conversion)
            result = conversion.Input;
        return result is { IsCompileTimeConstant: true }
            && SameConstant(result.ConstantValue, parameter.GetConstantValue());
    }

    /// <summary>
    /// Constant equality across the representations the decompiler and the metadata use: a
    /// default is stored as its underlying number while an argument may carry an enum or a
    /// wider numeric type.
    /// </summary>
    private static bool SameConstant(object a, object b)
    {
        if (Equals(a, b))
            return true;
        if (a is null || b is null || a is string || b is string || a is bool || b is bool)
            return false;
        try
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture)
                == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A node as one line of C#, formatted as the other decompile tools format their output.
    /// Only line breaks and the indentation after them are folded, because the decompiler writes
    /// string literals with escapes, never with raw line breaks.
    /// </summary>
    private static string Render(AstNode node) =>
        Regex.Replace(node.ToString(Formatting), @"\r?\n\s*", " ").Trim();

    /// <summary>What a shortened line writes in place of the code it leaves out.</summary>
    private const string Elision = "...";

    /// <summary>
    /// <paramref name="root"/>, the node of a call, as its line, written by
    /// <paramref name="write"/>, or by <see cref="Render"/> without it, and shortened as
    /// <see cref="Shorten"/> says.
    /// </summary>
    private static Written Line(AstNode root, Func<AstNode, string> write = null)
    {
        write ??= Render;
        return Shorten(write(root), () => root.Clone(), write, (_, _) => false);
    }

    /// <summary>
    /// What a call passes for one parameter as one line, the values of an expanded params
    /// parameter together, shortened as <see cref="Shorten"/> says, except that a value keeps
    /// itself and the body of a lambda that is one: an argument shown alone is what was asked
    /// about, and cut it keeps its start, where left out it would read <c>() =&gt; ...</c>.
    /// </summary>
    private static Written ArgumentLine(IReadOnlyList<Expression> values) =>
        Shorten(string.Join(", ", values.Select(Render)),
            () => new ArrayInitializerExpression(values.Select(value => value.Clone())),
            list => string.Join(", ", ((ArrayInitializerExpression)list).Elements.Select(Render)),
            (list, node) => node.Parent == list || IsLambdaBody(node) && node.Parent.Parent == list);

    /// <summary>
    /// <paramref name="text"/>, a line <paramref name="write"/> wrote, shortened to
    /// <see cref="LineLength"/> characters where it is longer. Where leaving parts of its code out
    /// can make it fit, a copy of its tree from <paramref name="copy"/> is written again with
    /// first the bodies of its lambdas and anonymous methods, then the contents of its
    /// initializers, each the largest first, as <c>...</c> until it fits, passing over a part its
    /// <c>...</c> would not make shorter, the copy's root and what <paramref name="keeps"/> holds.
    /// Where leaving all of them out would not make it fit either, the line is cut as it stands
    /// (<see cref="Cut"/>), since the parts would be lost for a few characters more of its end.
    /// </summary>
    /// <remarks>
    /// A body goes before an initializer since it is the caller's own code, which decompiling the
    /// caller shows, where an initializer's entries are values the call passes. Over the methods
    /// of every third type of a game assembly, 630 of 64,093 call lines were longer than 300
    /// characters, the longest 3,592, and two in three fit once parts are left out. Before the
    /// cut took a line as it stands, 44 of the 77 cut lines of a library and 13 of the game's
    /// 208 had lost parts first, mostly lambdas of a few characters such as
    /// <c>a =&gt; a.Expression</c>.
    /// </remarks>
    private static Written Shorten(string text, Func<AstNode> copy, Func<AstNode, string> write,
        Func<AstNode, AstNode, bool> keeps)
    {
        if (text.Length <= LineLength)
            return new Written(text, false);

        // The least the line can be: every part left out, the outermost first, which a walk from
        // the root reaches before the parts inside them.
        var least = copy();
        foreach (var part in Elidable(least, keeps))
        {
            if (Attached(part, least) && Gain(part) > 0)
                Elide(part);
        }
        if (write(least).Length > LineLength)
            return new Written(Cut(text), true);

        var tree = copy();
        var parts = Elidable(tree, keeps);
        foreach (var kind in new Func<AstNode, bool>[] { IsBody, IsInitializer })
        {
            var largestFirst = parts
                .Where(part => kind(part) && Attached(part, tree))
                .Select(part => (Part: part, Gain: Gain(part)))
                .Where(part => part.Gain > 0)
                .OrderByDescending(part => part.Gain)
                .Select(part => part.Part)
                .ToList();
            foreach (var part in largestFirst)
            {
                // A part inside one left out before it is gone with it.
                if (!Attached(part, tree))
                    continue;
                Elide(part);
                var shortened = write(tree);
                if (shortened.Length <= LineLength)
                    return new Written(shortened, true);
            }
        }
        // Not reached: this walk leaves out every part the least version leaves out.
        return new Written(Cut(text), true);
    }

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="LineLength"/> characters with the <c>...</c>
    /// that ends it, before a surrogate pair the cut would split, and before the dots it would
    /// end in, which would read as more than the marker, <c>line....</c>.
    /// </summary>
    private static string Cut(string text)
    {
        var kept = LineLength - Elision.Length;
        if (char.IsHighSurrogate(text[kept - 1]))
            kept--;
        while (kept > 0 && text[kept - 1] == '.')
            kept--;
        return text[..kept] + Elision;
    }

    /// <summary>
    /// The parts of <paramref name="tree"/> that <see cref="Elide"/> can write as <c>...</c>,
    /// those outside before those inside them, without the root and what
    /// <paramref name="keeps"/> holds.
    /// </summary>
    private static List<AstNode> Elidable(AstNode tree, Func<AstNode, AstNode, bool> keeps) =>
        tree.Descendants.Where(node => (IsBody(node) || IsInitializer(node)) && !keeps(tree, node)).ToList();

    /// <summary>Whether <paramref name="part"/> is still in <paramref name="tree"/>, not inside a part left out.</summary>
    private static bool Attached(AstNode part, AstNode tree) => part.AncestorsAndSelf.Contains(tree);

    /// <summary>How many characters writing <paramref name="part"/> as its placeholder takes off its line.</summary>
    private static int Gain(AstNode part) => Render(part).Length - Placeholder(part).Length;

    /// <summary>The body of a lambda, or an anonymous method, whose body <see cref="Elide"/> leaves out with it.</summary>
    private static bool IsBody(AstNode node) => IsLambdaBody(node) || node is AnonymousMethodExpression;

    private static bool IsLambdaBody(AstNode node) => node.Parent is LambdaExpression lambda && lambda.Body == node;

    /// <summary>The braces of an object, collection or array initializer, or of an anonymous type, that hold something.</summary>
    private static bool IsInitializer(AstNode node) =>
        node is ArrayInitializerExpression { Elements.Count: > 0 } or AnonymousTypeCreateExpression { Initializers.Count: > 0 };

    /// <summary>
    /// What <see cref="Elide"/> writes for <paramref name="part"/>: <c>...</c> for a lambda's
    /// body, braces around it for an initializer's contents, and for an anonymous method the
    /// method with its parameters, <c>delegate(int x) { ... }</c>.
    /// </summary>
    private static string Placeholder(AstNode part) => part switch
    {
        _ when IsLambdaBody(part) => Elision,
        AnonymousMethodExpression method => HeadOf(method) + "{ " + Elision + " }",
        AnonymousTypeCreateExpression => "new { " + Elision + " }",
        _ => "{ " + Elision + " }",
    };

    /// <summary>An anonymous method as written up to its body, <c>delegate(int x) </c>.</summary>
    private static string HeadOf(AnonymousMethodExpression method)
    {
        var head = (AnonymousMethodExpression)method.Clone();
        head.Body = new BlockStatement();
        var written = Render(head);
        return written[..written.LastIndexOf('{')];
    }

    /// <summary>Writes <paramref name="part"/> as its <see cref="Placeholder"/>.</summary>
    private static void Elide(AstNode part)
    {
        if (IsLambdaBody(part))
        {
            ((LambdaExpression)part.Parent).Body = Verbatim(Elision);
        }
        else if (part is AnonymousMethodExpression method)
        {
            method.ReplaceWith(Verbatim(Placeholder(method)));
        }
        else if (part is ArrayInitializerExpression initializer)
        {
            initializer.Elements.Clear();
            initializer.Elements.Add(Verbatim(Elision));
        }
        else if (part is AnonymousTypeCreateExpression anonymous)
        {
            anonymous.Initializers.Clear();
            anonymous.Initializers.Add(Verbatim(Elision));
        }
    }

    /// <summary>A node the output visitor writes as <paramref name="text"/>, as it is.</summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050:
    /// <c>TextWriterTokenWriter.WritePrimitiveValue</c> writes a value of a type it does not know
    /// by its <c>ToString</c>, unescaped, where an identifier of that name would have a backslash
    /// in it escaped again; <c>LongCallTests</c> pins the placeholders written so.
    /// </remarks>
    private static PrimitiveExpression Verbatim(string text) => new(new VerbatimText(text));

    /// <summary>The value <see cref="Verbatim"/> gives the output visitor, written as its text.</summary>
    private sealed class VerbatimText(string text)
    {
        public override string ToString() => text;
    }

    /// <summary>
    /// <paramref name="expression"/> as the cast to <paramref name="type"/> that the compiler
    /// inserted and the source does not write, <c>(Money)3</c>.
    /// </summary>
    private static CastExpression CastOf(Expression expression, IType type)
    {
        var cast = new CastExpression(
            new TypeSystemAstBuilder { AlwaysUseShortTypeNames = true }.ConvertType(type), expression.Clone());
        cast.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
        return cast;
    }

    /// <summary>
    /// The head of a foreach without its body as a line, <c>foreach (int item in items)</c>,
    /// shortened in what the loop runs over.
    /// </summary>
    private static Written HeadLine(ForeachStatement loop)
    {
        var head = $"{(loop.IsAsync ? "await " : "")}foreach ({Render(loop.VariableType)} " +
            $"{Render(loop.VariableDesignation)} in ";
        return Line(loop.InExpression, runsOver => head + Render(runsOver) + ")");
    }

    /// <summary>
    /// How often an initializer reads the property it fills for <paramref name="entry"/>: once
    /// for an entry, and for an entry that fills a property in turn, once for each entry in it.
    /// </summary>
    private static int EntriesIn(Expression entry) =>
        entry is NamedExpression { Expression: ArrayInitializerExpression held }
            ? held.Elements.Sum(EntriesIn)
            : 1;

    private static readonly CSharpFormattingOptions Formatting =
        new DecompilerSettings().CSharpFormattingOptions;
}
