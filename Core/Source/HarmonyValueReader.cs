using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.Semantics;
using CallInstruction = ICSharpCode.Decompiler.IL.CallInstruction;
using ILFunctionKind = ICSharpCode.Decompiler.IL.ILFunctionKind;
using ILVariable = ICSharpCode.Decompiler.IL.ILVariable;
using LdMemberToken = ICSharpCode.Decompiler.IL.LdMemberToken;
using UnaryInstruction = ICSharpCode.Decompiler.IL.UnaryInstruction;
using VariableKind = ICSharpCode.Decompiler.IL.VariableKind;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Reads what decompiled code hands to Harmony and to reflection: the classes and members it looks
/// up, the patch methods it names, and the text, flags and lists these are built from. It reads
/// the trees <see cref="AssemblyHost.DecompileWithPositionalArguments"/> builds, in which every call
/// writes its arguments in parameter order, a call to an extension method is a static call with the
/// receiver first, and every symbol is resolved, a class from an assembly the decompiler cannot load
/// by the name the reference to it gives.
/// </summary>
/// <remarks>
/// A value is followed back to where the assembly writes it: a local to the assignments that reach
/// its use; a parameter to each call of its member, a call binding all of the member's parameters
/// together; a field to the assignments before its use in the member, and failing those, a static
/// field to every assignment and an instance field to what the constructor that built the object
/// assigns it, for each place that builds one; and an own method to what it returns, with the values of
/// the call's arguments in place of its parameters, read once for each set of values, see
/// <see cref="ReadOwnCall"/>. Following stops <see cref="MaxDepth"/> members
/// away from where the reading started. A value it cannot follow is <see cref="Unknown"/>, which a
/// line prints as '?'. Every read gives a list of alternatives, one for each branch or element that
/// may supply the value. The alternatives of two arguments of one call are not paired: a call that
/// would need that reads as <see cref="Unknown"/>.
/// </remarks>
internal sealed class HarmonyValueReader
{
    /// <summary>How many members away from where the reading starts a value is followed.</summary>
    public const int MaxDepth = 4;

    /// <summary>
    /// A value the code computes, as far as the reader can tell. <see cref="Origins"/> are the
    /// lookup calls it comes from, and no part of what makes two values equal. A record that holds a
    /// class is listed in <see cref="ClassRead"/>, which tells the calls of an own method apart by what
    /// the reader reads of the class beside its name.
    /// </summary>
    public abstract record Value
    {
        public IReadOnlyList<Origin> Origins { get; init; } = [];

        public virtual bool Equals(Value other) => other is not null && EqualityContract == other.EqualityContract;
        public override int GetHashCode() => EqualityContract.GetHashCode();
    }

    /// <summary>
    /// A lookup call and the value it made. The value read from the call carries it, and so does a
    /// value derived from that one, such as the accessor taken from a property looked up, so a patch
    /// target can be traced to the call that found it. Code that makes a value from another passes
    /// the origins on, as <see cref="AccessorOf"/> does; without them the lookup gets a line of its
    /// own beside the patch line that shows its result. Two are equal when they name the same call
    /// in the tree and equal values.
    /// </summary>
    public sealed record Origin(AstNode Call, Value Made)
    {
        public bool Equals(Origin other) => other is not null && ReferenceEquals(Call, other.Call) && Made.Equals(other.Made);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Call), Made);
    }

    /// <summary>A value the reader cannot follow.</summary>
    public sealed record UnknownValue : Value;

    public sealed record NullValue : Value;

    public sealed record TextValue(string Text) : Value;

    /// <summary>
    /// A constant that is no text: a bool, or an integer as a long, an enum's value among them, such
    /// as the binding flags a lookup is given.
    /// </summary>
    public sealed record ConstantValue(object Constant) : Value;

    /// <summary>
    /// A class, by its name as the other tools take it, with the type when the tree resolves it or
    /// the scan finds the class a text names, and otherwise the text the code names it by, or for a
    /// class nested in one the scan cannot look into, its reflection name. Two are equal by name.
    /// </summary>
    public sealed record TypeValue(string Name, IType Type) : Value
    {
        public string Text { get; init; }

        public bool Equals(TypeValue other) => other is not null && other.Name == Name;
        public override int GetHashCode() => Name.GetHashCode();
    }

    /// <summary>
    /// A type passed by reference, as Type.MakeByRefType makes it from <see cref="Element"/>. Harmony
    /// binds it to a parameter passed by ref, out or in alike, so a parameter type writes it
    /// <c>ref T</c>, the form ILens writes for a parameter whose keyword it does not know.
    /// </summary>
    public sealed record ByReferenceValue(TypeValue Element) : Value;

    /// <summary>
    /// A method or accessor, by its class and name and, when the code pins an overload, its
    /// parameter types, written as parameter types are; <see cref="MethodType"/> is the kind patched
    /// when the name does not show it, such as Enumerator or Lambdas, which name code the compiler
    /// generates for the method, or LocalFunction with the local function's name and any parameter
    /// types, and <see cref="Searched"/> says that the code's own tests pick the overload.
    /// <see cref="TypeArguments"/> are those a generic method was made for, written as parameter
    /// types are, '?' for one not read, and null for a method the code does not make for type
    /// arguments.
    /// </summary>
    public sealed record MethodValue(TypeValue Type, string Name, IReadOnlyList<string> ParameterTypes,
        string MethodType = null, bool Searched = false) : Value
    {
        public IReadOnlyList<string> TypeArguments { get; init; }

        /// <summary>The method as a line names it: its name and the type arguments it was made for, Parse&lt;int&gt;.</summary>
        public string Member => WithTypeArguments(Name, TypeArguments);

        public bool Equals(MethodValue other) =>
            other is not null && Type.Equals(other.Type) && Name == other.Name && MethodType == other.MethodType
            && Searched == other.Searched && ParameterListComparer.Instance.Equals(ParameterTypes, other.ParameterTypes)
            && ParameterListComparer.Instance.Equals(TypeArguments, other.TypeArguments);

        public override int GetHashCode() => HashCode.Combine(Type, Name, MethodType, Searched, ParameterTypes?.Count, TypeArguments?.Count);
    }

    /// <summary>A method's name with the type arguments it was made for, as C# writes the call, or the name alone for a method made for none.</summary>
    public static string WithTypeArguments(string name, IReadOnlyList<string> typeArguments) =>
        name is null || typeArguments is null ? name : $"{name}<{string.Join(", ", typeArguments)}>";

    /// <summary>A property, by its class and name; an indexer looked up by its index types also by those.</summary>
    public sealed record PropertyValue(TypeValue Type, string Name, IReadOnlyList<string> ParameterTypes = null) : Value
    {
        public bool Equals(PropertyValue other) =>
            other is not null && Type.Equals(other.Type) && Name == other.Name
            && ParameterListComparer.Instance.Equals(ParameterTypes, other.ParameterTypes);

        public override int GetHashCode() => HashCode.Combine(Type, Name, ParameterTypes?.Count);
    }

    /// <summary>An event, by its class and name.</summary>
    public sealed record EventValue(TypeValue Type, string Name) : Value;

    /// <summary>A patch method as a HarmonyMethod names it: its class and its name.</summary>
    public sealed record PatchMethodValue(TypeValue Class, string Name) : Value;

    /// <summary>The methods of a class, narrowed to one name when the code tests the name.</summary>
    public sealed record MethodsValue(TypeValue Type, string Name) : Value;

    /// <summary>A list whose elements the code writes out, each a value of its own.</summary>
    public sealed record ListValue(IReadOnlyList<Value> Elements) : Value
    {
        public bool Equals(ListValue other) => other is not null && Elements.SequenceEqual(other.Elements);
        public override int GetHashCode() => Elements.Count;
    }

    /// <summary>
    /// A Traverse over an object of a class, or over a class's static members; '?' for a class not
    /// read, whose <see cref="Type"/> is then null.
    /// </summary>
    public sealed record TraverseValue(string Class) : Value
    {
        public TypeValue Type { get; init; }

        public static TraverseValue Over(TypeValue type) => new(type?.Name ?? "?") { Type = type };
    }

    public static readonly Value Unknown = new UnknownValue();
    public static readonly Value Null = new NullValue();
    private static readonly IReadOnlyList<Value> UnknownOnly = [Unknown];
    private static readonly Value EmptyList = new ListValue([]);

    // What a static field reads as while its own value is read: unknown, in a list of its own, so that a
    // field met again then is told apart from one read.
    private static readonly IReadOnlyList<Value> BeingRead = [Unknown];

    /// <summary>
    /// Where an expression is read: the member whose tree holds it, what its parameters, its
    /// object's fields and the variables of a lambda are bound to, and the member where the reading
    /// started, which is the one that writes the values out.
    /// </summary>
    public sealed class Frame
    {
        internal Frame(IMethod member, SyntaxTree tree, IMethod origin, int depth)
        {
            Member = member;
            Tree = tree;
            Origin = origin;
            Depth = depth;
        }

        public IMethod Member { get; }
        public SyntaxTree Tree { get; }

        /// <summary>The member that writes the values out: this frame's, or the outermost caller's.</summary>
        public IMethod Origin { get; }

        internal int Depth { get; }

        /// <summary>The member's parameters by index. One not bound reads as unknown.</summary>
        internal IReadOnlyDictionary<int, IReadOnlyList<Binding>> Parameters { get; private init; } = NoParameters;

        /// <summary>The fields of the member's object by name. One not bound reads as unknown.</summary>
        internal IReadOnlyDictionary<string, IReadOnlyList<Binding>> Fields { get; private init; } = NoFields;

        /// <summary>The variables of a lambda bound to values.</summary>
        internal IReadOnlyDictionary<ILVariable, IReadOnlyList<Value>> Variables { get; private init; } = NoVariables;

        internal Frame WithParameters(IReadOnlyDictionary<int, IReadOnlyList<Binding>> parameters, IMethod origin) =>
            new(Member, Tree, origin, Depth) { Parameters = parameters, Fields = Fields, Variables = Variables };

        internal Frame WithFields(IReadOnlyDictionary<string, IReadOnlyList<Binding>> fields, IMethod origin) =>
            new(Member, Tree, origin, Depth) { Parameters = Parameters, Fields = fields, Variables = Variables };

        internal Frame WithVariable(ILVariable variable, IReadOnlyList<Value> values) =>
            new(Member, Tree, Origin, Depth)
            {
                Parameters = Parameters,
                Fields = Fields,
                Variables = new Dictionary<ILVariable, IReadOnlyList<Value>>(Variables) { [variable] = values },
            };

        private static readonly IReadOnlyDictionary<int, IReadOnlyList<Binding>> NoParameters =
            new Dictionary<int, IReadOnlyList<Binding>>();
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<Binding>> NoFields =
            new Dictionary<string, IReadOnlyList<Binding>>();
        private static readonly IReadOnlyDictionary<ILVariable, IReadOnlyList<Value>> NoVariables =
            new Dictionary<ILVariable, IReadOnlyList<Value>>();
    }

    /// <summary>An expression read in a frame, or values already known, such as a parameter's default.</summary>
    internal sealed record Binding(Expression Expression, Frame Frame, IReadOnlyList<Value> Values = null)
    {
        public static Binding Of(IReadOnlyList<Value> values) => new(null, null, values);
    }

    /// <summary>The calls of a member the trees show, and whether the IL makes more of them than the trees show.</summary>
    private sealed class CallSites : List<(IMethod Caller, AstNode Call, IReadOnlyList<Expression> Arguments)>
    {
        public bool Unseen { get; set; }
    }

    private readonly AssemblyHost _host;
    private readonly string _assemblyName;
    private readonly Dictionary<EntityHandle, SyntaxTree> _trees = new();
    private readonly Dictionary<EntityHandle, CallSites> _callSites = new();
    private readonly Dictionary<EntityHandle, IReadOnlyList<Value>> _staticFields = new();
    private readonly Dictionary<OwnCall, IReadOnlyList<Value>> _returns = new();
    private readonly HashSet<(AstNode, Frame)> _reading = new();
    private HashSet<string> _ownTypes;

    // How often a static field was met while its own value was read, which cuts that reading short.
    private int _cutShort;

    // The method a delegate type's Harmony attributes name, null where the type names none.
    private readonly Func<ITypeDefinition, HarmonyDependencyScanner.DelegateMethod> _delegateTarget;

    public HarmonyValueReader(AssemblyHost host, Func<ITypeDefinition, HarmonyDependencyScanner.DelegateMethod> delegateTarget)
    {
        _host = host;
        _assemblyName = host.TypeSystem.MainModule.AssemblyName;
        _delegateTarget = delegateTarget;
    }

    // --- Trees and frames ---------------------------------------------------------------------

    /// <summary>A member's tree, decompiled once per reader; null when decompiling fails.</summary>
    public SyntaxTree Tree(IMethod member)
    {
        var definition = member.MemberDefinition as IMethod ?? member;
        if (_trees.TryGetValue(definition.MetadataToken, out var tree))
            return tree;
        try
        {
            tree = _host.DecompileWithPositionalArguments(definition);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ILens] HarmonyValueReader: failed to decompile " +
                $"{member.DeclaringType?.FullName}.{member.Name}: {ex.GetType().Name}: {ex.Message}");
            tree = null;
        }
        return _trees[definition.MetadataToken] = tree;
    }

    /// <summary>
    /// The frames in which to read <paramref name="expressions"/> of <paramref name="member"/>: one
    /// with nothing bound when they need neither the member's parameters nor its object's fields,
    /// and otherwise one for each call of the member, or each construction of its object, bound as
    /// that call or construction binds them. A member nothing in the assembly calls, or one
    /// <see cref="MaxDepth"/> members away, gets a frame that leaves them unbound, as does a call the
    /// IL makes and the trees do not show, such as through a method group.
    /// </summary>
    public IEnumerable<Frame> Frames(IMethod member, SyntaxTree tree, IEnumerable<Expression> expressions) =>
        Frames(member, tree, expressions, 0);

    private List<Frame> Frames(IMethod member, SyntaxTree tree, IEnumerable<Expression> expressions, int depth)
    {
        List<Frame> frames = [new Frame(member, tree, member, depth)];
        var (parameters, fields) = Needs(member, expressions);
        if (parameters.Count > 0)
            frames = frames.SelectMany(frame => BindParameters(frame, parameters)).ToList();
        if (fields.Count > 0)
            frames = frames.SelectMany(frame => BindFields(frame, fields)).ToList();
        return frames;
    }

    /// <summary>
    /// The member's parameters and its object's fields that <paramref name="expressions"/> read,
    /// directly or through the locals they read. A parameter of a lambda in the member is not one.
    /// </summary>
    private (HashSet<int> Parameters, HashSet<string> Fields) Needs(IMethod member, IEnumerable<Expression> expressions)
    {
        var parameters = new HashSet<int>();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<ILVariable>();
        var pending = new Stack<AstNode>(expressions.Where(expression => expression is not null && !expression.IsNull));
        while (pending.Count > 0)
        {
            foreach (var node in pending.Pop().DescendantsAndSelf)
            {
                switch (node)
                {
                    case IdentifierExpression identifier when identifier.GetILVariable() is { } variable:
                        if (IsMemberParameter(variable))
                            parameters.Add(variable.Index!.Value);
                        else if (seen.Add(variable))
                            foreach (var definition in AllDefinitions(identifier, variable))
                                pending.Push(definition);
                        break;
                    case IdentifierExpression or MemberReferenceExpression
                        when node.GetSymbol() is IField { IsStatic: false } field && IsOwnField(node, field, member):
                        fields.Add(field.Name);
                        break;
                }
            }
        }
        return (parameters, fields);
    }

    /// <summary>One frame per call of the frame's member, binding <paramref name="parameters"/> as the call does.</summary>
    private List<Frame> BindParameters(Frame frame, HashSet<int> parameters)
    {
        if (frame.Depth >= MaxDepth)
            return [frame];
        var sites = CallSitesOf(frame.Member);
        var frames = new List<Frame>();
        foreach (var (caller, call, arguments) in sites)
        {
            if (Tree(caller) is not { } callerTree)
                continue;
            var called = call.GetSymbol() as IMethod;
            var bound = parameters.ToDictionary(index => index, index => arguments.ElementAtOrDefault(index));
            foreach (var callerFrame in Frames(caller, callerTree, bound.Values.Where(argument => argument is not null), frame.Depth + 1))
            {
                var bindings = bound.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<Binding>)[pair.Value is not null
                        ? new Binding(pair.Value, callerFrame)
                        : Binding.Of(DefaultOf(called, pair.Key))]);
                frames.Add(frame.WithParameters(bindings, callerFrame.Origin));
            }
        }
        if (frames.Count == 0 || sites.Unseen)
            frames.Add(frame);
        return frames;
    }

    /// <summary>
    /// One frame per construction of the frame's member's object, binding <paramref name="fields"/>
    /// to what the constructor that built it assigns them. A constructor that assigns a field nothing
    /// leaves it at its default, unless it hands over to another constructor of the class, which makes
    /// the field unknown, as does a constructor that cannot be decompiled.
    /// </summary>
    private List<Frame> BindFields(Frame frame, HashSet<string> fields)
    {
        if (frame.Member.IsStatic || frame.Depth >= MaxDepth || frame.Member.DeclaringTypeDefinition is not { } type)
            return [frame];
        var frames = new List<Frame>();
        foreach (var constructor in type.DeclaredMethods().Where(method => method.IsConstructor && !method.IsStatic && method.HasBody))
        {
            if (Tree(constructor) is not { } tree)
            {
                frames.Add(frame.WithFields(fields.ToDictionary(field => field, _ => (IReadOnlyList<Binding>)[Binding.Of(UnknownOnly)]), frame.Origin));
                continue;
            }
            var assigned = fields.ToDictionary(field => field, field => FieldAssignments(tree, field));
            var handsOver = tree.Descendants.OfType<ConstructorInitializer>()
                .Any(initializer => initializer.ConstructorInitializerType == ConstructorInitializerType.This);
            foreach (var built in Frames(constructor, tree, assigned.Values.SelectMany(values => values), frame.Depth + 1))
            {
                var bindings = assigned.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<Binding>)(pair.Value.Count > 0
                        ? pair.Value.Select(value => new Binding(value, built)).ToList()
                        : [Binding.Of(handsOver ? UnknownOnly : [Null])]));
                frames.Add(frame.WithFields(bindings, built.Origin));
            }
        }
        if (frames.Count == 0)
            frames.Add(frame);
        return frames;
    }

    /// <summary>What a constructor's tree assigns the field of its object named <paramref name="field"/>.</summary>
    private static List<Expression> FieldAssignments(SyntaxTree tree, string field) =>
        tree.Descendants.OfType<AssignmentExpression>()
            .Where(assignment => assignment.Operator == AssignmentOperatorType.Assign
                && assignment.Left.GetSymbol() is IField { IsStatic: false } assigned && assigned.Name == field
                && assignment.Left is IdentifierExpression or MemberReferenceExpression { Target: ThisReferenceExpression })
            .Select(assignment => assignment.Right)
            .ToList();

    /// <summary>
    /// Every call of <paramref name="member"/> the trees of the assembly show, with its arguments in
    /// parameter order, and whether the IL makes calls of it the trees do not show.
    /// </summary>
    private CallSites CallSitesOf(IMethod member)
    {
        var definition = member.MemberDefinition as IMethod ?? member;
        if (_callSites.TryGetValue(definition.MetadataToken, out var cached))
            return cached;

        var sites = new CallSites();
        if (definition.DeclaringTypeDefinition?.ReflectionName is { } typeName)
        {
            foreach (var (caller, calls) in _host.MembersCalling((type, name) => type == typeName && name == definition.Name))
            {
                if (Tree(caller) is not { } tree)
                {
                    sites.Unseen = true;
                    continue;
                }
                var found = 0;
                foreach (var node in tree.Descendants)
                {
                    var (called, arguments) = node switch
                    {
                        InvocationExpression invocation => (invocation.GetSymbol() as IMethod, invocation.Arguments.ToList()),
                        ObjectCreateExpression creation => (creation.GetSymbol() as IMethod, creation.Arguments.ToList()),
                        _ => (null, null),
                    };
                    if (called is null || !IsSame(called, definition))
                        continue;
                    found++;
                    sites.Add((caller, node, arguments));
                }
                // The IL counts the calls of every overload of the name, so a shortfall shows only without overloads.
                if (found < calls.Count && definition.DeclaringTypeDefinition.DeclaredMethods().Count(method => method.Name == definition.Name) == 1)
                    sites.Unseen = true;
            }
        }
        return _callSites[definition.MetadataToken] = sites;
    }

    private bool IsSame(IMethod called, IMethod definition) =>
        (called.MemberDefinition as IMethod ?? called).MetadataToken == definition.MetadataToken
        && called.ParentModule?.AssemblyName == _assemblyName;

    /// <summary>The default value of the parameter a call leaves out, or unknown when it has none.</summary>
    private static IReadOnlyList<Value> DefaultOf(IMethod method, int index)
    {
        var parameter = (method?.MemberDefinition as IMethod ?? method)?.Parameters.ElementAtOrDefault(index);
        if (parameter is null || !parameter.HasConstantValueInSignature)
            return UnknownOnly;
        return parameter.GetConstantValue() switch
        {
            null => [Null],
            string text => [new TextValue(text)],
            var constant when Constant(constant) is { } value => [value],
            _ => UnknownOnly,
        };
    }

    // --- Reading ------------------------------------------------------------------------------

    /// <summary>The values <paramref name="expression"/> may have in <paramref name="frame"/>; none for no expression.</summary>
    public IReadOnlyList<Value> Read(Expression expression, Frame frame)
    {
        if (expression is null || expression.IsNull)
            return [];
        if (!_reading.Add((expression, frame)))
            return UnknownOnly;
        try
        {
            return Merged(ReadCore(expression, frame));
        }
        finally
        {
            _reading.Remove((expression, frame));
        }
    }

    /// <summary>
    /// The distinct values among <paramref name="values"/>, in their order. Equal values become one
    /// that carries the origins of all of them, so that no lookup call is lost where two branches
    /// look the same member up.
    /// </summary>
    private static List<Value> Merged(IEnumerable<Value> values)
    {
        var merged = new List<Value>();
        var places = new Dictionary<Value, int>();
        foreach (var value in values)
        {
            if (places.TryGetValue(value, out var place))
                merged[place] = United(merged[place], value);
            else
            {
                places[value] = merged.Count;
                merged.Add(value);
            }
        }
        return merged;
    }

    /// <summary>One of two equal values with the origins of both, a list's elements united one by one.</summary>
    private static Value United(Value first, Value second)
    {
        if (first is ListValue list && second is ListValue other)
            return list with { Elements = list.Elements.Zip(other.Elements, United).ToList() };
        return second.Origins.All(first.Origins.Contains)
            ? first
            : first with { Origins = first.Origins.Union(second.Origins).ToList() };
    }

    /// <summary>
    /// The values the frame's member returns, read in the frame. An iterator's yields count as one
    /// list, the value an iterator returns.
    /// </summary>
    public IReadOnlyList<Value> Returns(Frame frame)
    {
        var returned = ReturnedExpressions(frame.Tree);
        var values = returned.Where(item => !item.Yields).SelectMany(item => Read(item.Expression, frame)).ToList();
        if (returned.Any(item => item.Yields))
            values.Add(new ListValue(Merged(returned.Where(item => item.Yields).SelectMany(item => Read(item.Expression, frame)))));
        return Merged(values);
    }

    /// <summary>The expressions a tree's member returns or yields, not those of a lambda or local function in it.</summary>
    public static List<(Expression Expression, bool Yields)> ReturnedExpressions(SyntaxTree tree) =>
        tree.Descendants
            .Select(node => node switch
            {
                ReturnStatement { Expression.IsNull: false } returned when !InNestedFunction(returned) => (returned.Expression, false),
                YieldReturnStatement yielded when !InNestedFunction(yielded) => (yielded.Expression, true),
                _ => (null, false),
            })
            .Where(item => item.Item1 is not null)
            .ToList();

    private static bool InNestedFunction(AstNode node) =>
        node.Ancestors.Any(ancestor => ancestor is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement);

    /// <summary>The elements of the lists among <paramref name="values"/>, and the other values as they are.</summary>
    public static IEnumerable<Value> Flatten(IEnumerable<Value> values) =>
        values.SelectMany(value => value is ListValue list ? list.Elements : [value]);

    private IEnumerable<Value> ReadCore(Expression expression, Frame frame)
    {
        // A literal or an enum's value, from what the decompiler records for the expression: it writes
        // an enum's value as its members joined by |, and only the whole expression carries the value.
        if (expression.GetResolveResult() is ConstantResolveResult { ConstantValue: var known } && Constant(known) is { } folded)
            return [folded];
        switch (expression)
        {
            case ParenthesizedExpression parenthesized:
                return Read(parenthesized.Expression, frame);
            case CastExpression cast:
                return Read(cast.Expression, frame);
            case AsExpression cast:
                return Read(cast.Expression, frame);
            case UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional or UnaryOperatorType.SuppressNullableWarning } unary:
                return Read(unary.Expression, frame);
            case UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditionalRewrap } rewrap:
                return Read(rewrap.Expression, frame);
            case NullReferenceExpression or DefaultValueExpression:
                return [Null];
            case PrimitiveExpression { Value: string text }:
                return [new TextValue(text)];
            // Flags built from a field, as in AccessTools.all & ~BindingFlags.Instance.
            case BinaryOperatorExpression { Operator: BinaryOperatorType.BitwiseOr or BinaryOperatorType.BitwiseAnd or BinaryOperatorType.ExclusiveOr } bitwise:
                return Combine(Read(bitwise.Left, frame), Read(bitwise.Right, frame), (left, right) => Bitwise(bitwise.Operator, left, right));
            case TypeOfExpression typeOf:
                return [TypeOf(typeOf.GetResolveResult() is TypeOfResolveResult resolved
                    ? resolved.ReferencedType
                    : typeOf.Type.GetResolveResult().Type) ?? Unknown];
            case ConditionalExpression conditional:
                return Read(conditional.TrueExpression, frame).Concat(Read(conditional.FalseExpression, frame));
            case BinaryOperatorExpression { Operator: BinaryOperatorType.NullCoalescing } coalescing:
                return Read(coalescing.Left, frame).Where(value => value is not NullValue).Concat(Read(coalescing.Right, frame));
            case IdentifierExpression identifier:
                return ReadIdentifier(identifier, frame);
            case MemberReferenceExpression reference:
                return ReadMember(reference, frame);
            case InvocationExpression invocation:
                return ReadCall(invocation, frame);
            case ObjectCreateExpression creation:
                return ReadCreation(creation, frame);
            case ArrayCreateExpression array:
                return [ReadArray(array, frame)];
            case IndexerExpression { Arguments.Count: 1 } indexer when Unparenthesized(indexer.Arguments.Single()) is PrimitiveExpression { Value: int index }:
                return Read(indexer.Target, frame)
                    .Select(value => value is ListValue list && index >= 0 && index < list.Elements.Count ? list.Elements[index] : Unknown);
            default:
                return UnknownOnly;
        }
    }

    private static TypeValue TypeOf(IType type) =>
        type is null or SpecialType ? null : new TypeValue(ReferenceFormatter.FormatTypeName(type), type);

    /// <summary>A constant that is a bool or an integer, the integer as a long; null for any other value.</summary>
    private static ConstantValue Constant(object value) => value switch
    {
        bool truth => new ConstantValue(truth),
        sbyte or byte or short or ushort or int or uint or long => new ConstantValue(Convert.ToInt64(value)),
        ulong large => new ConstantValue(unchecked((long)large)),
        _ => null,
    };

    private static Value Bitwise(BinaryOperatorType operation, Value left, Value right) =>
        left is ConstantValue { Constant: long first } && right is ConstantValue { Constant: long second }
            ? new ConstantValue(operation switch
            {
                BinaryOperatorType.BitwiseOr => first | second,
                BinaryOperatorType.BitwiseAnd => first & second,
                _ => first ^ second,
            })
            : Unknown;

    private IEnumerable<Value> ReadIdentifier(IdentifierExpression identifier, Frame frame)
    {
        if (identifier.GetILVariable() is { } variable)
        {
            if (frame.Variables.TryGetValue(variable, out var bound))
                return bound;
            if (variable.Kind == VariableKind.ForeachLocal)
                return ReadLoopVariable(identifier, variable, frame);
            if (variable.Kind == VariableKind.Parameter && !IsMemberParameter(variable))
                return UnknownOnly;
            return ReachingValues(identifier, VariableDefined(variable),
                found => IsMemberParameter(variable) ? ReadParameter(variable.Index!.Value, frame)
                    : found ? []
                    : UnknownOnly,
                frame);
        }
        return identifier.GetSymbol() switch
        {
            IField field => ReadField(identifier, field, frame),
            IMethod => [MethodGroup(identifier)],
            _ => UnknownOnly,
        };
    }

    private IEnumerable<Value> ReadMember(MemberReferenceExpression reference, Frame frame)
    {
        switch (reference.GetSymbol())
        {
            case IField { DeclaringType.ReflectionName: "System.Type", Name: "EmptyTypes" }:
                return [EmptyList];
            case IField { DeclaringType.ReflectionName: "HarmonyLib.AccessTools", Name: "all" or "allDeclared" } flags:
                return [new ConstantValue(flags.Name == "all" ? AccessToolsAll : AccessToolsAll | (long)System.Reflection.BindingFlags.DeclaredOnly)];
            case IField { IsConst: true } constant when Constant(constant.GetConstantValue()) is { } value:
                return [value];
            case IField field:
                return ReadField(reference, field, frame);
            case IProperty property when AccessorPrefix(property, called: false) is { } prefix:
                return Read(reference.Target, frame).Select(value => AccessorOf(value, prefix));
            case IProperty { Getter: { } getter } when FormOf(getter) is { } form:
                return LookupValues(reference, getter, form, frame);
            case IMethod:
                return [MethodGroup(reference)];
            default:
                return UnknownOnly;
        }
    }

    /// <summary>
    /// The members that take an accessor from a property or an event, by the class declaring each,
    /// its name and whether code calls it, with the prefix of the accessor's name. The reader reads
    /// a patch target through them, and <see cref="MemberLookupReader"/> names an accessor taken
    /// right at a lookup through them, so the two read the same members.
    /// </summary>
    private static readonly Dictionary<(string Type, string Name, bool Called), string> AccessorMembers = new()
    {
        [("System.Reflection.PropertyInfo", "GetGetMethod", true)] = "get_",
        [("System.Reflection.PropertyInfo", "GetMethod", false)] = "get_",
        [("System.Reflection.PropertyInfo", "GetSetMethod", true)] = "set_",
        [("System.Reflection.PropertyInfo", "SetMethod", false)] = "set_",
        [("System.Reflection.EventInfo", "GetAddMethod", true)] = "add_",
        [("System.Reflection.EventInfo", "AddMethod", false)] = "add_",
        [("System.Reflection.EventInfo", "GetRemoveMethod", true)] = "remove_",
        [("System.Reflection.EventInfo", "RemoveMethod", false)] = "remove_",
    };

    /// <summary>The prefix of the accessor <paramref name="member"/> takes, called or read, and null for a member that takes none.</summary>
    public static string AccessorPrefix(IMember member, bool called) =>
        member?.DeclaringType?.ReflectionName is { } type && AccessorMembers.TryGetValue((type, member.Name, called), out var prefix)
            ? prefix
            : null;

    /// <summary>
    /// A property's or an event's accessor, which keeps the member's origins. An indexer's takes
    /// its index types, a setter with the value's type, which the lookup does not name, after them.
    /// </summary>
    private static Value AccessorOf(Value member, string prefix) => member switch
    {
        PropertyValue found => new MethodValue(found.Type, prefix + found.Name,
            prefix == "set_" && found.ParameterTypes is not null ? [.. found.ParameterTypes, "?"] : found.ParameterTypes)
        {
            Origins = found.Origins,
        },
        EventValue found => new MethodValue(found.Type, prefix + found.Name, null) { Origins = found.Origins },
        _ => Unknown,
    };

    // --- Variables ----------------------------------------------------------------------------

    /// <summary>Whether a variable is a parameter of the tree's member rather than of a lambda or local function in it.</summary>
    private static bool IsMemberParameter(ILVariable variable) =>
        variable.Kind == VariableKind.Parameter && variable.Index is >= 0
        && variable.Function?.Kind == ILFunctionKind.TopLevelFunction;

    /// <summary>
    /// What defines the value being read: the expressions that name it, as the left of an assignment
    /// or as an out or ref argument, and the declarations that initialize it.
    /// </summary>
    private sealed record Defined(Func<Expression, bool> Names, Func<VariableInitializer, bool> Declares);

    private static Defined VariableDefined(ILVariable variable) => new(
        expression => expression is IdentifierExpression target && target.GetILVariable() == variable,
        initializer => initializer.GetILVariable() == variable);

    /// <summary>A field named by itself, through this, or through its class.</summary>
    private static Defined FieldDefined(IField field) => new(
        expression => NamesField(expression, field),
        _ => false);

    private static bool NamesField(Expression expression, IField field) =>
        expression is IdentifierExpression or MemberReferenceExpression { Target: ThisReferenceExpression or TypeReferenceExpression }
        && expression.GetSymbol() is IField named
        && (named.MemberDefinition as IField ?? named).MetadataToken == (field.MemberDefinition as IField ?? field).MetadataToken
        && named.ParentModule?.AssemblyName == field.ParentModule?.AssemblyName;

    /// <summary>
    /// The values that reach a use of a variable or field: the assignments before it, walking back over
    /// the statements of each enclosing block and out of a lambda into the code around it. An assignment
    /// at the level of the walk ends it; one inside a branch, loop or other statement adds an
    /// alternative and the walk goes on. Where the walk reaches the start of the member,
    /// <paramref name="outside"/> gives the value from outside it, told whether the walk found any. An
    /// assignment later in a loop that holds the use is not seen.
    /// </summary>
    private IEnumerable<Value> ReachingValues(AstNode use, Defined defined, Func<bool, IEnumerable<Value>> outside, Frame frame)
    {
        var values = new List<Value>();
        var statement = use.Ancestors.OfType<Statement>().FirstOrDefault();
        while (statement is not null)
        {
            if (statement.Parent is BlockStatement or SwitchSection)
            {
                for (var previous = statement.PrevSibling; previous is not null; previous = previous.PrevSibling)
                {
                    if (previous is not Statement earlier)
                        continue;
                    if (UnconditionalDefinition(earlier, defined) is { } definition)
                    {
                        values.AddRange(Definition(definition, frame));
                        return values;
                    }
                    foreach (var nested in DefinitionsIn(earlier, defined))
                        values.AddRange(Definition(nested, frame));
                }
            }

            var parent = statement.Parent;
            if (parent is BlockStatement { Parent: LambdaExpression or AnonymousMethodExpression } lambdaBody)
                statement = lambdaBody.Parent.Ancestors.OfType<Statement>().FirstOrDefault();
            else if (parent is BlockStatement { Parent: EntityDeclaration or Accessor or LocalFunctionDeclarationStatement })
                break;
            else
                statement = parent?.AncestorsAndSelf.OfType<Statement>().FirstOrDefault();
        }
        values.AddRange(outside(values.Count > 0));
        return values;
    }

    private IEnumerable<Value> ReadParameter(int index, Frame frame) =>
        frame.Parameters.TryGetValue(index, out var bindings)
            ? bindings.SelectMany(binding => binding.Values ?? Read(binding.Expression, binding.Frame))
            : UnknownOnly;

    /// <summary>
    /// The definition <paramref name="statement"/> makes on every path through it: a declaration with
    /// an initializer, or an expression statement that assigns the value.
    /// </summary>
    private static AstNode UnconditionalDefinition(Statement statement, Defined defined) => statement switch
    {
        VariableDeclarationStatement declaration =>
            declaration.Variables.FirstOrDefault(initializer => defined.Declares(initializer) && !initializer.Initializer.IsNull),
        ExpressionStatement { Expression: AssignmentExpression assignment } when defined.Names(assignment.Left) => assignment,
        _ => null,
    };

    /// <summary>The definitions anywhere inside <paramref name="statement"/>, its uses as an out or ref argument among them.</summary>
    private static IEnumerable<AstNode> DefinitionsIn(Statement statement, Defined defined) =>
        statement.DescendantsAndSelf.Where(node => node switch
        {
            VariableInitializer initializer => defined.Declares(initializer) && !initializer.Initializer.IsNull,
            AssignmentExpression assignment => defined.Names(assignment.Left),
            DirectionExpression { FieldDirection: FieldDirection.Out or FieldDirection.Ref } direction => defined.Names(direction.Expression),
            OutVarDeclarationExpression declaration => defined.Declares(declaration.Variable),
            _ => false,
        });

    /// <summary>Every value the tree holding <paramref name="use"/> assigns a variable, for <see cref="Needs"/>.</summary>
    private static IEnumerable<AstNode> AllDefinitions(IdentifierExpression use, ILVariable variable)
    {
        var defined = VariableDefined(variable);
        foreach (var node in (use.Ancestors.LastOrDefault() ?? use).Descendants)
        {
            switch (node)
            {
                case VariableInitializer initializer when defined.Declares(initializer) && !initializer.Initializer.IsNull:
                    yield return initializer.Initializer;
                    break;
                case AssignmentExpression assignment when defined.Names(assignment.Left):
                    yield return assignment.Right;
                    break;
                case ForeachStatement loop when Declares(loop, variable):
                    yield return loop.InExpression;
                    break;
            }
        }
    }

    private IEnumerable<Value> Definition(AstNode definition, Frame frame) => definition switch
    {
        VariableInitializer initializer => Read(initializer.Initializer, frame),
        AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment => Read(assignment.Right, frame),
        _ => UnknownOnly,
    };

    /// <summary>
    /// The values a foreach variable takes: the elements of a list the code writes out, or, for a loop
    /// over one class's methods, the method an exact name test picks, whose overload the code's other
    /// tests pick. Anything else is unknown.
    /// </summary>
    private IEnumerable<Value> ReadLoopVariable(IdentifierExpression use, ILVariable variable, Frame frame)
    {
        var loop = use.Ancestors.OfType<ForeachStatement>().FirstOrDefault(candidate => Declares(candidate, variable));
        if (loop is null)
            return UnknownOnly;
        return Read(loop.InExpression, frame).SelectMany(source => source switch
        {
            ListValue list => list.Elements,
            MethodsValue methods => [Searched(methods, NameTested(use, variable, loop))],
            _ => UnknownOnly,
        });
    }

    /// <summary>
    /// Whether a foreach statement declares the variable. ILSpy annotates the variable's uses but
    /// not always the statement, which then names it.
    /// </summary>
    private static bool Declares(ForeachStatement loop, ILVariable variable) =>
        (loop.GetILVariable() ?? loop.VariableDesignation.Annotation<ILVariableResolveResult>()?.Variable) is { } declared
            ? declared == variable
            : loop.VariableDesignation is SingleVariableDesignation single && single.Identifier == variable.Name;

    private static Value Searched(MethodsValue methods, string tested) =>
        (methods.Name ?? tested) is { } name
            ? new MethodValue(methods.Type, name, null, Searched: true) { Origins = methods.Origins }
            : Unknown;

    /// <summary>
    /// The name an if around <paramref name="use"/>, inside <paramref name="boundary"/>, picks the
    /// variable's method by: a conjunct <c>m.Name == "Y"</c> of a condition whose true branch holds the
    /// use. A test that excludes a name picks none.
    /// </summary>
    private static string NameTested(AstNode use, ILVariable variable, AstNode boundary)
    {
        for (var node = use; node is not null && node != boundary; node = node.Parent)
        {
            if (node.Parent is IfElseStatement branch && node == branch.TrueStatement
                && Conjuncts(branch.Condition).Select(conjunct => NameTest(conjunct, variable, null)).FirstOrDefault(name => name is not null) is { } name)
                return name;
        }
        return null;
    }

    /// <summary>The name a lambda's test picks its parameter's method by, as an if picks a loop variable's.</summary>
    private static string NameTested(LambdaExpression lambda)
    {
        if (lambda.Parameters.Count != 1 || BodyExpression(lambda) is not { } body)
            return null;
        var parameter = lambda.Parameters.Single();
        return Conjuncts(body).Select(conjunct => NameTest(conjunct, LambdaVariable(lambda), parameter.Name))
            .FirstOrDefault(name => name is not null);
    }

    /// <summary>A lambda's body as one expression: its expression, or the expression its only statement returns.</summary>
    private static Expression BodyExpression(LambdaExpression lambda) => lambda.Body switch
    {
        Expression expression => expression,
        BlockStatement { Statements.Count: 1 } block when block.Statements.Single() is ReturnStatement returned => returned.Expression,
        _ => null,
    };

    /// <summary>The variable of a lambda's only parameter, as its uses in the body resolve it.</summary>
    private static ILVariable LambdaVariable(LambdaExpression lambda)
    {
        if (lambda.Parameters.Count != 1)
            return null;
        var parameter = lambda.Parameters.Single();
        return parameter.Annotation<ILVariableResolveResult>()?.Variable
            ?? lambda.Body.Descendants.OfType<IdentifierExpression>()
                .Select(identifier => identifier.Identifier == parameter.Name ? identifier.GetILVariable() : null)
                .FirstOrDefault(variable => variable is not null);
    }

    private static IEnumerable<Expression> Conjuncts(Expression condition) => Unparenthesized(condition) switch
    {
        BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd } and => Conjuncts(and.Left).Concat(Conjuncts(and.Right)),
        var other => [other],
    };

    /// <summary>
    /// The name <c>m.Name == "Y"</c>, <c>"Y" == m.Name</c> or <c>m.Name.Equals("Y")</c> tests the
    /// variable for, the variable known by <paramref name="variable"/> or, failing that, by its name.
    /// </summary>
    private static string NameTest(Expression conjunct, ILVariable variable, string name)
    {
        bool IsName(Expression expression) =>
            Unparenthesized(expression) is MemberReferenceExpression { MemberName: "Name", Target: IdentifierExpression target }
            && (variable is not null ? target.GetILVariable() == variable : target.Identifier == name);
        static string Literal(Expression expression) =>
            Unparenthesized(expression) is PrimitiveExpression { Value: string text } ? text : null;

        return Unparenthesized(conjunct) switch
        {
            BinaryOperatorExpression { Operator: BinaryOperatorType.Equality } equality when IsName(equality.Left) => Literal(equality.Right),
            BinaryOperatorExpression { Operator: BinaryOperatorType.Equality } equality when IsName(equality.Right) => Literal(equality.Left),
            InvocationExpression { Target: MemberReferenceExpression { MemberName: "Equals" } equals, Arguments.Count: 1 } call
                when IsName(equals.Target) => Literal(call.Arguments.Single()),
            _ => null,
        };
    }

    private static Expression Unparenthesized(Expression expression) =>
        expression is ParenthesizedExpression parenthesized ? Unparenthesized(parenthesized.Expression) : expression;

    /// <summary>An expression past the parentheses and casts around it.</summary>
    private static Expression Unwrapped(Expression expression) => expression switch
    {
        ParenthesizedExpression parenthesized => Unwrapped(parenthesized.Expression),
        CastExpression cast => Unwrapped(cast.Expression),
        _ => expression,
    };

    // --- Fields -------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="node"/> names a field of <paramref name="member"/>'s own object.</summary>
    private bool IsOwnField(AstNode node, IField field, IMethod member) =>
        node is IdentifierExpression or MemberReferenceExpression { Target: ThisReferenceExpression }
        && member.DeclaringTypeDefinition is { } type
        && field.DeclaringTypeDefinition?.MetadataToken == type.MetadataToken
        && field.ParentModule?.AssemblyName == _assemblyName;

    /// <summary>
    /// A field's values. A field named by itself, through this or through its class is read as a
    /// variable is, from the assignments before the use in the member, and where none settles it, from
    /// outside the member: for a field of the frame's object, what the frame binds it to; for a static
    /// field of the assembly, every value the assembly assigns it. A field of another object is unknown.
    /// </summary>
    private IEnumerable<Value> ReadField(Expression reference, IField field, Frame frame)
    {
        if (!NamesField(reference, field))
            return UnknownOnly;
        return ReachingValues(reference, FieldDefined(field), _ => OutsideValue(reference, field, frame), frame);
    }

    private IEnumerable<Value> OutsideValue(AstNode reference, IField field, Frame frame)
    {
        if (!field.IsStatic)
        {
            return IsOwnField(reference, field, frame.Member) && frame.Fields.TryGetValue(field.Name, out var bindings)
                ? bindings.SelectMany(binding => binding.Values ?? Read(binding.Expression, binding.Frame))
                : UnknownOnly;
        }
        if (field.ParentModule?.AssemblyName != _assemblyName || field.DeclaringTypeDefinition is not { } type)
            return UnknownOnly;
        var token = (field.MemberDefinition as IField ?? field).MetadataToken;
        if (_staticFields.TryGetValue(token, out var cached))
        {
            if (ReferenceEquals(cached, BeingRead))
                _cutShort++;
            return cached;
        }
        _staticFields[token] = BeingRead;  // a field met again while its own value is read is not followed in a circle
        return _staticFields[token] = ReadStaticField(type.ReflectionName, field.Name, frame.Depth);
    }

    /// <summary>
    /// Every value the assembly assigns a static field, each read where it is assigned. Unknown is
    /// among them when the IL stores to the field where the trees do not show it, or when code calls
    /// a method on a list the field holds that may change it, such as Add.
    /// </summary>
    private IReadOnlyList<Value> ReadStaticField(string typeName, string name, int depth)
    {
        var values = new List<Value>();
        foreach (var (member, stores) in _host.MembersStoring((owner, field) => owner == typeName && field == name))
        {
            var tree = Tree(member);
            var assignments = tree?.Descendants.OfType<AssignmentExpression>()
                .Where(assignment => IsStaticField(assignment.Left, typeName, name))
                .ToList() ?? [];
            if (assignments.Count < stores.Count)
                values.Add(Unknown);
            foreach (var assignment in assignments)
            {
                if (assignment.Operator != AssignmentOperatorType.Assign)
                {
                    values.Add(Unknown);
                    continue;
                }
                foreach (var assigning in Frames(member, tree, [assignment.Right], depth + 1))
                    values.AddRange(Read(assignment.Right, assigning));
            }
        }
        if (values.Any(value => value is ListValue) && ChangesList(typeName, name))
            values.Add(Unknown);
        return values.Count == 0 ? [Null] : Merged(values);
    }

    private static bool IsStaticField(AstNode node, string typeName, string name) =>
        node.GetSymbol() is IField { IsStatic: true } field && field.Name == name && field.DeclaringType.ReflectionName == typeName;

    /// <summary>
    /// Whether code may change what the static field holds: a call on it to a method that is not one of
    /// a list's reading methods, or a call that takes it as an argument, which in the positional tree
    /// is also how an extension method that adds to the list reaches it. LINQ's methods only
    /// read their source.
    /// </summary>
    private bool ChangesList(string typeName, string name)
    {
        string[] reading = ["GetEnumerator", "Contains", "IndexOf", "ToArray", "get_Count", "get_Item", "ToString", "GetHashCode", "Equals", "GetType"];
        foreach (var (member, _) in _host.MembersLoading((owner, field) => owner == typeName && field == name))
        {
            if (Tree(member) is not { } tree)
                return true;
            foreach (var call in tree.Descendants.OfType<InvocationExpression>())
            {
                var method = call.GetSymbol() as IMethod;
                var onField = call.Target is MemberReferenceExpression { Target: var receiver } && IsStaticField(receiver, typeName, name)
                    && method is not { IsStatic: true };
                var passesField = call.Arguments.Any(argument => IsStaticField(Unwrapped(argument), typeName, name));
                if (onField && (method is null || !reading.Contains(method.Name)))
                    return true;
                if (passesField && method?.DeclaringType?.ReflectionName != "System.Linq.Enumerable")
                    return true;
            }
        }
        return false;
    }

    // --- Calls --------------------------------------------------------------------------------

    private IEnumerable<Value> ReadCall(InvocationExpression invocation, Frame frame)
    {
        if (invocation.GetSymbol() is not IMethod method)
            return UnknownOnly;
        var arguments = invocation.Arguments.ToList();
        var receiver = method.IsStatic ? null : (invocation.Target as MemberReferenceExpression)?.Target;
        if (FormOf(method) is { } form)
            return LookupValues(invocation, method, form, frame);
        if (AccessorPrefix(method, called: true) is { } accessor)
            return Read(receiver, frame).Select(value => AccessorOf(value, accessor));
        switch (method.DeclaringType?.ReflectionName, method.Name)
        {
            case ("HarmonyLib.AccessTools", "TypeByName"):
            case ("System.Type", "GetType") when method.IsStatic:
            case ("System.Reflection.Assembly", "GetType") when !method.IsStatic:
                return Texts(arguments.ElementAtOrDefault(0), frame).Select(TextType);
            case ("System.Object", "GetType") when receiver is null or ThisReferenceExpression:
                return [TypeOf(frame.Member.DeclaringTypeDefinition) ?? Unknown];
            case ("System.Type", "MakeByRefType"):
                return Read(receiver, frame)
                    .Select(value => value is TypeValue type ? new ByReferenceValue(type) { Origins = type.Origins } : Unknown);
            case ("HarmonyLib.AccessTools" or "HarmonyLib.AccessToolsExtensions", "Inner"):
                return Combine(Types(arguments.ElementAtOrDefault(0), frame), Texts(arguments.ElementAtOrDefault(1), frame), Nested);
            case ("System.Type", "GetNestedType"):
                return Combine(Types(receiver, frame), Texts(arguments.ElementAtOrDefault(0), frame), Nested);
            // Code the compiler generates for a method, which Harmony finds from the method: the MoveNext
            // of its iterator or async state machine, its lambdas, and a local function.
            case ("HarmonyLib.AccessTools", "EnumeratorMoveNext" or "AsyncMoveNext" or "StateMachineMoveNext"):
                var kind = method.Name switch { "EnumeratorMoveNext" => "Enumerator", "AsyncMoveNext" => "Async", _ => "StateMachine" };
                return Read(arguments.ElementAtOrDefault(0), frame)
                    .Select(value => value is MethodValue found ? found with { MethodType = kind } : Unknown);
            // A list whose one element stands for every lambda of the method.
            case ("HarmonyLib.AccessTools", "Lambdas"):
                return Read(arguments.ElementAtOrDefault(0), frame)
                    .Select(value => value is MethodValue found ? new ListValue([found with { MethodType = "Lambdas" }]) : Unknown);
            case ("HarmonyLib.AccessTools", "LocalFunction"):
                return ReadLocalFunction(arguments, frame);
            // A generic method made for type arguments is patched or called as made, so the value keeps them.
            // Several methods and several lists of type arguments are not paired.
            case ("System.Reflection.MethodInfo", "MakeGenericMethod"):
                var made = TypeArgumentLists(arguments, frame);
                var methods = Read(receiver, frame);
                if (methods.Count > 1 && made.Count > 1)
                    return UnknownOnly;
                return methods.SelectMany(value => value is MethodValue method
                    ? made.Select(typeArguments => (Value)(method with { TypeArguments = typeArguments }))
                    : UnknownOnly);
            case ("HarmonyLib.HarmonyMethod", "op_Implicit"):
                return Read(arguments.ElementAtOrDefault(0), frame).Select(PatchMethodOf);
            case ("System.Array", "Empty"):
                return [EmptyList];
            case ("System.Linq.Enumerable", _):
                return ReadEnumerable(method, arguments, frame);
            case ("HarmonyLib.Traverse", "Create" or "CreateWithType"):
                return ReadTraverseCreation(method, invocation, arguments, frame);
        }
        return IsOwn(method) ? ReadOwnCall(method, arguments, receiver, frame) : UnknownOnly;
    }

    /// <summary>
    /// The method a LocalFunction call is given, marked with the local function's name, '?' where
    /// not read, and its parameter types where the code gives them. Several methods, names and lists
    /// of parameter types are not paired.
    /// </summary>
    private IEnumerable<Value> ReadLocalFunction(List<Expression> arguments, Frame frame)
    {
        var methods = Read(arguments.ElementAtOrDefault(0), frame);
        var names = Texts(arguments.ElementAtOrDefault(1), frame);
        IReadOnlyList<IReadOnlyList<string>> overloads = arguments.ElementAtOrDefault(2) is { } array ? ParameterTypes(array, frame) : [null];
        if (new[] { methods.Count, names.Count, overloads.Count }.Count(count => count > 1) > 1)
            return UnknownOnly;
        return methods.SelectMany(value => names.SelectMany(name => overloads.Select(pinned => value is MethodValue found
            ? found with { MethodType = LocalFunctionMark(name, pinned) }
            : Unknown)));
    }

    /// <summary>The mark of a local function: LocalFunction, its name and its parameter types where given, LocalFunction Fill(byte[], int).</summary>
    private static string LocalFunctionMark(Value name, IReadOnlyList<string> parameterTypes) =>
        $"LocalFunction {(name as TextValue)?.Text ?? "?"}" + (parameterTypes is null ? "" : $"({string.Join(", ", parameterTypes)})");

    private bool IsOwn(IMethod method) =>
        method.ParentModule?.AssemblyName == _assemblyName && !method.IsAbstract && method.HasBody;

    /// <summary>
    /// What an own method returns for this call, read with its parameters bound to the values of the
    /// call's arguments and, for a call on the frame's own object, its object's fields to the values the
    /// frame gives them. Those values are read in the caller's frame first, and what the method returns
    /// is remembered for the scan by <see cref="OwnCall"/>. Without it, a scan of Harmony's own library
    /// followed calls into own methods 2.6 million times, nearly all in six lookups on the results of a
    /// resolver that calls itself in what it returns, each read in every chain of the resolver's calls.
    /// </summary>
    /// <remarks>
    /// Every parameter and every field the frame binds is read, also one the returned expressions do
    /// not name: the reading reaches values through assignments inside the method that
    /// <see cref="Needs"/> does not follow, such as one to a field the method keeps a lookup in. A
    /// remembered result holds for every call with the same key because the method is read in frames
    /// made for it alone, which bind nothing to an expression of the caller's frame, so the reader's check
    /// for a circle can cut the reading short only inside it, the same way for each such call, and because
    /// a frame's origin, which the key leaves out, names where a patch call's values are written out and
    /// makes no value. What can
    /// cut it short from outside is a static field met while its own value is read, whose placeholder
    /// depends on where the reading started, so a result read past one is not remembered.
    /// </remarks>
    private IEnumerable<Value> ReadOwnCall(IMethod method, List<Expression> arguments, Expression receiver, Frame frame)
    {
        if (frame.Depth >= MaxDepth || Tree(method) is not { } tree)
            return UnknownOnly;
        var definition = method.MemberDefinition as IMethod ?? method;
        List<(int Index, IReadOnlyList<Value> Values)> parameters = Enumerable.Range(0, definition.Parameters.Count)
            .Select(index => (Index: index,
                Values: arguments.ElementAtOrDefault(index) is { } argument ? Read(argument, frame) : DefaultOf(method, index)))
            .ToList();
        List<(string Field, IReadOnlyList<Value> Values)> fields = !method.IsStatic && receiver is null or ThisReferenceExpression
            ? frame.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (Field: pair.Key, Values: (IReadOnlyList<Value>)Merged(pair.Value
                    .SelectMany(binding => binding.Values ?? Read(binding.Expression, binding.Frame)))))
                .ToList()
            : [];
        var call = new OwnCall(definition.MetadataToken, frame.Depth + 1, parameters, fields);
        if (_returns.TryGetValue(call, out var remembered))
            return remembered;

        var callee = new Frame(method, tree, frame.Origin, frame.Depth + 1)
            .WithParameters(parameters.ToDictionary(parameter => parameter.Index,
                parameter => (IReadOnlyList<Binding>)[Binding.Of(parameter.Values)]), frame.Origin)
            .WithFields(fields.ToDictionary(field => field.Field,
                field => (IReadOnlyList<Binding>)[Binding.Of(field.Values)]), frame.Origin);
        var cutShort = _cutShort;
        var values = Returns(callee);
        var returned = values.Count == 0 ? UnknownOnly : values;
        if (_cutShort == cutShort)
            _returns[call] = returned;
        return returned;
    }

    /// <summary>
    /// A call of an own method as what the method returns depends on it: the method, the depth it is
    /// read at, and the values of its parameters by position and of its object's fields by name, each
    /// compared by <see cref="SameValue"/>.
    /// </summary>
    private sealed record OwnCall(EntityHandle Method, int Depth, IReadOnlyList<(int Index, IReadOnlyList<Value> Values)> Parameters,
        IReadOnlyList<(string Field, IReadOnlyList<Value> Values)> Fields)
    {
        public bool Equals(OwnCall other) =>
            other is not null && Method == other.Method && Depth == other.Depth
            && Parameters.Count == other.Parameters.Count
            && Parameters.Zip(other.Parameters).All(pair => pair.First.Index == pair.Second.Index && SameValues(pair.First.Values, pair.Second.Values))
            && Fields.Count == other.Fields.Count
            && Fields.Zip(other.Fields).All(pair => pair.First.Field == pair.Second.Field && SameValues(pair.First.Values, pair.Second.Values));

        // A value's hash leaves out what value equality leaves out and SameValue compares.
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Method);
            hash.Add(Depth);
            foreach (var values in Parameters.Select(parameter => parameter.Values).Concat(Fields.Select(field => field.Values)))
            {
                hash.Add(values.Count);
                foreach (var value in values)
                    hash.Add(value);
            }
            return hash.ToHashCode();
        }
    }

    private static bool SameValues(IReadOnlyList<Value> first, IReadOnlyList<Value> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => SameValue(pair.First, pair.Second));

    /// <summary>
    /// Whether two values read the same in everything the reader reads of them: equal, with the same
    /// origins, since the values a method returns carry on those it is given and a patch line that
    /// shows a lookup's result names the lookup by them, with their class resolved to the same type and
    /// named by the same text, which value equality leaves out and the reader reads, as for a class
    /// nested in it or a parameter type, and a list's elements the same one by one.
    /// </summary>
    private static bool SameValue(Value first, Value second) =>
        first.Equals(second)
        && first.Origins.Count == second.Origins.Count && first.Origins.All(second.Origins.Contains)
        && ClassRead(first) == ClassRead(second)
        && (first is not ListValue list || SameValues(list.Elements, ((ListValue)second).Elements));

    /// <summary>What a value's class holds beside its name: the type the tree resolves or the scan finds, by its reflection name, and the text it keeps for a class not found.</summary>
    private static (string Resolved, string Text) ClassRead(Value value) =>
        (value switch
        {
            TypeValue type => type,
            ByReferenceValue reference => reference.Element,
            MethodValue method => method.Type,
            PropertyValue property => property.Type,
            EventValue @event => @event.Type,
            PatchMethodValue patch => patch.Class,
            MethodsValue methods => methods.Type,
            TraverseValue traverse => traverse.Type,
            _ => null,
        }) is { } named
            ? (named.Type?.ReflectionName, named.Text)
            : default;

    // --- Lookups ------------------------------------------------------------------------------

    /// <summary>What a lookup looks for.</summary>
    private enum LookupKind
    {
        Method, Getter, Setter, Property, Constructor, TypeInitializer, IndexerGetter, IndexerSetter, Indexer,
        Event, Adder, Remover, Finalizer, Token, Delegate, DelegateType, CalledMethod, Methods, Properties,
        Constructors, Events, Members,
    }

    /// <summary>
    /// A form of lookup: the label its lines carry, what it looks for, and whether the reader
    /// follows what it returns, which it does for a method, property, event or constructor and for
    /// a list of a class's methods, and not for a Traverse, a delegate, a search, a list of names,
    /// a list of a class's properties, constructors, events or members, a method named by its
    /// metadata token, or a transpiler's instruction or match.
    /// </summary>
    private sealed record LookupForm(string Label, LookupKind Kind, bool Followed);

    /// <summary>
    /// The lookups of methods, properties, events and constructors, by the full name of the type
    /// declaring each and its name, a property's by its getter, which the IL calls, and a
    /// constructor's as .ctor: Harmony's in AccessTools and their extension spelling, which takes
    /// the label of the call it spells, Traverse's, the method a SymbolExtensions lambda names, the
    /// methods a transpiler names to CodeInstruction and CodeMatch, and .NET's on Type. A label is
    /// the call's name, with AccessTools or Traverse before it where the bare name is a kind of
    /// member, a property's name, and a constructor's class. A form is read the same way wherever it
    /// stands, as a patch target and as a lookup the assembly makes to call what it finds.
    /// </summary>
    private static readonly Dictionary<(string Type, string Name), LookupForm> LookupForms = BuildLookupForms();

    private static Dictionary<(string Type, string Name), LookupForm> BuildLookupForms()
    {
        var forms = new Dictionary<(string Type, string Name), LookupForm>();
        void AccessTools(string name, LookupKind kind, bool followed = true, string label = null)
        {
            forms[("HarmonyLib.AccessTools", name)] = new LookupForm(label ?? name, kind, followed);
            forms[("HarmonyLib.AccessToolsExtensions", name)] = new LookupForm(label ?? name, kind, followed);
        }

        AccessTools("Method", LookupKind.Method, label: "AccessToolsMethod");
        AccessTools("DeclaredMethod", LookupKind.Method);
        AccessTools("PropertyGetter", LookupKind.Getter);
        AccessTools("DeclaredPropertyGetter", LookupKind.Getter);
        AccessTools("PropertySetter", LookupKind.Setter);
        AccessTools("DeclaredPropertySetter", LookupKind.Setter);
        AccessTools("Property", LookupKind.Property, label: "AccessToolsProperty");
        AccessTools("DeclaredProperty", LookupKind.Property);
        AccessTools("Constructor", LookupKind.Constructor, label: "AccessToolsConstructor");
        AccessTools("DeclaredConstructor", LookupKind.Constructor);
        AccessTools("Event", LookupKind.Event, label: "AccessToolsEvent");
        AccessTools("DeclaredEvent", LookupKind.Event);
        AccessTools("EventAdder", LookupKind.Adder);
        AccessTools("DeclaredEventAdder", LookupKind.Adder);
        AccessTools("EventRemover", LookupKind.Remover);
        AccessTools("DeclaredEventRemover", LookupKind.Remover);
        AccessTools("Finalizer", LookupKind.Finalizer, label: "AccessToolsFinalizer");
        AccessTools("DeclaredFinalizer", LookupKind.Finalizer);
        AccessTools("IndexerGetter", LookupKind.IndexerGetter);
        AccessTools("DeclaredIndexerGetter", LookupKind.IndexerGetter);
        AccessTools("IndexerSetter", LookupKind.IndexerSetter);
        AccessTools("DeclaredIndexerSetter", LookupKind.IndexerSetter);
        AccessTools("Indexer", LookupKind.Indexer);
        AccessTools("DeclaredIndexer", LookupKind.Indexer);
        AccessTools("GetDeclaredMethods", LookupKind.Methods);
        AccessTools("FirstMethod", LookupKind.Methods, followed: false);
        AccessTools("GetMethodNames", LookupKind.Methods, followed: false);
        AccessTools("GetDeclaredProperties", LookupKind.Properties, followed: false);
        AccessTools("FirstProperty", LookupKind.Properties, followed: false);
        AccessTools("GetPropertyNames", LookupKind.Properties, followed: false);
        AccessTools("GetDeclaredConstructors", LookupKind.Constructors, followed: false);
        AccessTools("FirstConstructor", LookupKind.Constructors, followed: false);
        forms[("HarmonyLib.AccessTools", "GetMethodByModuleAndToken")] = new LookupForm("GetMethodByModuleAndToken", LookupKind.Token, false);
        forms[("HarmonyLib.AccessTools", "MethodDelegate")] = new LookupForm("MethodDelegate", LookupKind.Delegate, false);
        forms[("HarmonyLib.AccessTools", "HarmonyDelegate")] = new LookupForm("HarmonyDelegate", LookupKind.DelegateType, false);
        forms[("HarmonyLib.SymbolExtensions", "GetMethodInfo")] = new LookupForm("GetMethodInfo", LookupKind.CalledMethod, true);
        // A transpiler's helpers: CodeInstruction.Call names a method by its class and name, as
        // text or in a lambda; CodeMatch's Calls and constructor name one in a lambda.
        forms[("HarmonyLib.CodeInstruction", "Call")] = new LookupForm("Call", LookupKind.Method, false);
        forms[("HarmonyLib.CodeMatch", "Calls")] = new LookupForm("Calls", LookupKind.CalledMethod, false);
        forms[("HarmonyLib.CodeMatch", ".ctor")] = new LookupForm("CodeMatch", LookupKind.CalledMethod, false);

        forms[("HarmonyLib.Traverse", "Method")] = new LookupForm("TraverseMethod", LookupKind.Method, false);
        forms[("HarmonyLib.Traverse", "Property")] = new LookupForm("TraverseProperty", LookupKind.Property, false);
        forms[("HarmonyLib.Traverse", "Methods")] = new LookupForm("TraverseMethods", LookupKind.Methods, false);
        forms[("HarmonyLib.Traverse", "Properties")] = new LookupForm("TraverseProperties", LookupKind.Properties, false);

        forms[("System.Type", "GetMethod")] = new LookupForm("GetMethod", LookupKind.Method, true);
        forms[("System.Type", "GetProperty")] = new LookupForm("GetProperty", LookupKind.Property, true);
        forms[("System.Type", "GetEvent")] = new LookupForm("GetEvent", LookupKind.Event, true);
        forms[("System.Type", "GetConstructor")] = new LookupForm("GetConstructor", LookupKind.Constructor, true);
        forms[("System.Type", "get_TypeInitializer")] = new LookupForm("TypeInitializer", LookupKind.TypeInitializer, true);
        forms[("System.Type", "GetMethods")] = new LookupForm("GetMethods", LookupKind.Methods, true);
        forms[("System.Type", "GetProperties")] = new LookupForm("GetProperties", LookupKind.Properties, false);
        forms[("System.Type", "GetEvents")] = new LookupForm("GetEvents", LookupKind.Events, false);
        forms[("System.Type", "GetConstructors")] = new LookupForm("GetConstructors", LookupKind.Constructors, false);
        forms[("System.Type", "GetMembers")] = new LookupForm("GetMembers", LookupKind.Members, false);
        return forms;
    }

    /// <summary>
    /// What one lookup call names: the first word of its line, Method, Property, Event or Member; the
    /// class, null where not read; the member as a line names it, null where the call names none or
    /// its name is not read; the parameter types that pin an overload, null where none do; and the
    /// value the call returns, unknown where the class or the name is not read or the reader does
    /// not follow what the form returns.
    /// </summary>
    public sealed record Lookup(string Word, TypeValue Type, string Member, IReadOnlyList<string> ParameterTypes, Value Value);

    /// <summary>Whether a call to the method <paramref name="name"/> of <paramref name="type"/> looks up a method, property, event or constructor.</summary>
    public static bool IsMemberLookup(string type, string name) => LookupForms.ContainsKey((type, name));

    /// <summary>The label of a lookup's lines.</summary>
    public static string LabelOf((string Type, string Name) lookup) => LookupForms[lookup].Label;

    /// <summary>The first word of a lookup's lines.</summary>
    public static string WordOf((string Type, string Name) lookup) => WordOf(LookupForms[lookup].Kind);

    private static string WordOf(LookupKind kind) => kind switch
    {
        LookupKind.Property or LookupKind.Indexer or LookupKind.Properties => "Property",
        LookupKind.Event or LookupKind.Events => "Event",
        LookupKind.Members => "Member",
        _ => "Method",
    };

    private static LookupForm FormOf(IMethod method) =>
        method.DeclaringType?.ReflectionName is { } type && LookupForms.TryGetValue((type, method.Name), out var form) ? form : null;

    /// <summary>
    /// What a lookup call or property read names, read in <paramref name="frame"/>: one entry for
    /// each class and name the frame gives it. None for an expression that is no lookup, and none for
    /// a call that takes a method already looked up, which the lookup that found it names.
    /// </summary>
    public IReadOnlyList<Lookup> Lookups(Expression lookup, Frame frame) =>
        LookupMethod(lookup) is { } method && FormOf(method) is { } form ? ReadLookup(lookup, method, form, frame) : [];

    /// <summary>
    /// The method a lookup expression calls: a call's method, an object creation's constructor, or
    /// the getter of a property read.
    /// </summary>
    public static IMethod LookupMethod(Expression lookup) => lookup switch
    {
        InvocationExpression invocation => invocation.GetSymbol() as IMethod,
        ObjectCreateExpression creation => creation.GetSymbol() as IMethod,
        MemberReferenceExpression reference => (reference.GetSymbol() as IProperty)?.Getter,
        _ => null,
    };

    /// <summary>The arguments of a call or an object creation, in parameter order; none for a property read.</summary>
    public static IEnumerable<Expression> ArgumentsOf(Expression lookup) => lookup switch
    {
        InvocationExpression invocation => invocation.Arguments,
        ObjectCreateExpression creation => creation.Arguments,
        _ => [],
    };

    /// <summary>Whether a parameter type, by its reflection name, is an expression tree a lambda converts to.</summary>
    public static bool IsExpressionTree(string reflectionName) =>
        reflectionName == "System.Linq.Expressions.LambdaExpression"
        || reflectionName.StartsWith("System.Linq.Expressions.Expression`1", StringComparison.Ordinal);

    /// <summary>
    /// Whether what a call of the lookup <paramref name="method"/> names depends on the frame it is
    /// read in. It does not for an overload that reads a lambda written at the call, for one of a
    /// form that names a method only in a lambda, which without one names nothing, nor for
    /// AccessTools.HarmonyDelegate, which reads its type argument, so no caller need be read for them.
    /// </summary>
    public static bool ReadsInFrames(IMethod method) =>
        FormOf(method) is not { Kind: LookupKind.CalledMethod or LookupKind.DelegateType }
        && !ParameterTypeNames(method).Any(IsExpressionTree);

    /// <summary>The values a lookup makes, each with the lookup as its origin.</summary>
    private IEnumerable<Value> LookupValues(Expression lookup, IMethod method, LookupForm form, Frame frame)
    {
        var made = ReadLookup(lookup, method, form, frame)
            .Select(found => found.Value is UnknownValue ? Unknown : found.Value with { Origins = [new Origin(lookup, found.Value)] })
            .ToList();
        return made.Count == 0 ? UnknownOnly : made;
    }

    /// <summary>
    /// The entries of one lookup call or property read. The class comes from the receiver, for a
    /// Traverse the class it was created over; from its Type argument; from "Namespace.Type:Name"
    /// text, which Harmony splits at its one colon, looking the type up with AccessTools.TypeByName;
    /// or from the decompiler's type of the object it is given. The name comes from its string
    /// argument. A Type[] of parameter types pins a method's or constructor's overload or names an
    /// indexer's index types, and the second Type[] of AccessTools' method lookups gives the type
    /// arguments a generic method is made for. A constructor lookup's flag says whether it asks for
    /// the static constructor, see <see cref="StaticReadings"/>. Several classes and several names are
    /// not paired, nor are several of either and several overloads, nor several lists of type
    /// arguments or several values of the flag and several of any other part; the call then gives one
    /// entry with nothing read. An overload that takes an expression tree names the method its
    /// lambda names, see <see cref="LambdaMethod"/>; one of a form that names a method only so,
    /// without one, takes a method found elsewhere, whose lookup has a line of its own, or names no
    /// member. AccessTools.HarmonyDelegate names the method its type argument's Harmony attributes
    /// name, and nothing it reads where the type argument is a type parameter or a delegate type of
    /// another assembly.
    /// </summary>
    private List<Lookup> ReadLookup(Expression lookup, IMethod method, LookupForm form, Frame frame)
    {
        var kind = form.Kind;
        var parameters = ParameterTypeNames(method);
        var arguments = ArgumentsOf(lookup).ToList();
        var lambdaAt = parameters.FindIndex(IsExpressionTree);
        if (kind == LookupKind.CalledMethod || lambdaAt >= 0)
        {
            if (lambdaAt < 0)
                return [];
            return [CalledMethod(arguments.ElementAtOrDefault(lambdaAt)) is MethodValue called
                ? new Lookup(WordOf(LookupKind.CalledMethod), called.Type, called.Member, called.ParameterTypes, form.Followed ? called : Unknown)
                : Unread(LookupKind.CalledMethod)];
        }
        if (kind == LookupKind.DelegateType)
        {
            return [lookup is InvocationExpression invocation
                && TypeArgumentType(method, invocation)?.Type?.GetDefinition() is { } type
                && _delegateTarget(type) is { } target
                    ? new Lookup(WordOf(kind), target.Class is null ? null : new TypeValue(target.Class, _host.Resolver.DefinitionOf(target.Class)),
                        target.Member, target.ParameterTypes, Unknown)
                    : Unread(kind)];
        }
        // A module's ID and a metadata token name a method only in one build of that module.
        if (kind == LookupKind.Token)
            return [Unread(kind)];
        if (parameters.Contains("System.Reflection.MethodInfo"))
            return [];

        var typeAt = parameters.IndexOf("System.Type");
        var nameAt = parameters.IndexOf("System.String");
        var arraysAt = parameters.IndexOf("System.Type[]");
        var names = kind is LookupKind.Method or LookupKind.Getter or LookupKind.Setter or LookupKind.Property or LookupKind.Event
            or LookupKind.Adder or LookupKind.Remover or LookupKind.Delegate
            ? Texts(nameAt >= 0 ? arguments.ElementAtOrDefault(nameAt) : null, frame)
            : null;

        List<(TypeValue Type, string Name)> named;
        if (method.DeclaringType.ReflectionName is "HarmonyLib.Traverse" or "System.Type")
        {
            // A call the tree shows without a receiver, as in a class that derives from Traverse, names no class.
            var receiver = ((lookup as InvocationExpression)?.Target ?? lookup) is MemberReferenceExpression reference ? reference.Target : null;
            IReadOnlyList<Value> classes = receiver is null ? UnknownOnly
                : method.DeclaringType.ReflectionName == "System.Type" ? Types(receiver, frame)
                : Read(receiver, frame).Select(value => value is TraverseValue { Type: { } traversed } ? traversed : Unknown).Distinct().ToList();
            named = Pairs(classes, names);
        }
        else if (typeAt >= 0)
            named = Pairs(Types(arguments.ElementAtOrDefault(typeAt), frame), names);
        else if (names is null)
            named = [(ObjectType(arguments.ElementAtOrDefault(0)), null)];
        else
        {
            named = names
                .Select(text => text is TextValue { Text: var joined } && joined.Split(':') is [var type, var name]
                    ? (TextType(new TextValue(type)) as TypeValue, name)
                    : (null, null))
                .Distinct()
                .ToList();
        }

        var array = arraysAt >= 0 ? arguments.ElementAtOrDefault(arraysAt) : null;

        // AccessTools' method lookups take a second Type[], the type arguments to make a generic
        // method for; null makes none.
        var genericsAt = kind == LookupKind.Method ? parameters.LastIndexOf("System.Type[]") : -1;
        IReadOnlyList<IReadOnlyList<string>> typeArgumentLists = [null];
        if (genericsAt > arraysAt && arguments.ElementAtOrDefault(genericsAt) is { } generics)
        {
            typeArgumentLists = Read(generics, frame)
                .Select(value => value switch
                {
                    NullValue => null,
                    ListValue list => list.Elements.Select(ParameterTypeName).ToList(),
                    _ => (IReadOnlyList<string>)["?"],
                })
                .Distinct(ParameterListComparer.Instance)
                .ToList();
        }

        List<IReadOnlyList<string>> overloads;
        IReadOnlyList<bool?> readings = [false];
        switch (kind)
        {
            // Without parameter types a constructor lookup finds the parameterless one.
            case LookupKind.Constructor:
                readings = StaticReadings(method, parameters, arguments, frame);
                overloads = array is not null && !Read(array, frame).All(value => value is NullValue) ? ParameterTypes(array, frame) : [[]];
                break;
            case LookupKind.Constructors:
                readings = StaticReadings(method, parameters, arguments, frame);
                overloads = [null];
                break;
            case LookupKind.Method or LookupKind.IndexerGetter or LookupKind.IndexerSetter or LookupKind.Indexer when array is not null:
                overloads = ParameterTypes(array, frame);
                break;
            default:
                overloads = [null];
                break;
        }
        if ((overloads.Count > 1 && named.Count > 1)
            || ((typeArgumentLists.Count > 1 || readings.Count > 1) && (overloads.Count > 1 || named.Count > 1)))
            return [Unread(kind)];
        return named.SelectMany(pair => overloads
            .SelectMany(pinned => readings.SelectMany(reading => ConstructorsNamed(reading, pinned)))
            .SelectMany(shape => typeArgumentLists.Select(typeArguments =>
                Made(form, pair.Type, pair.Name, shape.Pinned, shape.IsStatic, typeArguments)))).ToList();
    }

    private const long StaticFlag = (long)System.Reflection.BindingFlags.Static;
    private const long InstanceFlag = (long)System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// The binding flags AccessTools.all holds, as Harmony 2.3.6, 2.4.3 and 3.0's release candidate
    /// define it: members of every accessibility, instance and static. AccessTools.allDeclared adds
    /// DeclaredOnly.
    /// </summary>
    private const long AccessToolsAll = (long)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.GetField | System.Reflection.BindingFlags.SetField
        | System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.SetProperty);

    /// <summary>
    /// What a constructor lookup's flag asks for, one entry for each value the flag may hold: true
    /// for the static constructor alone, false for instance constructors, alone or beside the static
    /// one, and null where the reader cannot follow the flag, which may ask for either. The flag is
    /// AccessTools' searchForStatic, true for the static constructor alone, false for instance
    /// constructors and, for GetDeclaredConstructors, null for both kinds; or the BindingFlags of
    /// .NET's GetConstructor and GetConstructors, which ask for the static constructor alone where
    /// they hold Static and not Instance. A lookup without such a parameter asks for instance
    /// constructors, as GetConstructor(Type[]) does, or for both kinds, as FirstConstructor does. An
    /// argument the tree leaves out, as the decompiler does with one at its default when it can load
    /// the lookup's library, is the parameter's default.
    /// </summary>
    /// <remarks>
    /// A lookup that asks for both kinds is read as asking for instance constructors. A single
    /// lookup, such as one given AccessTools.all, is written for the instance constructor: with
    /// parameter types only an instance constructor can match, and without, reflection finds the
    /// static constructor where the class has no parameterless instance constructor, and fails with
    /// an ambiguous match where it has both. A list or a search over both kinds holds the static
    /// constructor wherever the class has one, so its line names the instance constructors, which
    /// such code looks for, and names the static constructor only where the lookup is limited to it.
    /// Flags that hold neither kind find no constructor and are read as asking for instance ones, as
    /// the list reads no lookup's flags for what they leave out. A mod that looks up a static
    /// constructor through flags that hold both, or picks it in a list or a search, would show this
    /// reading wrong for that mod. On 2026-10-30, none of the 381 library files of a game's installed
    /// mods passed flags that hold Static, or a searchForStatic that is not a literal false, to a
    /// constructor lookup, and none listed or searched a class's constructors.
    /// </remarks>
    private IReadOnlyList<bool?> StaticReadings(IMethod method, List<string> parameters, List<Expression> arguments, Frame frame)
    {
        var at = parameters.FindIndex(name => name is "System.Boolean" or "System.Nullable`1[[System.Boolean]]" or "System.Reflection.BindingFlags");
        if (at < 0)
            return [false];
        var values = arguments.ElementAtOrDefault(at) is { } argument ? Read(argument, frame) : DefaultOf(method, at);
        return values
            .Select(value => value switch
            {
                ConstantValue { Constant: bool truth } => truth,
                ConstantValue { Constant: long flags } => (flags & StaticFlag) != 0 && (flags & InstanceFlag) == 0,
                NullValue => false,
                _ => (bool?)null,
            })
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The constructors a lookup names for one reading of its flag and one overload: an instance
    /// constructor with the overload's parameter types; the static constructor, which takes none,
    /// without them where they are empty or not read, and with them where the lookup asks for it
    /// alone, so that the line names what it asks for, which no class has. Where the flag is not
    /// read, both, but the static constructor only where it could match.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<string> Pinned, bool IsStatic)> ConstructorsNamed(bool? reading, IReadOnlyList<string> pinned)
    {
        var parameterless = pinned is null or { Count: 0 };
        if (reading != true)
            yield return (pinned, false);
        if (reading == true || (reading is null && parameterless))
            yield return (parameterless ? null : pinned, true);
    }

    /// <summary>
    /// The classes and names a lookup is given, paired, each null where not read; a form that names
    /// no member has no names. Several classes and several names give one pair with neither, since
    /// which goes with which is not known.
    /// </summary>
    private static List<(TypeValue Type, string Name)> Pairs(IReadOnlyList<Value> types, IReadOnlyList<Value> names)
    {
        if (names is null)
            return types.Select(type => (type as TypeValue, (string)null)).Distinct().ToList();
        if (types.Count > 1 && names.Count > 1)
            return [(null, null)];
        return types.SelectMany(type => names.Select(name => (type as TypeValue, (name as TextValue)?.Text))).Distinct().ToList();
    }

    /// <summary>
    /// The entry for one class, name and overload. An accessor is named as the other tools take it,
    /// and a finalizer as Finalize; an indexer by C#'s default name, Item, which <c>[IndexerName]</c>
    /// can change, its setter taking the index types and then the value, whose type the lookup does
    /// not name, written '?'. A method made for <paramref name="typeArguments"/> is named with them.
    /// </summary>
    private static Lookup Made(LookupForm form, TypeValue type, string name, IReadOnlyList<string> pinned, bool isStatic,
        IReadOnlyList<string> typeArguments)
    {
        var member = form.Kind switch
        {
            LookupKind.Method or LookupKind.Property or LookupKind.Event or LookupKind.Delegate => name,
            LookupKind.Getter => name is null ? null : "get_" + name,
            LookupKind.Setter => name is null ? null : "set_" + name,
            LookupKind.Adder => name is null ? null : "add_" + name,
            LookupKind.Remover => name is null ? null : "remove_" + name,
            LookupKind.Finalizer => "Finalize",
            LookupKind.Constructor or LookupKind.Constructors => isStatic ? ".cctor" : ".ctor",
            LookupKind.TypeInitializer => ".cctor",
            LookupKind.IndexerGetter => "get_Item",
            LookupKind.IndexerSetter => "set_Item",
            LookupKind.Indexer => "Item",
            _ => null,
        };
        IReadOnlyList<string> types = form.Kind == LookupKind.IndexerSetter && pinned is not null ? [.. pinned, "?"] : pinned;
        var read = type is not null && (member is not null || form.Kind == LookupKind.Methods);
        Value value = !form.Followed || !read ? Unknown : form.Kind switch
        {
            LookupKind.Property or LookupKind.Indexer => new PropertyValue(type, member, types),
            LookupKind.Event => new EventValue(type, member),
            LookupKind.Methods => new MethodsValue(type, null),
            _ => new MethodValue(type, member, types) { TypeArguments = typeArguments },
        };
        return new Lookup(WordOf(form.Kind), type, WithTypeArguments(member, typeArguments), types, value);
    }

    private static Lookup Unread(LookupKind kind) => new(WordOf(kind), null, null, null, Unknown);

    /// <summary>
    /// Whether the scanned assembly defines the class: by its definition, or for a class named as
    /// text that the scan did not find, by its name.
    /// </summary>
    public bool Defines(TypeValue type) =>
        type.Type is { } resolved
            ? resolved.GetDefinition()?.ParentModule?.AssemblyName == _assemblyName
            : (_ownTypes ??= _host.TypeSystem.MainModule.TypeDefinitions
                .Select(definition => ReferenceFormatter.FormatTypeName(definition))
                .ToHashSet(StringComparer.Ordinal)).Contains(type.Name);

    private static List<string> ParameterTypeNames(IMethod method) =>
        (method.MemberDefinition as IMethod ?? method).Parameters.Select(parameter => parameter.Type.ReflectionName).ToList();

    /// <summary>
    /// The parameter types a Type[] argument pins, written as parameter types are: none for an empty
    /// array, each element's for an array written out, <c>ref T</c> for a type MakeByRefType makes,
    /// '?' for an element not read; null, for no overload pinned, when the argument is null or not read.
    /// </summary>
    private List<IReadOnlyList<string>> ParameterTypes(Expression argument, Frame frame) =>
        Read(argument, frame)
            .Select(value => value is ListValue list ? (IReadOnlyList<string>)list.Elements.Select(ParameterTypeName).ToList() : null)
            .Distinct(ParameterListComparer.Instance)
            .ToList();

    /// <summary>
    /// The type arguments a call passes to make a generic method, as MakeGenericMethod takes them,
    /// one list for each alternative: the elements of a Type[] the code writes out, or the Type
    /// arguments the call lists one by one, each written as a parameter type is and '?' where not
    /// read. An array not read gives one list with a lone '?', since its length is not known.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TypeArgumentLists(IReadOnlyList<Expression> arguments, Frame frame)
    {
        if (arguments.Count == 1)
        {
            return Read(arguments[0], frame)
                .Select(value => value switch
                {
                    ListValue list => list.Elements.Select(ParameterTypeName).ToList(),
                    TypeValue type => [ParameterTypeName(type)],
                    _ => (IReadOnlyList<string>)["?"],
                })
                .Distinct(ParameterListComparer.Instance)
                .ToList();
        }
        // A type argument with several alternatives is not paired with the others.
        return arguments.Count == 0
            ? [["?"]]
            : [arguments.Select(argument => Read(argument, frame).ToList() is [TypeValue type] ? ParameterTypeName(type) : "?").ToList()];
    }

    private static string ParameterTypeName(Value value) => value switch
    {
        TypeValue { Text: { } text } => HarmonyDependencyScanner.ParameterTypeOfText(text),
        TypeValue { Type: { } type } => ReferenceFormatter.FormatTypeRef(type),
        // A type not read stays '?' when passed by reference as well.
        ByReferenceValue { Element: var element } when ParameterTypeName(element) is var name && name != "?" => "ref " + name,
        _ => "?",
    };

    /// <summary>Two lists of parameter types alike element by element, or both no list.</summary>
    private sealed class ParameterListComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static readonly ParameterListComparer Instance = new();

        public bool Equals(IReadOnlyList<string> x, IReadOnlyList<string> y) =>
            x is null ? y is null : y is not null && x.SequenceEqual(y);

        public int GetHashCode(IReadOnlyList<string> list) => list?.Count ?? -1;
    }

    /// <summary>
    /// The method a lambda names, see <see cref="LambdaMethod"/>, pinned to that overload's
    /// parameter types, and for a generic method with the type arguments the lambda makes it for.
    /// </summary>
    private static Value CalledMethod(Expression lambda) =>
        LambdaMethod(lambda) is { } called && TypeOf(called.DeclaringType) is { } type
            ? new MethodValue(type, called.Name, (called.MemberDefinition as IMethod ?? called).Parameters
                .Select(parameter => ReferenceFormatter.FormatTypeRef(parameter.Type)).ToList())
            {
                TypeArguments = called.TypeParameters.Count > 0
                    ? called.TypeArguments.Select(ReferenceFormatter.FormatTypeRef).ToList()
                    : null,
            }
            : Unknown;

    // --- Members named in a lambda ------------------------------------------------------------

    /// <summary>
    /// The method a lambda written at the call names to Harmony, as SymbolExtensions.GetMethodInfo
    /// takes it: the method of a delegate the body makes from a method group, as in () => Helper,
    /// also of an object a call makes, which the decompiler writes as a pointer's member where the
    /// object is a struct, or else the method the body calls, the first call in the body.
    /// Without HarmonyLib the decompiler casts the lambda to its expression type. Where it cannot
    /// resolve the reflection types of the core library, as for an assembly built against .NET 10
    /// read beside an older System.Runtime that forwards none of them, it writes the expression
    /// tree's construction instead of the lambda, and the method is read from that as Harmony reads
    /// the tree: the method of the body's Expression.Call, or, for the conversion of a call on a
    /// constant, which is how the compiler makes a delegate from a method group, the constant's
    /// method. Null for anything else, a lambda the call is given through a parameter or a local
    /// among it.
    /// </summary>
    private static IMethod LambdaMethod(Expression argument)
    {
        switch (Unwrapped(argument))
        {
            case LambdaExpression { Body: var body }:
                var group = Unwrapped(body as Expression) is ObjectCreateExpression { Arguments.Count: 1 } creation
                            && creation.GetResolveResult().Type.Kind == TypeKind.Delegate
                    ? Unwrapped(creation.Arguments.Single())
                    : Unwrapped(body as Expression);
                if (group is MemberReferenceExpression or IdentifierExpression or PointerReferenceExpression
                    && group.GetSymbol() is IMethod named)
                    return named;
                return body.DescendantsAndSelf.OfType<InvocationExpression>().FirstOrDefault()?.GetSymbol() as IMethod;
            case InvocationExpression construction when IsFactory(construction, "Lambda"):
                var tree = Unwrapped(construction.Arguments.FirstOrDefault());
                if (tree is InvocationExpression call && IsFactory(call, "Call"))
                    return call.Arguments.Select(TokenMember).OfType<IMethod>().FirstOrDefault();
                return tree is InvocationExpression conversion && IsFactory(conversion, "Convert")
                       && Unwrapped(conversion.Arguments.FirstOrDefault()) is InvocationExpression made && IsFactory(made, "Call")
                       && Unwrapped(made.Arguments.FirstOrDefault()) is InvocationExpression constant && IsFactory(constant, "Constant")
                    ? TokenMember(constant.Arguments.FirstOrDefault()) as IMethod
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The class and name of the field a lambda written at the call reads, as
    /// SymbolExtensions.GetFieldInfo takes it past the conversions around it: from the lambda, or
    /// from the expression tree's construction where the decompiler writes that, as
    /// <see cref="LambdaMethod"/> reads a method. Null for anything else.
    /// </summary>
    public static (string Class, string Field)? LambdaField(Expression argument)
    {
        var field = Unwrapped(argument) switch
        {
            LambdaExpression { Body: Expression body } => Unwrapped(body) is (MemberReferenceExpression or IdentifierExpression) and var access
                ? access.GetSymbol() as IField
                : null,
            InvocationExpression construction when IsFactory(construction, "Lambda") => ConstructedField(construction.Arguments.FirstOrDefault()),
            _ => null,
        };
        return field is not null && TypeOf(field.DeclaringType) is { } type ? (type.Name, field.Name) : null;
    }

    /// <summary>The field of an expression tree's Expression.Field, past Expression.Convert and ConvertChecked.</summary>
    private static IField ConstructedField(Expression tree)
    {
        while (Unwrapped(tree) is InvocationExpression conversion && (IsFactory(conversion, "Convert") || IsFactory(conversion, "ConvertChecked")))
            tree = conversion.Arguments.FirstOrDefault();
        return Unwrapped(tree) is InvocationExpression access && IsFactory(access, "Field")
            ? access.Arguments.Select(TokenMember).OfType<IField>().FirstOrDefault()
            : null;
    }

    /// <summary>Whether a call is the factory method of the name on System.Linq.Expressions.Expression.</summary>
    private static bool IsFactory(InvocationExpression call, string name) =>
        call.GetSymbol() is IMethod method && method.Name == name
        && method.DeclaringType?.ReflectionName == "System.Linq.Expressions.Expression";

    /// <summary>
    /// The method or field whose token the decompiler cannot write in C#, read from the instruction
    /// it keeps on the MethodBase.GetMethodFromHandle or FieldInfo.GetFieldFromHandle call that is
    /// given the token, past the conversion it puts around the token where it cannot resolve the
    /// handle's type; null for any other expression.
    /// </summary>
    private static IMember TokenMember(Expression expression)
    {
        if (Unwrapped(expression) is not InvocationExpression handle
            || handle.GetSymbol() is not IMethod { Name: "GetMethodFromHandle" or "GetFieldFromHandle" }
            || handle.Annotation<CallInstruction>() is not { Arguments.Count: > 0 } call)
            return null;
        var argument = call.Arguments[0];
        while (argument is UnaryInstruction unary)
            argument = unary.Argument;
        return (argument as LdMemberToken)?.Member;
    }

    /// <summary>
    /// LINQ over values the reader knows: a filter or pick among one class's methods by an exact name
    /// test, a projection of a list the code writes out, and a copy of a list.
    /// </summary>
    private IEnumerable<Value> ReadEnumerable(IMethod method, List<Expression> arguments, Frame frame)
    {
        var lambda = arguments.ElementAtOrDefault(1) is { } second ? Unwrapped(second) as LambdaExpression : null;
        switch (method.Name)
        {
            case "Where":
                return Read(arguments.ElementAtOrDefault(0), frame).Select(source => source is MethodsValue methods && lambda is not null
                    ? methods with { Name = methods.Name ?? NameTested(lambda) }
                    : Unknown);
            case "First" or "FirstOrDefault" or "Single" or "SingleOrDefault" or "Last" or "LastOrDefault":
                return Read(arguments.ElementAtOrDefault(0), frame).Select(source => source is MethodsValue methods
                    ? Searched(methods, lambda is null ? null : NameTested(lambda))
                    : Unknown);
            case "Select" when lambda is not null && LambdaVariable(lambda) is { } variable && BodyExpression(lambda) is { } body:
                return Read(arguments.ElementAtOrDefault(0), frame).Select(source => source is ListValue list
                    ? new ListValue(list.Elements.SelectMany(element => Read(body, frame.WithVariable(variable, [element]))).ToList())
                    : Unknown);
            case "ToList" or "ToArray" or "AsEnumerable":
                return Read(arguments.ElementAtOrDefault(0), frame);
            default:
                return UnknownOnly;
        }
    }

    /// <summary>
    /// The class a Traverse reads: what Traverse.Create(x) was given, as the decompiler types x; the
    /// class Traverse.Create(typeof(X)), Create&lt;X&gt;() or CreateWithType("X") names.
    /// </summary>
    private IEnumerable<Value> ReadTraverseCreation(IMethod method, InvocationExpression invocation, List<Expression> arguments, Frame frame)
    {
        var definition = method.MemberDefinition as IMethod ?? method;
        var argument = arguments.FirstOrDefault();
        if (method.Name == "CreateWithType")
            return Texts(argument, frame).Select(text => TraverseValue.Over(TextType(text) as TypeValue));
        if (Math.Max(definition.TypeParameters.Count, method.TypeArguments.Count) == 1)
            return [TraverseValue.Over(TypeArgumentType(method, invocation))];
        if (definition.Parameters.FirstOrDefault()?.Type.ReflectionName == "System.Type")
            return Types(argument, frame).Select(type => TraverseValue.Over(type as TypeValue));
        return [TraverseValue.Over(ObjectType(argument))];
    }

    /// <summary>The first type argument of a generic call, as resolved or else as written; '?' where none is known.</summary>
    public static string TypeArgument(IMethod method, InvocationExpression invocation) =>
        TypeArgumentType(method, invocation)?.Name ?? "?";

    private static TypeValue TypeArgumentType(IMethod method, InvocationExpression invocation)
    {
        var argument = method.TypeArguments.FirstOrDefault();
        if (argument is null or ITypeParameter)
            argument = (invocation.Target as MemberReferenceExpression)?.TypeArguments.FirstOrDefault()?.GetResolveResult().Type;
        return TypeOf(argument);
    }

    /// <summary>
    /// The class the decompiler gives an object, past the casts to object that open it. The decompiler
    /// writes <c>x as T</c> as <c>(x is T) ? x : null</c> when it cannot load T's assembly, which is of
    /// x's type, so that form is read as T. '?' for an object typed as object.
    /// </summary>
    public static string ObjectClass(Expression expression) => ObjectType(expression)?.Name ?? "?";

    /// <summary>The class <see cref="ObjectClass"/> names, as a value; null for an object typed as object.</summary>
    private static TypeValue ObjectType(Expression expression)
    {
        while (true)
        {
            if (expression is ParenthesizedExpression parenthesized)
                expression = parenthesized.Expression;
            else if (expression is CastExpression cast && cast.GetResolveResult().Type.IsKnownType(KnownTypeCode.Object))
                expression = cast.Expression;
            else
                break;
        }
        var type = expression is ConditionalExpression { FalseExpression: NullReferenceExpression } conditional
                   && Unparenthesized(conditional.Condition) is IsExpression test
                   && test.Expression.IsMatch(conditional.TrueExpression)
            ? test.Type.GetResolveResult().Type
            : expression?.GetResolveResult().Type;
        return type is null || type.IsKnownType(KnownTypeCode.Object) ? null : TypeOf(type);
    }

    // --- Creations ----------------------------------------------------------------------------

    /// <summary>
    /// A HarmonyMethod's patch method, by class and name or from a method or delegate; a delegate's
    /// method; and a list built with a collection initializer.
    /// </summary>
    private IEnumerable<Value> ReadCreation(ObjectCreateExpression creation, Frame frame)
    {
        var arguments = creation.Arguments.ToList();
        var type = creation.GetResolveResult()?.Type;
        if (type?.ReflectionName == "HarmonyLib.HarmonyMethod")
        {
            if (arguments.Count >= 2 && (creation.GetSymbol() as IMethod)?.Parameters.FirstOrDefault()?.Type.ReflectionName == "System.Type")
                return CombineNamed(Types(arguments[0], frame), Texts(arguments[1], frame), (@class, name) => new PatchMethodValue(@class, name));
            return arguments.Count == 0 ? UnknownOnly : Read(arguments[0], frame).Select(PatchMethodOf);
        }
        if (type?.Kind == TypeKind.Delegate && arguments.Count == 1)
            return [MethodGroup(arguments[0])];
        if (!creation.Initializer.IsNull)
        {
            var elements = creation.Initializer.Elements.ToList();
            if (elements.Count == 0 || elements.Any(element => element is NamedExpression or ArrayInitializerExpression))
                return UnknownOnly;
            return [new ListValue(elements.SelectMany(element => Read(element, frame)).ToList())];
        }
        return UnknownOnly;
    }

    /// <summary>The patch method a value names when a HarmonyMethod is made from it.</summary>
    public static Value PatchMethodOf(Value value) => value switch
    {
        MethodValue method => new PatchMethodValue(method.Type, method.Name),
        PatchMethodValue patch => patch,
        NullValue => Null,
        _ => Unknown,
    };

    private static Value MethodGroup(Expression expression) =>
        expression.GetSymbol() is IMethod method && TypeOf(method.DeclaringType) is { } type
            ? new MethodValue(type, method.Name, null)
            : Unknown;

    private Value ReadArray(ArrayCreateExpression array, Frame frame)
    {
        if (!array.Initializer.IsNull)
            return new ListValue(array.Initializer.Elements.SelectMany(element => Read(element, frame)).ToList());
        return array.Arguments.Count == 1 && Unparenthesized(array.Arguments.Single()) is PrimitiveExpression { Value: 0 }
            ? EmptyList
            : Unknown;
    }

    // --- Types and text -----------------------------------------------------------------------

    /// <summary>The classes an expression names; unknown for anything else.</summary>
    public IReadOnlyList<Value> Types(Expression expression, Frame frame) =>
        expression is null ? UnknownOnly
            : Read(expression, frame).Select(value => value is TypeValue ? value : Unknown).Distinct().ToList();

    /// <summary>The texts an expression may be; unknown for anything else.</summary>
    public IReadOnlyList<Value> Texts(Expression expression, Frame frame) =>
        expression is null ? UnknownOnly
            : Read(expression, frame).Select(value => value is TextValue ? value : Unknown).Distinct().ToList();

    /// <summary>
    /// The class a text names, as AccessTools.TypeByName takes it: the value typeof gives where
    /// <see cref="ClassNamedAs"/> finds the class, so that both read alike, and otherwise the
    /// class's name with the text the code wrote.
    /// </summary>
    private Value TextType(Value text) =>
        text is not TextValue { Text: var name } ? Unknown
            : ClassNamedAs(name) is { } found ? TypeOf(found)
            : new TypeValue(HarmonyDependencyScanner.TextTypeName(name), null) { Text = name };

    /// <summary>
    /// The class a reflection name names, <c>Shop.Outer+Part</c>, where one assembly the scan
    /// reads defines it, or the assembly a qualifier after the name gives. Null for a name with type
    /// arguments, an array or another mark, and for a name C# writes, which TypeByName does not
    /// read as one class. A bare name finds only a class of the global namespace: TypeByName
    /// takes the first class of that name in the order the runtime loaded the assemblies in, which
    /// the scan cannot know, as for a full name two assemblies define.
    /// </summary>
    private ITypeDefinition ClassNamedAs(string text)
    {
        var comma = text.IndexOf(',');
        var name = (comma < 0 ? text : text[..comma]).Trim();
        if (name.Length == 0 || name.IndexOfAny(NotInAClassName) >= 0)
            return null;
        var assembly = comma < 0 ? null : text[(comma + 1)..].Split(',')[0].Trim();
        return _host.FindTypeNamed(new FullTypeName(name), string.IsNullOrEmpty(assembly) ? null : assembly);
    }

    private static readonly char[] NotInAClassName = ['[', ']', '*', '&', '<', '>', ' '];

    /// <summary>
    /// A class nested in another by its name, as AccessTools.Inner and Type.GetNestedType find it.
    /// Where the scan cannot look into the outer class, which it did not find from the text naming
    /// it, or whose library it did not load, or which lacks the nested class in the version loaded,
    /// the nested class keeps a reflection name built from the outer class's, so that it reads as
    /// a parameter type; none where that name carries type arguments or an array.
    /// </summary>
    private static Value Nested(Value outer, Value name)
    {
        if (outer is not TypeValue type || name is not TextValue { Text: var nestedName })
            return Unknown;
        var nested = type.Type?.GetDefinition()?.NestedTypes.FirstOrDefault(candidate => candidate.Name == nestedName);
        if (nested is not null)
            return TypeOf(nested);
        var outerName = (type.Text ?? type.Type?.ReflectionName)?.Split(',')[0].Trim();
        return new TypeValue($"{type.Name}.{nestedName}", null)
        {
            Text = outerName is null || outerName.IndexOfAny(['[', ']']) >= 0 ? null : $"{outerName}+{nestedName}",
        };
    }

    /// <summary>
    /// <paramref name="combine"/> over each pair of alternatives, or unknown when both have several,
    /// since which goes with which is not known.
    /// </summary>
    private static IReadOnlyList<Value> Combine(IReadOnlyList<Value> first, IReadOnlyList<Value> second, Func<Value, Value, Value> combine)
    {
        if (first.Count > 1 && second.Count > 1)
            return UnknownOnly;
        return first.SelectMany(a => second.Select(b => combine(a, b))).Distinct().ToList();
    }

    /// <summary><see cref="Combine"/> of classes and names, unknown where either is not known.</summary>
    private static IReadOnlyList<Value> CombineNamed(IReadOnlyList<Value> types, IReadOnlyList<Value> names, Func<TypeValue, string, Value> combine) =>
        Combine(types, names, (type, name) =>
            type is TypeValue found && name is TextValue { Text: var text } ? combine(found, text) : Unknown);
}
