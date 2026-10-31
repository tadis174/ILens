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
/// so both counts book it under the declaration that introduced the slot. A C# compiler names
/// that declaration, except in a call of a method with a type parameter of its own, where it
/// names the nearest override the receiver's class sees, and in a call on an object of a class
/// that overrides with a more specific return type, where it names that override; the VB and
/// the F# compiler name the nearest override in any virtual call. For an overriding target the
/// calls through its slot are therefore counted and found as well. One of them is a call to the
/// target when the decompiler resolves it to the target or to an override of it, which it does
/// when the receiver's type is the target's class or derives from it. The others only take part
/// in comparing the two counts.
///
/// Whether a written call is virtual is read from the call instruction its node carries, and for
/// a call that is not, the declaration it names. The written form does not say either: the
/// decompiler leaves out <c>base.</c> before an override that the calling class does not
/// override itself, where the plain name means the same method.
/// </remarks>
public static class CallSiteFinder
{
    /// <summary>
    /// One call: the member whose source contains it, the overload it calls, the call as C#,
    /// and the argument for each parameter, in parameter order.
    /// </summary>
    public sealed record CallSite(
        ISymbol Caller, IMethod Target, string Call, IReadOnlyList<Argument> Arguments);

    /// <summary>
    /// What a call passes for one parameter. <see cref="IsDefault"/> holds only when the
    /// argument is provably the parameter's default: a constant equal to it.
    /// </summary>
    public sealed record Argument(IParameter Parameter, string Text, bool IsDefault);

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

        // Books a call of an accessor of the property or event the code names.
        void BookAccessor(IMember accessed, IMethod accessor, bool throughBase, Func<string> shown,
            IEnumerable<Expression> passed)
        {
            // Metadata can declare a property without the accessor its syntax implies;
            // such a call is left to be reported as not shown.
            if (accessor is null)
                return;
            var named = (IMethod)accessor.MemberDefinition;
            if (!InAssembly(named) || !namedTokens.Contains(named.MetadataToken))
                return;
            var target = throughBase
                ? (targetTokens.Contains(named.MetadataToken) ? named : null)
                : TargetReached(accessed, member => AccessorOfKind(member, named.AccessorKind), targetTokens);
            found.Add((named.MetadataToken, target is null ? null
                : new CallSite(caller, target, shown(), MapArguments(target, passed, expandedParams: false))));
        }

        // Books a call of the method the code names, which can be an accessor the code names as
        // a method. Compares by metadata token within the scanned assembly: the decompiler has
        // its own type system, so its symbols are never the same instances as the targets.
        // exact says that the call runs the method it names and no override of it. named is the
        // method the call's instruction names, where the node carries the instruction; without
        // it the call is taken to name what C# compiles its written form to. A virtual call
        // that names an override is booked under the declaration that introduced the slot,
        // where the count in the IL books it.
        void BookCall(IMethod method, bool exact, Func<string> shown, IEnumerable<Expression> passed,
            bool expandedParams = false, IMethod named = null)
        {
            var declaration = (IMethod)named?.MemberDefinition
                ?? (exact ? (IMethod)method.MemberDefinition : SymbolResolver.SlotDeclarationOf(method));
            if (!exact && Overriding.Of(declaration) && SymbolResolver.SlotDeclarationOf(declaration) is var slot
                && InAssembly(slot) && slotTokens.Contains(slot.MetadataToken))
                declaration = slot;
            if (!InAssembly(declaration) || !namedTokens.Contains(declaration.MetadataToken))
                return;
            var callee = exact
                ? (targetTokens.Contains(declaration.MetadataToken) ? declaration : null)
                : TargetReached(method, member => member as IMethod, targetTokens);
            found.Add((declaration.MetadataToken, callee is null ? null
                : new CallSite(caller, callee, shown(), MapArguments(callee, passed, expandedParams))));
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
        void BookDeconstruction(IL.MatchInstruction pattern, AstNode parts, Expression source, Func<string> shown)
        {
            if (!read.Add(pattern))
                return;
            var elements = PartsOf(parts);
            if (pattern.IsDeconstructCall && pattern.Method is { } deconstruct)
            {
                // An extension method takes the value as its first argument. Where the code
                // writes none, as for the element of a foreach, what it is taken apart into
                // stands for it.
                var receiver = deconstruct.IsStatic && (source ?? AsExpression(parts)) is { } value
                    ? new[] { value } : [];
                BookCall(SeenBy(pattern.Variable.Type, deconstruct), false, shown,
                    receiver.Concat(elements.Select(AsExpression)));
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
                foreach (var (accessor, shown, passed) in AccessorCalls(access, owner))
                    BookAccessor(accessed, accessor, throughBase, () => Render(shown), passed);
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
                // virtual. The written form does not: the decompiler leaves out "base." where
                // the plain name means the same method, and a compiler can name an override in
                // a virtual call, as a C# compiler does for a generic method.
                var behind = CallBehind(node, called);
                BookCall(called, behind is null ? ThroughBase(node) : behind is not IL.CallVirt, () => Render(node),
                    arguments, resolved is CSharpInvocationResolveResult { IsExpandedForm: true }, behind?.Method);
                MarkRead(node, called);
            }

            if (OperatorCalled(node, out var operands) is { } @operator)
            {
                BookCall(@operator, false, () => Render(node), operands);
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
                BookCall(conversion, false, () => RenderAsCast(value, unwritten.TargetType), [value]);
                MarkRead(node, conversion);
            }

            // Under a negation the converted expression has no such mark and carries only the
            // instruction that calls the conversion.
            if (node is Expression operand)
            {
                foreach (var call in operand.Annotations.OfType<IL.CallInstruction>())
                {
                    if (call.Method is { IsOperator: true, Name: "op_Implicit" or "op_Explicit" } && read.Add(call))
                        BookCall(call.Method, false, () => RenderAsCast(operand, call.Method.ReturnType), [operand]);
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
                    BookCall(SeenBy(receiver, call.Method), false, () => RenderHead(loop),
                        call.Method.IsStatic ? [loop.InExpression] : []);
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
                            BookCall(SeenBy(collection, add), false, () => Render(entry),
                                ReceiverOf(filled, add).Concat(several.Elements));
                    }
                    else if (entry is not NamedExpression && AddCallOf(entry) is { } call && read.Add(call))
                    {
                        BookCall(SeenBy(collection, call.Method), false, () => "{ " + Render(entry) + " }",
                            ReceiverOf(filled, call.Method).Append(entry));
                    }
                }
            }

            if (node is AssignmentExpression assignment)
            {
                foreach (var deconstruction in assignment.Annotations.OfType<IL.DeconstructInstruction>())
                    BookDeconstruction(deconstruction.Pattern, assignment.Left, assignment.Right, () => Render(assignment));
            }
            else if (node is VariableDesignation designation)
            {
                // Reached only where no assignment carries the deconstruction, as in a foreach.
                foreach (var pattern in designation.Annotations.OfType<IL.MatchInstruction>())
                    BookDeconstruction(pattern, designation, null,
                        () => designation.Parent is ForeachStatement each ? RenderHead(each) : Render(designation.Parent));
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
                BookCall(chained.Method, false, () => ": base()", []);
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
    /// argument of. A single call that is the entry's own is no Add call.
    /// </summary>
    private static IL.CallInstruction AddCallOf(Expression entry)
    {
        var calls = entry.Annotations.OfType<IL.CallInstruction>().ToList();
        var outer = calls.FirstOrDefault(call => calls.All(other => other.IsDescendantOf(call)));
        if (outer is null)
            return null;
        var own = entry.GetResolveResult() switch
        {
            InvocationResolveResult invocation => invocation.Member,
            MemberResolveResult member => member.Member,
            _ => null,
        };
        var named = outer.Method.AccessorOwner ?? outer.Method;
        return calls.Count == 1 && own != null && own.MemberDefinition.Equals(named.MemberDefinition) ? null : outer;
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
            mapped.Add(new Argument(parameter, string.Join(", ", values.Select(Render)), isDefault));
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

    /// <summary>
    /// <paramref name="expression"/> as the cast to <paramref name="type"/> that the compiler
    /// inserted and the source does not write, <c>(Money)3</c>.
    /// </summary>
    private static string RenderAsCast(Expression expression, IType type)
    {
        var cast = new CastExpression(
            new TypeSystemAstBuilder { AlwaysUseShortTypeNames = true }.ConvertType(type), expression.Clone());
        cast.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
        return Render(cast);
    }

    /// <summary>The head of a foreach without its body, <c>foreach (int item in items)</c>.</summary>
    private static string RenderHead(ForeachStatement loop) =>
        $"{(loop.IsAsync ? "await " : "")}foreach ({Render(loop.VariableType)} {Render(loop.VariableDesignation)} " +
        $"in {Render(loop.InExpression)})";

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
