using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for code the compiler moves out of a method of a generic type: analyze
/// reports it under the method it is written in, as it does in a type without type parameters.
/// The fixture's <c>CallShapes.Generics.Shapes&lt;T&gt;</c> has one method per shape, named
/// after the shape, and each uses the field of <c>Marks</c> of the same name, calls
/// <c>Marks.Note</c> and creates a <c>Token</c>. <c>Lambdas&lt;T&gt;</c> has three methods whose
/// lambdas share one generated class, and <c>GenericMethods</c> has methods with a type
/// parameter in a class without one.
/// </summary>
public sealed class GenericShapeTests : IClassFixture<ILensServerFixture>
{
    private const string Generics = "CallShapes.Generics";

    private static readonly string[] Shapes =
    [
        "Plain", "Lambda", "CapturingLambda", "LocalFunction", "LocalFunctionUsingThis",
        "CapturingLocalFunction", "LocalFunctionAsDelegate", "LocalFunctionCalledByLambda",
        "LambdaWithinLocalFunction", "Iterator", "IteratorLocalFunction", "IteratorLambda",
        "IteratorCapturingLambda", "AsyncMethod", "AsyncLocalFunction", "AsyncLambda", "FinallyBlock",
        "GenericLocalFunction", "NestedLocalFunction",
    ];

    public static TheoryData<string> ShapeNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (string shape in Shapes)
                names.Add(shape);
            return names;
        }
    }

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public GenericShapeTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "CallShapes.dll");
        if (!File.Exists(_callShapesPath))
            throw new FileNotFoundException(
                $"CallShapes fixture not found: {_callShapesPath}. " +
                "Tests/Fixtures/CallShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // Inside a generic class a method calls its local function through a reference on the
    // constructed type, so each shape with a local function is one a lookup by the function's
    // definition does not connect to its method.
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public async Task ReadByAndAssignedBy_NameTheMethodTheShapeIsWrittenIn(string shape)
    {
        foreach (string kind in new[] { "read_by", "assigned_by" })
        {
            string[] lines = (await Analyze($"{Generics}.Marks", shape, kind)).Split('\n');

            Assert.Equal(2, lines.Length);
            Assert.StartsWith($"{Generics}.Shapes<>.{MethodName(shape)}(", lines[1]);
        }
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public async Task Uses_ListsWhatTheShapeUses(string shape)
    {
        string[] lines = (await Analyze($"{Generics}.Shapes<>", shape, "uses")).Split('\n');

        Assert.Contains($"{Generics}.Marks.Note(string)", lines);
        Assert.Contains($"{Generics}.Marks.{shape}", lines);
        Assert.Contains($"{Generics}.Token..ctor(string)", lines);
    }

    // Every shape calls Note and creates a Token, so each answer names every method of
    // Shapes<T> once, and no generated method is left for the switch to add.
    [Theory]
    [InlineData("Marks", "Note", "used_by", false)]
    [InlineData("Marks", "Note", "used_by", true)]
    [InlineData("Token", null, "instantiated_by", false)]
    [InlineData("Token", null, "instantiated_by", true)]
    public async Task AQuestionAboutWhoUses_NamesEachShapesMethodOnce(
        string typeName, string? memberName, string kind, bool includeGenerated)
    {
        string[] lines = (await Analyze($"{Generics}.{typeName}", memberName, kind, includeGenerated)).Split('\n');

        Assert.Equal(
            Shapes.Select(MethodName).Order(StringComparer.Ordinal),
            lines.Skip(1).Select(MethodOfShapes).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CallSites_ListsEachShapesCallUnderItsMethod_AndLeavesNoneUnshown()
    {
        string[] lines = (await Analyze($"{Generics}.Marks", "Note", "call_sites")).Split('\n');

        Assert.Equal($"{Shapes.Length} calls to {Generics}.Marks.Note(string):", lines[0]);
        // Each method makes one call, so the lines come in pairs: the method, then its call.
        Assert.Equal(
            Shapes.Select(shape => (MethodName(shape), $"  Marks.Note(\"{shape}\")")).OrderBy(pair => pair.Item1, StringComparer.Ordinal),
            lines.Skip(1).Chunk(2).Select(pair => (MethodOfShapes(pair[0]), pair[1])).OrderBy(pair => pair.Item1, StringComparer.Ordinal));
    }

    // The three lambdas lie in one generated class. Each belongs to the method that takes its
    // address, not to the first method that takes any of them.
    [Theory]
    [InlineData("FirstLambda")]
    [InlineData("SecondLambda")]
    [InlineData("ThirdLambda")]
    public async Task ALambdaOfASharedClass_IsReportedUnderItsOwnMethod(string method)
    {
        Assert.Equal(
            $"1 reader of {Generics}.Marks.{method} [declared]:\n{Generics}.Lambdas<>.{method}()",
            await Analyze($"{Generics}.Marks", method, "read_by"));

        string[] used = (await Analyze($"{Generics}.Lambdas<>", method, "uses")).Split('\n');
        var marks = used.Where(line => line.StartsWith($"{Generics}.Marks."));
        Assert.Equal([$"{Generics}.Marks.{method}"], marks);
    }

    // A lambda in a generic method is a generic method itself or lies in a generic class, and
    // one inside an iterator or async method lies beside that method's class, not in it. Its
    // address is then taken through a reference that names no definition.
    [Theory]
    [InlineData("MethodIteratorLambda")]
    [InlineData("MethodAsyncLambda")]
    [InlineData("MethodLambdaUsingThis")]
    [InlineData("MethodLocalFunctionCalledByLambda")]
    public async Task ALambda_InAGenericMethodOfAClassWithoutTypeParameters_IsReportedUnderItsMethod(string method)
    {
        string[] readers = (await Analyze($"{Generics}.Marks", method, "read_by")).Split('\n');
        Assert.Equal(2, readers.Length);
        Assert.StartsWith($"{Generics}.GenericMethods.{method}<U>(", readers[1]);

        string[] used = (await Analyze($"{Generics}.GenericMethods", method, "uses")).Split('\n');
        Assert.Contains($"{Generics}.Marks.{method}", used);
    }

    // A class nested in a generic class takes that class's type parameter, and a generic
    // struct is a generic type like a class.
    [Theory]
    [InlineData("NestedClassLocalFunction", "Shapes<>.Inner")]
    [InlineData("StructLocalFunction", "Pair<>")]
    public async Task ALocalFunction_InANestedClassOfAGenericClass_AndInAGenericStruct_IsReportedUnderItsMethod(
        string method, string type)
    {
        Assert.Equal(
            $"1 reader of {Generics}.Marks.{method} [declared]:\n{Generics}.{type}.{method}()",
            await Analyze($"{Generics}.Marks", method, "read_by"));
    }

    /// <summary>
    /// A shape's method as ILens names it, which is the shape's name but for the one generic
    /// method, written with its type parameter.
    /// </summary>
    private static string MethodName(string shape) =>
        shape == "GenericLocalFunction" ? "GenericLocalFunction<U>" : shape;

    /// <summary>
    /// The name of the method of Shapes&lt;T&gt; a result line starts with; a line that names
    /// anything else fails the test.
    /// </summary>
    private static string MethodOfShapes(string line)
    {
        string prefix = $"{Generics}.Shapes<>.";
        Assert.StartsWith(prefix, line);
        return line[prefix.Length..line.IndexOf('(')];
    }

    private async Task<string> Analyze(string typeName, string? memberName, string kind, bool includeGenerated = false)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["kind"] = kind,
            ["include_compiler_generated"] = includeGenerated,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
