using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for arrays of more than one dimension and arrays of arrays. Every answer writes
/// an array as C# does, with a comma for each dimension past the first and the outermost array's
/// brackets first, so int[][,] is an array of two-dimensional arrays, which reflection writes
/// System.Int32[,][]. A type pattern reads the brackets the same way: held exactly, [] fits an
/// array of one dimension only, so overloads that differ in an array's dimensions alone can each be
/// asked for; held loosely, as find_methods holds a pattern, [] fits an array of any number of
/// dimensions. The fixture is the namespace CallShapes.Ranks in Tests/Fixtures/CallShapes/Ranks.cs;
/// the Harmony lines are pinned in HarmonyTests.
/// </summary>
public sealed class ArrayRankTests : IClassFixture<ILensServerFixture>
{
    private const string Ranks = "CallShapes.Ranks";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public ArrayRankTests(ILensServerFixture server)
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

    // Overloads that differ in the dimensions alone, an array of arrays in each order, and a
    // class's type parameter as an array's element: each line apart.
    [Theory]
    [InlineData("Grid", $"4 members of public class {Ranks}.Grid:\n" +
        "4 methods:\n" +
        "  public Grid()\n" +
        "  public int Take(int[,,] values)\n" +
        "  public int Take(int[,] values)\n" +
        "  public int Take(int[] values)")]
    [InlineData("Layers", $"4 members of public class {Ranks}.Layers:\n" +
        "4 methods:\n" +
        "  public Layers()\n" +
        "  public int Put(int[,][] cells)\n" +
        "  public int Put(int[][,] cells)\n" +
        "  public int Put(int[][] cells)")]
    [InlineData("Shelf<>", $"3 members of public class {Ranks}.Shelf<T>:\n" +
        "3 methods:\n" +
        "  public Shelf()\n" +
        "  public int Take(T[,] values)\n" +
        "  public int Take(T[] values)")]
    public async Task ListMembers_WritesOverloadsThatDifferInTheirArraysApart(string typeName, string expected)
    {
        Assert.Equal(expected, await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Ranks}.{typeName}",
        }));
    }

    // A parameter, a return, a field, a property, an indexer's index, an event's type, a type
    // argument, a parameter passed by reference and an array whose element is a nullable value.
    [Fact]
    public async Task ListMembers_WritesATwoDimensionalArrayInEachPlaceAsCSharpDoes()
    {
        Assert.Equal(
            $"15 members of public class {Ranks}.Board:\n" +
            "9 methods:\n" +
            "  public Board()\n" +
            "  public Board(int[,] cells)\n" +
            "  public int Count(int?[,] cells)\n" +
            "  public void Fill(ref int[,] cells)\n" +
            "  public void Fill(ref int[] cells)\n" +
            "  public int[,] Make(int width)\n" +
            "  public int Read(int[,] cells)\n" +
            "  public List<int[,]> Rows(List<int[]> flat)\n" +
            "  public void Write(int[,] cells)\n" +
            "3 properties:\n" +
            "  public int Item[int[,] key] { get; }\n" +
            "  public int Item[int[] key] { get; }\n" +
            "  public int[,] Marks { get; set; }\n" +
            "1 field:\n" +
            "  public int[,] Cells\n" +
            "1 event:\n" +
            "  public event Action<int[,]> Changed\n" +
            "1 nested type:\n" +
            $"  {Ranks}.Board.Maker",
            await CallToolText("list_members", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Ranks}.Board",
            }));
    }

    [Fact]
    public async Task FindMethods_WritesATwoDimensionalArrayAsCSharpDoes()
    {
        Assert.Equal(
            "9 methods:\n" +
            $"{Ranks}.Board..ctor() → void\n" +
            $"{Ranks}.Board..ctor(int[,]) → void\n" +
            $"{Ranks}.Board.Count(int?[,]) → int\n" +
            $"{Ranks}.Board.Fill(ref int[,]) → void\n" +
            $"{Ranks}.Board.Fill(ref int[]) → void\n" +
            $"{Ranks}.Board.Make(int) → int[,]\n" +
            $"{Ranks}.Board.Read(int[,]) → int\n" +
            $"{Ranks}.Board.Rows(List<int[]>) → List<int[,]>\n" +
            $"{Ranks}.Board.Write(int[,]) → void",
            await CallToolText("find_methods", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Ranks}.Board",
            }));
    }

    // The type, the member, the parameter type, and the header that names the method reached.
    // Held exactly, [] fits an array of one dimension alone, so each overload is reached by its
    // own pattern, and the brackets of an array of arrays are read outermost first.
    public static TheoryData<string, string, string, string> Reads => new()
    {
        { "Grid", "Take", "int[]", $"// {Ranks}.Grid.Take(int[]) → int" },
        { "Grid", "Take", "int[,]", $"// {Ranks}.Grid.Take(int[,]) → int" },
        { "Grid", "Take", "int[,,]", $"// {Ranks}.Grid.Take(int[,,]) → int" },
        { "Layers", "Put", "int[][,]", $"// {Ranks}.Layers.Put(int[][,]) → int" },
        { "Layers", "Put", "int[,][]", $"// {Ranks}.Layers.Put(int[,][]) → int" },
        { "Layers", "Put", "int[][]", $"// {Ranks}.Layers.Put(int[][]) → int" },
        { "Board", "Fill", "ref int[,]", $"// {Ranks}.Board.Fill(ref int[,]) → void" },
        { "Board", "get_Item", "int[,]", $"// {Ranks}.Board.get_Item(int[,]) → int" },
        { "Shelf<>", "Take", "T[,]", $"// {Ranks}.Shelf<>.Take(T[,]) → int" },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Decompile_ReadsAnArraysDimensionsAsCSharpWritesThem(
        string typeName, string memberName, string parameterType, string header)
    {
        string text = await CallToolText("decompile", Question(typeName, memberName, parameterType));

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // The header and the decompiled code write an array of arrays alike.
    [Theory]
    [InlineData("int[][,]", "public int Put(int[][,] cells)", "return 1;")]
    [InlineData("int[,][]", "public int Put(int[,][] cells)", "return 2;")]
    public async Task Decompile_AnArrayOfArrays_HeaderAndCodeWriteItAlike(string parameterType, string declaration, string body)
    {
        string text = await CallToolText("decompile", Question("Layers", "Put", parameterType));

        // The decompiled code may end its lines with a carriage return.
        Assert.Equal(declaration, text.Split('\n')[1].TrimEnd('\r'));
        Assert.Contains(body, text);
    }

    // find_methods holds a pattern loosely: [] fits an array of any number of dimensions, while
    // brackets with a comma fit that number alone.
    [Theory]
    [InlineData("Take", "parameter_types", "int[,]", "1 method:\n" +
        $"{Ranks}.Grid.Take(int[,]) → int")]
    [InlineData("Take", "parameter_types", "int[]", "3 methods:\n" +
        $"{Ranks}.Grid.Take(int[,,]) → int\n" +
        $"{Ranks}.Grid.Take(int[,]) → int\n" +
        $"{Ranks}.Grid.Take(int[]) → int")]
    [InlineData("Take", "parameter_types", "T[,]", "1 method:\n" +
        $"{Ranks}.Shelf<>.Take(T[,]) → int")]
    [InlineData("Put", "parameter_types", "int[][,]", "1 method:\n" +
        $"{Ranks}.Layers.Put(int[][,]) → int")]
    [InlineData("Put", "parameter_types", "int[,][]", "1 method:\n" +
        $"{Ranks}.Layers.Put(int[,][]) → int")]
    [InlineData("Put", "parameter_types", "int[][]", "3 methods:\n" +
        $"{Ranks}.Layers.Put(int[,][]) → int\n" +
        $"{Ranks}.Layers.Put(int[][,]) → int\n" +
        $"{Ranks}.Layers.Put(int[][]) → int")]
    [InlineData("Make", "return_type", "int[,]", "1 method:\n" +
        $"{Ranks}.Board.Make(int) → int[,]")]
    public async Task FindMethods_ReadsAnArraysDimensions(string name, string parameter, string pattern, string expected)
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["namespace"] = Ranks,
            ["name_contains"] = name,
        };
        question[parameter] = parameter == "parameter_types" ? new[] { pattern } : pattern;

        Assert.Equal(expected, await CallToolText("find_methods", question));
    }

    [Theory]
    [InlineData("int[]", "grid.Take(new int[1])")]
    [InlineData("int[,]", "grid.Take(new int[1, 1])")]
    [InlineData("int[,,]", "grid.Take(new int[1, 1, 1])")]
    public async Task Analyze_ReachesEachOverloadByItsArraysDimensions(string parameterType, string call)
    {
        var usedBy = Question("Grid", "Take", parameterType);
        usedBy["kind"] = "used_by";
        var callSites = Question("Grid", "Take", parameterType);
        callSites["kind"] = "call_sites";

        Assert.Equal(
            $"1 user of {Ranks}.Grid.Take({parameterType}) [declared]:\n" +
            $"{Ranks}.Player.Play(Grid)",
            await CallToolText("analyze", usedBy));
        Assert.Equal(
            $"1 call to {Ranks}.Grid.Take({parameterType}):\n" +
            $"{Ranks}.Player.Play(Grid)\n" +
            $"  {call}",
            await CallToolText("analyze", callSites));
    }

    // Calls of overloads that differ in their arrays alone are lines apart, and the members of an
    // array itself, its constructor, Get and Set, are named by the array written as C# writes it,
    // an array of arrays outermost first.
    [Theory]
    [InlineData("Player", "Play", $"6 symbols used by {Ranks}.Player.Play(Grid) [declared]:\n" +
        $"{Ranks}.Grid.Take(int[,,])\n" +
        $"{Ranks}.Grid.Take(int[,])\n" +
        $"{Ranks}.Grid.Take(int[])\n" +
        "System.Int32\n" +
        "System.Int32[,,]..ctor(int, int, int)\n" +
        "System.Int32[,]..ctor(int, int)")]
    [InlineData("Player", "Stack", $"5 symbols used by {Ranks}.Player.Stack(Layers) [declared]:\n" +
        $"{Ranks}.Layers.Put(int[,][])\n" +
        $"{Ranks}.Layers.Put(int[][,])\n" +
        $"{Ranks}.Layers.Put(int[][])\n" +
        "System.Int32\n" +
        "System.Int32[,][]..ctor(int, int)")]
    [InlineData("Board", "Read", $"1 symbol used by {Ranks}.Board.Read(int[,]) [declared]:\n" +
        "System.Int32[,].Get(int, int)")]
    [InlineData("Board", "Write", $"1 symbol used by {Ranks}.Board.Write(int[,]) [declared]:\n" +
        "System.Int32[,].Set(int, int, int)")]
    public async Task Analyze_Uses_WritesEachArrayAsCSharpDoes(string typeName, string memberName, string expected)
    {
        Assert.Equal(expected, await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Ranks}.{typeName}",
            ["member_name"] = memberName,
            ["kind"] = "uses",
        }));
    }

    [Theory]
    [InlineData("Grid", "Take", "int[,]", $"{Ranks}.Grid.Take(int[,]) → int")]
    [InlineData("Layers", "Put", "int[][,]", $"{Ranks}.Layers.Put(int[][,]) → int")]
    public async Task Compare_ReadsAndWritesAnArraysDimensions(
        string typeName, string memberName, string parameterType, string reference)
    {
        var question = Question(typeName, memberName, parameterType);
        question.Remove("assembly");
        question["assembly_a"] = _callShapesPath;
        question["assembly_b"] = _callShapesPath;

        Assert.Equal(
            $"// {reference} — bodies are identical in both assemblies.",
            await CallToolText("compare", question));
    }

    // return_type reads an array's dimensions as parameter_types does, in each tool that takes it.
    [Theory]
    [InlineData("decompile", $"// {Ranks}.Board.Make(int) → int[,]")]
    [InlineData("analyze", $"No users of {Ranks}.Board.Make(int) [declared]")]
    [InlineData("compare", $"// {Ranks}.Board.Make(int) → int[,] — bodies are identical in both assemblies.")]
    public async Task ReturnType_ReadsAnArraysDimensions(string toolName, string firstLine)
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Ranks}.Board",
            ["member_name"] = "Make",
            ["return_type"] = "int[,]",
        };
        if (toolName == "analyze")
            question["kind"] = "used_by";
        if (toolName == "compare")
        {
            question.Remove("assembly");
            question["assembly_a"] = _callShapesPath;
            question["assembly_b"] = _callShapesPath;
        }

        Assert.Equal(firstLine, (await CallToolText(toolName, question)).Split('\n')[0]);
    }

    // The overload error lists overloads that differ in their arrays alone apart.
    [Fact]
    public async Task Decompile_WithoutAHint_ListsTheOverloadsWithTheirArraysDimensions()
    {
        string text = await CallToolError("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Ranks}.Grid",
            ["member_name"] = "Take",
        });

        Assert.Equal(
            $"Error: ArgumentException: 'Take' on {Ranks}.Grid has 3 matching overloads; specify parameter_count or " +
            $"parameter_types to disambiguate: {Ranks}.Grid.Take(int[]), {Ranks}.Grid.Take(int[,]), {Ranks}.Grid.Take(int[,,]).",
            text);
    }

    // Each overload's lines stand beneath its own member.
    [Fact]
    public async Task SearchCode_GroupsTheLinesOfEachOverloadApart()
    {
        Assert.Equal(
            $"3 lines matching return in {Ranks}.Grid:\n" +
            $"{Ranks}.Grid.Take(int[,,])\n" +
            "  return 3;\n" +
            $"{Ranks}.Grid.Take(int[,])\n" +
            "  return 2;\n" +
            $"{Ranks}.Grid.Take(int[])\n" +
            "  return 1;",
            await CallToolText("search_code", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Ranks}.Grid",
                ["regex"] = "return",
            }));
    }

    /// <summary>A question about a member of a class of the fixture, with a parameter type.</summary>
    private Dictionary<string, object?> Question(string typeName, string memberName, string parameterType) => new()
    {
        ["assembly"] = _callShapesPath,
        ["type_name"] = $"{Ranks}.{typeName}",
        ["member_name"] = memberName,
        ["parameter_types"] = new[] { parameterType },
    };

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        return text;
    }
}
