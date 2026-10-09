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
/// looked at. A call to a property or event accessor is written with property, indexer or event
/// syntax and is recognized as such. The decompiler can express a call as something else too,
/// such as an operator or a collection initializer, and a member can fail to decompile; both
/// surface as shortfalls.
///
/// A call to an override names the declaration that introduced its slot, so for an overriding
/// target the calls to that declaration are counted and found as well. One of them is a call to
/// the target when the decompiler resolves it to the target or to an override of it, which it
/// does when the receiver's type is the target's class or derives from it. The others only take
/// part in comparing the two counts.
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
    /// declaration the calls name when it is not a target but the declaration an overriding
    /// target overrides, and <c>null</c> otherwise.
    /// </summary>
    public sealed record Shortfall(ISymbol Caller, int Expected, int Found, string Failure, IMethod Through);

    public sealed record Result(IReadOnlyList<CallSite> Calls, IReadOnlyList<Shortfall> Shortfalls);

    /// <summary>
    /// Every call to <paramref name="targets"/> in the assembly, ordered by caller and, within
    /// a caller, by position in its decompiled source.
    /// </summary>
    public static Result Find(AssemblyHost host, IReadOnlyList<IMethod> targets)
    {
        // The declarations the overriding targets override, whose calls are counted as well. An
        // override whose slot was introduced in another assembly has none here, so the calls
        // through that slot cannot be counted.
        var slots = targets
            .Select(SymbolResolver.SlotDeclarationOf)
            .Where(slot => !slot.IsOverride && !targets.Any(target => target.MetadataToken == slot.MetadataToken))
            .DistinctBy(slot => slot.MetadataToken)
            .ToDictionary(slot => slot.MetadataToken);
        var bodies = host.CountCallsTo([.. targets, .. slots.Values]);
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
        var namedTokens = targetTokens.Concat(slots.Keys).ToHashSet();
        var calls = new List<CallSite>();
        var shortfalls = new List<Shortfall>();
        var callers = expected.Values
            .OrderBy(entry => ReferenceFormatter.FormatSymbol(entry.Member), StringComparer.Ordinal);
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
                    shortfalls.Add(new Shortfall(member, count, 0, ex.GetType().Name, slots.GetValueOrDefault(named)));
                continue;
            }

            var found = FindCalls(tree, member, namedTokens, targetTokens);
            calls.AddRange(found.Where(call => call.Call != null).Select(call => call.Call));
            foreach (var named in counts.Keys.Union(found.Select(call => call.Named)))
            {
                var count = counts.GetValueOrDefault(named);
                var shown = found.Count(call => call.Named == named);
                if (shown != count)
                    shortfalls.Add(new Shortfall(member, count, shown, null, slots.GetValueOrDefault(named)));
            }
        }

        return new Result(calls, shortfalls);
    }

    /// <summary>
    /// Every call in <paramref name="tree"/> to a method in <paramref name="namedTokens"/>, as
    /// the IL names it, each with the call to show when it is a call to a target, and with
    /// <c>null</c> when it is a call to the declaration an overriding target overrides that does
    /// not reach the target.
    /// </summary>
    private static List<(EntityHandle Named, CallSite Call)> FindCalls(
        SyntaxTree tree, ISymbol caller, HashSet<EntityHandle> namedTokens, HashSet<EntityHandle> targetTokens)
    {
        var found = new List<(EntityHandle, CallSite)>();
        foreach (var node in tree.Descendants)
        {
            if (node is Expression access && AccessedMember(access) is { } accessed)
            {
                var throughBase = ThroughBase(access);
                var owner = throughBase ? (IMember)accessed.MemberDefinition : SymbolResolver.SlotDeclarationOf(accessed);
                foreach (var (accessor, shown, passed) in AccessorCalls(access, owner))
                {
                    // Metadata can declare a property without the accessor its syntax implies;
                    // such a call is left to be reported as not shown.
                    if (accessor is null)
                        continue;
                    var named = (IMethod)accessor.MemberDefinition;
                    if (!InAssembly(named) || !namedTokens.Contains(named.MetadataToken))
                        continue;
                    var target = throughBase
                        ? (targetTokens.Contains(named.MetadataToken) ? named : null)
                        : TargetReached(accessed, member => AccessorOfKind(member, named.AccessorKind), targetTokens);
                    found.Add((named.MetadataToken, target is null ? null
                        : new CallSite(caller, target, Render(shown), MapArguments(target, passed, expandedParams: false))));
                }
                continue;
            }

            var arguments = node switch
            {
                InvocationExpression invocation => invocation.Arguments,
                ObjectCreateExpression creation => creation.Arguments,
                ConstructorInitializer initializer => initializer.Arguments,
                _ => null,
            };
            if (arguments is null)
                continue;
            if (node.GetResolveResult() is not InvocationResolveResult resolved
                || resolved.Member is not IMethod method)
                continue;

            // Compare by metadata token within the scanned assembly: the decompiler has its own
            // type system, so its symbols are never the same instances as the targets.
            var throughBaseCall = ThroughBase(node);
            var declaration = throughBaseCall ? (IMethod)method.MemberDefinition : SymbolResolver.SlotDeclarationOf(method);
            if (!InAssembly(declaration) || !namedTokens.Contains(declaration.MetadataToken))
                continue;
            var callee = throughBaseCall
                ? (targetTokens.Contains(declaration.MetadataToken) ? declaration : null)
                : TargetReached(method, member => member as IMethod, targetTokens);
            found.Add((declaration.MetadataToken, callee is null ? null : new CallSite(caller, callee, Render(node),
                MapArguments(callee, arguments, resolved is CSharpInvocationResolveResult { IsExpandedForm: true }))));
        }
        return found;
    }

    /// <summary>
    /// The target a call that is not through <c>base.</c> calls, or <c>null</c>: the first
    /// member, from <paramref name="resolved"/> back through the declarations it overrides,
    /// whose <paramref name="part"/> is a target. The decompiler resolves a call to the override
    /// the receiver's type sees, so the call is an overriding target's exactly when the
    /// receiver's type is the target's class or derives from it, and every call through a slot
    /// is a call to the declaration that introduced it.
    /// </summary>
    private static IMethod TargetReached(IMember resolved, Func<IMember, IMethod> part,
        HashSet<EntityHandle> targetTokens)
    {
        for (var current = resolved.MemberDefinition; current != null;
             current = current.IsOverride ? InheritanceHelper.GetBaseMember(current)?.MemberDefinition : null)
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
    /// the declaration it names rather than to the declaration that introduced the slot.
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
    /// <c>P</c>, <c>x[i]</c>, or <c>P = v</c> in an object initializer. An indexer with a params
    /// parameter is left out, so its calls stay reported as not shown: its arguments cannot be
    /// told apart from its indices by position.
    /// </summary>
    private static IMember AccessedMember(Expression access)
    {
        if (access is not (MemberReferenceExpression or IdentifierExpression or IndexerExpression
            or NamedExpression))
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
    /// accessors; anywhere else a property is read through its getter.
    /// </summary>
    private static IEnumerable<(IMethod Accessor, AstNode Shown, List<Expression> Arguments)> AccessorCalls(
        Expression access, IMember owner)
    {
        var indices = access is IndexerExpression indexer ? indexer.Arguments.ToList() : [];
        List<Expression> With(Expression value) => [.. indices, value];

        if (access is NamedExpression named)
        {
            if (owner is IProperty initialized)
                yield return (initialized.Setter, named, With(named.Expression));
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
                    : new Argument(parameter, FormatConstant(parameter.GetConstantValue()),
                        parameter.IsOptional));
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

    /// <summary>A constant as a C# literal, e.g. <c>null</c>, <c>true</c>, <c>0</c> or <c>"x"</c>.</summary>
    public static string FormatConstant(object value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => $"\"{s}\"",
        char c => $"'{c}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// A node as one line of C#, formatted as the other decompile tools format their output.
    /// Only line breaks and the indentation after them are folded, because the decompiler writes
    /// string literals with escapes, never with raw line breaks.
    /// </summary>
    private static string Render(AstNode node) =>
        Regex.Replace(node.ToString(Formatting), @"\r?\n\s*", " ").Trim();

    private static readonly CSharpFormattingOptions Formatting =
        new DecompilerSettings().CSharpFormattingOptions;
}
