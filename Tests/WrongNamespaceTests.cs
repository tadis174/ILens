using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a type name that finds no type while types of its own name lie in
/// another namespace or in another class: the answer names them, so a caller who wrote the
/// wrong namespace, or left out the class a type is nested in, gets the name to ask by.
/// <para>
/// CallShapes declares a class Badge in each of the namespaces CallShapes.Namesakes.Alpha and
/// Beta, a class Entry nested in each of its classes Box and Bag, and the classes Visitor and
/// Visitor&lt;TResult&gt;; CallShapes.Gauge is a class of its own. ExternalUser declares
/// ExternalLib.Namesakes.Gauge&lt;T&gt; beside ExternalLib's ExternalLib.Namesakes.Gauge, which it
/// references, references ExternalLib's Kettle, and holds the NullableAttribute the compiler
/// embeds. BaseShift2 has a class Banner that BaseShift lacks. ForwardingAssembly writes Crowd,
/// which defines classes named Leaf and Bud in many namespaces, references a Bud of Core and
/// forwards to Core Shop.Kettle and a class Twig nested in a class named as the compiler names
/// its own.
/// </para>
/// </summary>
public sealed class WrongNamespaceTests : IClassFixture<ILensServerFixture>
{
    private const string Origin = "ExternalLib, Version=1.0.0.0";
    private const string ToCore = "Core, Version=3.0.0.0";
    private const string Badges = "CallShapes.Namesakes.Alpha.Badge, CallShapes.Namesakes.Beta.Badge";
    private const string NotReferenced = "This assembly neither defines nor references a type of that name.";

    private readonly ILensServerFixture _server;
    private readonly string _folder;
    private readonly string _callShapesPath;
    private readonly string _externalUserPath;

    public WrongNamespaceTests(ILensServerFixture server)
    {
        _server = server;
        _folder = Path.GetDirectoryName(_server.AssemblyPath)!;
        _callShapesPath = Fixture("CallShapes.dll", "CallShapes");
        _externalUserPath = Fixture("ExternalUser.dll", "ExternalUser");
    }

    private string Fixture(string file, string project)
    {
        string path = Path.Combine(_folder, file);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Tests/Fixtures/{project}/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
    }

    // Every tool that needs the type's code, with the parameters it requires beside the type.
    public static TheoryData<string, string?, string?> ToolsNeedingTheCode => new()
    {
        { "decompile", null, null },
        { "list_members", null, null },
        { "find_methods", null, null },
        { "search_code", "regex", "Badge" },
    };

    [Theory]
    [MemberData(nameof(ToolsNeedingTheCode))]
    public async Task ANameUnderAnotherNamespace_IsNotFound_AndNamesTheTypesOfItsName(
        string toolName, string? parameter, string? value)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Badge",
        };
        if (parameter is not null)
            arguments[parameter] = value;

        var (result, text) = await CallToolExpectingError(toolName, arguments);

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal($"Error: ArgumentException: Type not found: CallShapes.Badge. Types named Badge: {Badges}.", text);
    }

    [Theory]
    // A name without a namespace.
    [InlineData("Badge", $"Types named Badge: {Badges}.")]
    // No class named Entry stands where the name puts one, so each class named Entry is named,
    // nested in whichever class.
    [InlineData("CallShapes.Entry", "Types named Entry: CallShapes.Namesakes.Bag.Entry, CallShapes.Namesakes.Box.Entry.")]
    // Box.Entry differs from the name only in the namespace, so Bag.Entry is left out.
    [InlineData("CallShapes.Box.Entry", "Types named Entry: CallShapes.Namesakes.Box.Entry.")]
    // A count of type parameters the name states keeps the type of that count.
    [InlineData("CallShapes.Visitor<>", "Types named Visitor: CallShapes.Namesakes.Visitor<>.")]
    [InlineData("CallShapes.Visitor", "Types named Visitor: CallShapes.Namesakes.Visitor, CallShapes.Namesakes.Visitor<>.")]
    // A name without a type of its own name anywhere keeps the error alone.
    [InlineData("CallShapes.Brooch", null)]
    public async Task ANameThatFindsNoType_NamesTheTypesItMayMean(string typeName, string? named)
    {
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            named is null
                ? $"Error: ArgumentException: Type not found: {typeName}."
                : $"Error: ArgumentException: Type not found: {typeName}. {named}",
            text);
    }

    [Fact]
    public async Task TheElementOfAnArrayType_NamesTheTypesOfItsName()
    {
        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Badge[]",
            ["member_name"] = "Pin",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal($"Error: ArgumentException: Type not found: CallShapes.Badge. Types named Badge: {Badges}.", text);
    }

    [Theory]
    // A type the assembly only references.
    [InlineData("ExternalUser.Kettle", $"Types named Kettle: this assembly references ExternalLib.Extended.Kettle from {Origin}.")]
    // A type the assembly defines and one it references, each in its group.
    [InlineData("ExternalUser.Gauge",
        $"Types named Gauge: this assembly defines ExternalLib.Namesakes.Gauge<>; it references ExternalLib.Namesakes.Gauge from {Origin}.")]
    public async Task ANameOfAReferencedType_NamesItWithItsLibrary(string typeName, string named)
    {
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal($"Error: ArgumentException: Type not found: {typeName}. {named}", text);
    }

    [Fact]
    public async Task AClassTheCompilerEmbedsUnderAnOrdinaryName_IsNamed()
    {
        // The class carries [CompilerGenerated], which leaves it out of the type lists by
        // default, but only a name in the compiler's own pattern keeps a type out of the hint.
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalUser.NullableAttribute",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: Type not found: ExternalUser.NullableAttribute. " +
            "Types named NullableAttribute: System.Runtime.CompilerServices.NullableAttribute.",
            text);
    }

    [Fact]
    public async Task Analyze_OnATypeNothingNames_EndsItsEmptyAnswerWithTheTypesOfItsName()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Badge",
            ["kind"] = "used_by",
        });

        Assert.Equal($"No users of CallShapes.Badge\n{NotReferenced} Types named Badge: {Badges}.", text);
    }

    [Fact]
    public async Task Analyze_OnAMemberOfATypeNothingNames_NamesTheTypesOfItsName()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Badge",
            ["member_name"] = "Pin",
            ["kind"] = "used_by",
        });

        string[] lines = text.Split('\n');
        Assert.Equal("No users of CallShapes.Badge.Pin", lines[0]);
        Assert.Equal($"{NotReferenced} Types named Badge: {Badges}.", lines[1]);
    }

    [Fact]
    public async Task Compare_OnANameNeitherBuildHas_NamesTheTypesOfItsNameOnceWhereBothHaveThem()
    {
        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = _callShapesPath,
            ["assembly_b"] = _callShapesPath,
            ["type_name"] = "CallShapes.Badge",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: Type 'CallShapes.Badge' not found in either assembly. Types named Badge: {Badges}.",
            text);
    }

    [Fact]
    public async Task Compare_OnANameNeitherBuildHas_SaysWhichBuildHasATypeOfItsName()
    {
        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = Path.Combine(_folder, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_folder, "BaseShift2.dll"),
            ["type_name"] = "BaseShift.Banner",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: Type 'BaseShift.Banner' not found in either assembly. " +
            "In B, types named Banner: BaseShift.Sides.Banner.",
            text);
    }

    [Fact]
    public async Task Compare_OnANameTypesOfAnotherArityBear_NamesThemAsTheOtherToolsDo()
    {
        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = _callShapesPath,
            ["assembly_b"] = _callShapesPath,
            ["type_name"] = "CallShapes.Namesakes.Visitor<,>",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: Type 'CallShapes.Namesakes.Visitor<,>' not found in either assembly. " +
            "Types of that name: CallShapes.Namesakes.Visitor, CallShapes.Namesakes.Visitor<>.",
            text);
    }

    [Theory]
    // Both builds reference the type, so the groups speak of each build.
    [InlineData("ExternalUser.dll", "ExternalUser.dll", "App.Kettle",
        $" Types named Kettle: each build references ExternalLib.Extended.Kettle from {Origin}.")]
    // The builds have other types of the name, so each build's stand in a sentence of their own.
    [InlineData("ExternalUser.dll", "CallShapes.dll", "App.Gauge",
        " In A, types named Gauge: it defines ExternalLib.Namesakes.Gauge<>; it references ExternalLib.Namesakes.Gauge " +
        $"from {Origin}. In B, types named Gauge: CallShapes.Gauge.")]
    public async Task Compare_WritesWhichBuildDefinesOrReferencesTheTypes(
        string assemblyA, string assemblyB, string typeName, string meant)
    {
        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = Path.Combine(_folder, assemblyA),
            ["assembly_b"] = Path.Combine(_folder, assemblyB),
            ["type_name"] = typeName,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal($"Error: ArgumentException: Type '{typeName}' not found in either assembly.{meant}", text);
    }

    [Fact]
    public async Task ManyTypesOfTheName_AreCutAtForty_AndAGeneratedOneOnlyWhereTheNameIsGenerated()
    {
        // 45 classes named Leaf; 41 named Bud beside Core's Aa.Bud, which sorts first.
        var leaves = Enumerable.Range(0, 45).Select(index => ($"Ns{index:00}", "Leaf"));
        var buds = Enumerable.Range(0, 41).Select(index => ($"Ns{index:00}", "Bud"));
        string path = ForwardingAssembly.Write(_folder, $"Crowd_{Guid.NewGuid():N}", new Version(1, 0, 0, 0),
            "Core", new Version(3, 0, 0, 0),
            forwarded: [("Shop", "Kettle"), ("Gen", "<Hidden>+Twig")],
            referenced: [("Aa", "Bud")],
            defined: [.. leaves, .. buds]);
        try
        {
            string leafList = string.Join(", ", Enumerable.Range(0, 40).Select(index => $"Ns{index:00}.Leaf"));
            Assert.Equal(
                $"Error: ArgumentException: Type not found: Shop.Leaf. Types named Leaf: {leafList} and 5 more.",
                await ListMembersError(path, "Shop.Leaf"));

            // The first 40 by their names, Core's Aa.Bud among them, stand in their groups.
            string budList = string.Join(", ", Enumerable.Range(0, 39).Select(index => $"Ns{index:00}.Bud"));
            Assert.Equal(
                $"Error: ArgumentException: Type not found: Shop.Bud. Types named Bud: this assembly defines {budList}; " +
                $"it references Aa.Bud from {ToCore}; and 2 more.",
                await ListMembersError(path, "Shop.Bud"));

            Assert.Equal(
                $"Error: ArgumentException: Type not found: Mall.Kettle. Types named Kettle: this assembly forwards Shop.Kettle to {ToCore}.",
                await ListMembersError(path, "Mall.Kettle"));

            // Twig is nested in a class named as the compiler names the classes it generates.
            Assert.Equal("Error: ArgumentException: Type not found: Mall.Twig.", await ListMembersError(path, "Mall.Twig"));
            Assert.Equal(
                $"Error: ArgumentException: Type not found: Mall.<Hidden>.Twig. Types named Twig: this assembly forwards Gen.<Hidden>.Twig to {ToCore}.",
                await ListMembersError(path, "Mall.<Hidden>.Twig"));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    private async Task<string> ListMembersError(string assembly, string typeName)
    {
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
        });
        Assert.True(result.IsError, $"Expected an error, got: {text}");
        return text;
    }

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and returns the
    /// concatenated text content.
    /// </summary>
    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    /// <summary>
    /// Calls an ILens MCP tool that is expected to fail, returning the raw result alongside
    /// its text so a test can assert on both.
    /// </summary>
    private async Task<(CallToolResult Result, string Text)> CallToolExpectingError(
        string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result, text);
    }
}
