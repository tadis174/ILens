using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.Semantics;
using ILFunctionKind = ICSharpCode.Decompiler.IL.ILFunctionKind;
using ILVariable = ICSharpCode.Decompiler.IL.ILVariable;
using VariableKind = ICSharpCode.Decompiler.IL.VariableKind;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Reads what decompiled code hands to Harmony and to reflection: the classes and members it looks
/// up, the patch methods it names, and the text and lists these are built from. It reads the trees
/// <see cref="AssemblyHost.DecompileWithPositionalArguments"/> builds, in which every call writes its
/// arguments in parameter order, a call to an extension method is a static call with the receiver
/// first, and every symbol is resolved, a class from an assembly the decompiler cannot load by the
/// name the reference to it gives.
/// </summary>
/// <remarks>
/// A value is followed back to where the assembly writes it: a local to the assignments that reach
/// its use; a parameter to each call of its member, a call binding all of the member's parameters
/// together; a field to the assignments before its use in the member, and failing those, a static
/// field to every assignment and an instance field to what the constructor that built the object
/// assigns it, for each place that builds one; and an own method to what it returns, with the call's
/// arguments in place of its parameters. Following stops <see cref="MaxDepth"/> members
/// away from where the reading started. A value it cannot follow is <see cref="Unknown"/>, which a
/// line prints as '?'. Every read gives a list of alternatives, one for each branch or element that
/// may supply the value. The alternatives of two arguments of one call are not paired: a call that
/// would need that reads as <see cref="Unknown"/>.
/// </remarks>
internal sealed class HarmonyValueReader
{
    /// <summary>How many members away from where the reading starts a value is followed.</summary>
    public const int MaxDepth = 4;

    /// <summary>A value the code computes, as far as the reader can tell.</summary>
    public abstract record Value;

    /// <summary>A value the reader cannot follow.</summary>
    public sealed record UnknownValue : Value;

    public sealed record NullValue : Value;

    public sealed record TextValue(string Text) : Value;

    /// <summary>
    /// A class, by its name as the other tools take it, with the type when the tree resolves it and
    /// the text when the code names it by text. Two are equal by name.
    /// </summary>
    public sealed record TypeValue(string Name, IType Type) : Value
    {
        public string Text { get; init; }

        public bool Equals(TypeValue other) => other is not null && other.Name == Name;
        public override int GetHashCode() => Name.GetHashCode();
    }

    /// <summary>
    /// A method or accessor, by its class and name and, when the code pins an overload, its
    /// parameter types, written as parameter types are; <see cref="MethodType"/> is the kind patched
    /// when the name does not show it, such as Enumerator, and <see cref="Searched"/> says that the
    /// code's own tests pick the overload.
    /// </summary>
    public sealed record MethodValue(TypeValue Type, string Name, IReadOnlyList<string> ParameterTypes,
        string MethodType = null, bool Searched = false) : Value
    {
        public bool Equals(MethodValue other) =>
            other is not null && Type.Equals(other.Type) && Name == other.Name && MethodType == other.MethodType
            && Searched == other.Searched && ParameterListComparer.Instance.Equals(ParameterTypes, other.ParameterTypes);

        public override int GetHashCode() => HashCode.Combine(Type, Name, MethodType, Searched, ParameterTypes?.Count);
    }

    public sealed record PropertyValue(TypeValue Type, string Name) : Value;

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

    /// <summary>A Traverse over an object of a class, or over a class's static members; '?' for a class not read.</summary>
    public sealed record TraverseValue(string Class) : Value;

    /// <summary>The class and the member name a lookup is given, before it says what kind of member the name is.</summary>
    private sealed record NamedMember(TypeValue Type, string Name) : Value;

    public static readonly Value Unknown = new UnknownValue();
    public static readonly Value Null = new NullValue();
    private static readonly IReadOnlyList<Value> UnknownOnly = [Unknown];
    private static readonly Value EmptyList = new ListValue([]);

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
    private readonly HashSet<(AstNode, Frame)> _reading = new();

    public HarmonyValueReader(AssemblyHost host)
    {
        _host = host;
        _assemblyName = host.TypeSystem.MainModule.AssemblyName;
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
        foreach (var constructor in type.Methods.Where(method => method.IsConstructor && !method.IsStatic && method.HasBody))
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
                if (found < calls.Count && definition.DeclaringTypeDefinition.Methods.Count(method => method.Name == definition.Name) == 1)
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
            return ReadCore(expression, frame).Distinct().ToList();
        }
        finally
        {
            _reading.Remove((expression, frame));
        }
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
            values.Add(new ListValue(returned.Where(item => item.Yields).SelectMany(item => Read(item.Expression, frame)).Distinct().ToList()));
        return values.Distinct().ToList();
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
            case IField field:
                return ReadField(reference, field, frame);
            case IProperty { Name: "GetMethod" or "SetMethod", DeclaringType.ReflectionName: "System.Reflection.PropertyInfo" } property:
                return Read(reference.Target, frame).Select(value => AccessorOf(value, property.Name == "GetMethod" ? "get_" : "set_"));
            case IMethod:
                return [MethodGroup(reference)];
            default:
                return UnknownOnly;
        }
    }

    private static Value AccessorOf(Value property, string prefix) =>
        property is PropertyValue found ? new MethodValue(found.Type, prefix + found.Name, null) : Unknown;

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
        (methods.Name ?? tested) is { } name ? new MethodValue(methods.Type, name, null, Searched: true) : Unknown;

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
            return cached;
        _staticFields[token] = UnknownOnly;  // a field met again while its own value is read is not followed in a circle
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
        return values.Count == 0 ? [Null] : values.Distinct().ToList();
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
        switch (method.DeclaringType?.ReflectionName, method.Name)
        {
            case ("HarmonyLib.AccessTools", "TypeByName"):
            case ("System.Type", "GetType") when method.IsStatic:
            case ("System.Reflection.Assembly", "GetType") when !method.IsStatic:
                return Texts(arguments.ElementAtOrDefault(0), frame).Select(TextType);
            case ("System.Object", "GetType") when receiver is null or ThisReferenceExpression:
                return [TypeOf(frame.Member.DeclaringTypeDefinition) ?? Unknown];
            case ("HarmonyLib.AccessTools" or "HarmonyLib.AccessToolsExtensions", "Inner"):
                return Combine(Types(arguments.ElementAtOrDefault(0), frame), Texts(arguments.ElementAtOrDefault(1), frame), Nested);
            case ("System.Type", "GetNestedType"):
                return Combine(Types(receiver, frame), Texts(arguments.ElementAtOrDefault(0), frame), Nested);
            case ("HarmonyLib.AccessTools" or "HarmonyLib.AccessToolsExtensions", "Method" or "DeclaredMethod" or "PropertyGetter"
                or "PropertySetter" or "DeclaredPropertyGetter" or "DeclaredPropertySetter" or "Property" or "DeclaredProperty"):
                return ReadLookup(method, arguments, frame);
            case ("HarmonyLib.AccessTools" or "HarmonyLib.AccessToolsExtensions", "Constructor" or "DeclaredConstructor"):
                return ReadConstructorLookup(method, arguments, frame);
            case ("HarmonyLib.AccessTools", "EnumeratorMoveNext" or "AsyncMoveNext"):
                var kind = method.Name == "EnumeratorMoveNext" ? "Enumerator" : "Async";
                return Read(arguments.ElementAtOrDefault(0), frame)
                    .Select(value => value is MethodValue found ? found with { MethodType = kind } : Unknown);
            case ("System.Type", "GetMethod"):
                return ReadGetMethod(method, receiver, arguments, frame);
            case ("System.Type", "GetProperty"):
                return CombineNamed(Types(receiver, frame), Texts(arguments.ElementAtOrDefault(0), frame),
                    (type, name) => new PropertyValue(type, name));
            case ("System.Reflection.PropertyInfo", "GetGetMethod" or "GetSetMethod"):
                return Read(receiver, frame).Select(value => AccessorOf(value, method.Name == "GetGetMethod" ? "get_" : "set_"));
            // A generic method made for type arguments runs the code of the method it was made from, so the target names that method.
            case ("System.Reflection.MethodInfo", "MakeGenericMethod"):
                return Read(receiver, frame).Select(value => value is MethodValue ? value : Unknown);
            case ("System.Type", "GetMethods"):
                return Types(receiver, frame).Select(type => type is TypeValue found ? new MethodsValue(found, null) : Unknown);
            case ("HarmonyLib.AccessTools" or "HarmonyLib.AccessToolsExtensions", "GetDeclaredMethods"):
                return Types(arguments.ElementAtOrDefault(0), frame).Select(type => type is TypeValue found ? new MethodsValue(found, null) : Unknown);
            case ("HarmonyLib.SymbolExtensions", "GetMethodInfo"):
                return [CalledMethod(arguments.ElementAtOrDefault(0))];
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

    private bool IsOwn(IMethod method) =>
        method.ParentModule?.AssemblyName == _assemblyName && !method.IsAbstract && method.HasBody;

    /// <summary>
    /// What an own method returns for this call, its parameters bound to the call's arguments and,
    /// for a call on the frame's own object, its object's fields to the frame's.
    /// </summary>
    private IEnumerable<Value> ReadOwnCall(IMethod method, List<Expression> arguments, Expression receiver, Frame frame)
    {
        if (frame.Depth >= MaxDepth || Tree(method) is not { } tree)
            return UnknownOnly;
        var parameters = Enumerable.Range(0, (method.MemberDefinition as IMethod ?? method).Parameters.Count)
            .ToDictionary(index => index, index => (IReadOnlyList<Binding>)[arguments.ElementAtOrDefault(index) is { } argument
                ? new Binding(argument, frame)
                : Binding.Of(DefaultOf(method, index))]);
        var callee = new Frame(method, tree, frame.Origin, frame.Depth + 1).WithParameters(parameters, frame.Origin);
        if (!method.IsStatic && receiver is null or ThisReferenceExpression)
            callee = callee.WithFields(frame.Fields, frame.Origin);
        var values = Returns(callee);
        return values.Count == 0 ? UnknownOnly : values;
    }

    /// <summary>
    /// The method or property an AccessTools lookup names, in AccessTools' spelling or Harmony's
    /// extension spelling: by a type and a name, or by "Namespace.Type:Name" text, which Harmony
    /// splits at its one colon, looking the type up with AccessTools.TypeByName. A method lookup may
    /// pin an overload with a Type[] of parameter types.
    /// </summary>
    private IEnumerable<Value> ReadLookup(IMethod method, List<Expression> arguments, Frame frame)
    {
        var parameters = ParameterTypeNames(method);
        var typeAt = parameters.IndexOf("System.Type");
        var nameAt = parameters.IndexOf("System.String");
        var arraysAt = parameters.IndexOf("System.Type[]");
        var accessor = method.Name.EndsWith("Getter", StringComparison.Ordinal) ? "get_"
            : method.Name.EndsWith("Setter", StringComparison.Ordinal) ? "set_"
            : null;
        var property = method.Name is "Property" or "DeclaredProperty";

        var named = typeAt >= 0
            ? CombineNamed(Types(arguments.ElementAtOrDefault(typeAt), frame), Texts(arguments.ElementAtOrDefault(nameAt), frame),
                (type, name) => new NamedMember(type, name))
            : Texts(arguments.ElementAtOrDefault(nameAt), frame)
                .Select(text => text is TextValue { Text: var joined } && joined.Split(':') is [var type, var name]
                    ? new NamedMember((TypeValue)TextType(new TextValue(type)), name)
                    : Unknown)
                .ToList();
        if (property)
            return named.Select(value => value is NamedMember found ? new PropertyValue(found.Type, found.Name) : Unknown);

        List<IReadOnlyList<string>> overloads = accessor is null && arraysAt >= 0 && arguments.ElementAtOrDefault(arraysAt) is { } array
            ? ParameterTypes(array, frame)
            : [null];
        if (overloads.Count > 1 && named.Count > 1)
            return UnknownOnly;
        return named.SelectMany(value => overloads.Select(pinned => value is NamedMember found
            ? new MethodValue(found.Type, accessor + found.Name, pinned)
            : Unknown));
    }

    /// <summary>
    /// The constructor an AccessTools constructor lookup names: without parameter types the
    /// parameterless one, and with searchForStatic the static one.
    /// </summary>
    private IEnumerable<Value> ReadConstructorLookup(IMethod method, List<Expression> arguments, Frame frame)
    {
        var parameters = ParameterTypeNames(method);
        var typeAt = parameters.IndexOf("System.Type");
        var arraysAt = parameters.IndexOf("System.Type[]");
        var staticAt = parameters.IndexOf("System.Boolean");
        var isStatic = staticAt >= 0 && arguments.ElementAtOrDefault(staticAt) is { } flag
            && Unparenthesized(flag) is PrimitiveExpression { Value: true };
        List<IReadOnlyList<string>> overloads = isStatic ? [null]
            : arraysAt >= 0 && arguments.ElementAtOrDefault(arraysAt) is { } array && !Read(array, frame).All(value => value is NullValue)
                ? ParameterTypes(array, frame)
                : [[]];
        var types = Types(arguments.ElementAtOrDefault(typeAt), frame);
        if (overloads.Count > 1 && types.Count > 1)
            return UnknownOnly;
        return types.SelectMany(type => overloads.Select(pinned => type is TypeValue found
            ? new MethodValue(found, isStatic ? ".cctor" : ".ctor", pinned)
            : Unknown));
    }

    /// <summary>Type.GetMethod: the receiver's method by name, pinned by a Type[] argument when there is one.</summary>
    private IEnumerable<Value> ReadGetMethod(IMethod method, Expression receiver, List<Expression> arguments, Frame frame)
    {
        var parameters = ParameterTypeNames(method);
        var nameAt = parameters.IndexOf("System.String");
        var arraysAt = parameters.IndexOf("System.Type[]");
        List<IReadOnlyList<string>> overloads = arraysAt >= 0 && arguments.ElementAtOrDefault(arraysAt) is { } array
            ? ParameterTypes(array, frame)
            : [null];
        var named = CombineNamed(Types(receiver, frame), Texts(arguments.ElementAtOrDefault(nameAt), frame),
            (type, name) => new NamedMember(type, name));
        if (overloads.Count > 1 && named.Count > 1)
            return UnknownOnly;
        return named.SelectMany(value => overloads.Select(pinned => value is NamedMember found
            ? new MethodValue(found.Type, found.Name, pinned)
            : Unknown));
    }

    private static List<string> ParameterTypeNames(IMethod method) =>
        (method.MemberDefinition as IMethod ?? method).Parameters.Select(parameter => parameter.Type.ReflectionName).ToList();

    /// <summary>
    /// The parameter types a Type[] argument pins, written as parameter types are: none for an empty
    /// array, each element's for an array written out, '?' for an element not read; null, for no
    /// overload pinned, when the argument is null or not read.
    /// </summary>
    private List<IReadOnlyList<string>> ParameterTypes(Expression argument, Frame frame) =>
        Read(argument, frame)
            .Select(value => value is ListValue list ? (IReadOnlyList<string>)list.Elements.Select(ParameterTypeName).ToList() : null)
            .Distinct(ParameterListComparer.Instance)
            .ToList();

    private static string ParameterTypeName(Value value) => value switch
    {
        TypeValue { Text: { } text } => HarmonyDependencyScanner.ParameterTypeOfText(text),
        TypeValue { Type: { } type } => ReferenceFormatter.FormatTypeRef(type),
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
    /// The method a SymbolExtensions.GetMethodInfo lambda calls, pinned to that overload's parameter
    /// types. Without HarmonyLib the decompiler casts the lambda to its expression type.
    /// </summary>
    private static Value CalledMethod(Expression lambda) =>
        Unwrapped(lambda) is LambdaExpression { Body: var body }
        && body.DescendantsAndSelf.OfType<InvocationExpression>().FirstOrDefault()?.GetSymbol() is IMethod called
        && TypeOf(called.DeclaringType) is { } type
            ? new MethodValue(type, called.Name, (called.MemberDefinition as IMethod ?? called).Parameters
                .Select(parameter => ReferenceFormatter.FormatTypeRef(parameter.Type)).ToList())
            : Unknown;

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
            return Texts(argument, frame).Select(text => new TraverseValue(TextType(text) is TypeValue type ? type.Name : "?"));
        if (Math.Max(definition.TypeParameters.Count, method.TypeArguments.Count) == 1)
            return [new TraverseValue(TypeArgument(method, invocation))];
        if (definition.Parameters.FirstOrDefault()?.Type.ReflectionName == "System.Type")
            return Types(argument, frame).Select(type => new TraverseValue(type is TypeValue found ? found.Name : "?"));
        return [new TraverseValue(ObjectClass(argument))];
    }

    /// <summary>The first type argument of a generic call, as resolved or else as written; '?' where none is known.</summary>
    public static string TypeArgument(IMethod method, InvocationExpression invocation)
    {
        var argument = method.TypeArguments.FirstOrDefault();
        if (argument is null or ITypeParameter)
            argument = (invocation.Target as MemberReferenceExpression)?.TypeArguments.FirstOrDefault()?.GetResolveResult().Type;
        return TypeOf(argument)?.Name ?? "?";
    }

    /// <summary>
    /// The class the decompiler gives an object, past the casts to object that open it. The decompiler
    /// writes <c>x as T</c> as <c>(x is T) ? x : null</c> when it cannot load T's assembly, which is of
    /// x's type, so that form is read as T. '?' for an object typed as object.
    /// </summary>
    public static string ObjectClass(Expression expression)
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
        return type is null || type.IsKnownType(KnownTypeCode.Object) ? "?" : TypeOf(type)?.Name ?? "?";
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

    private static Value TextType(Value text) =>
        text is TextValue { Text: var name } ? new TypeValue(HarmonyDependencyScanner.TextTypeName(name), null) { Text = name } : Unknown;

    /// <summary>A class nested in another by its name, as AccessTools.Inner and Type.GetNestedType find it.</summary>
    private static Value Nested(Value outer, Value name)
    {
        if (outer is not TypeValue type || name is not TextValue { Text: var nestedName })
            return Unknown;
        var nested = type.Type?.GetDefinition()?.NestedTypes.FirstOrDefault(candidate => candidate.Name == nestedName);
        return nested is not null ? TypeOf(nested) : new TypeValue($"{type.Name}.{nestedName}", null);
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
