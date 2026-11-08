using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how a C# 14 extension block's members are listed, searched and named. The
/// compiler makes a static method of each member, get_Doubled(Gauge) of a property's getter, and
/// every answer names a member by that method. list_members lists the members under their block,
/// as the decompiled class declares them, with the names of the methods the compiler made of a
/// property's accessors, and its filters take a member as its block declares it; blocks whose
/// lines read alike are one group. search_code writes the block as decompile writes the class, and
/// a line of a member belongs to the method the compiler made of it: an accessor's to its
/// accessor's method, a property's other lines to its getter's, and the block's own lines to the
/// class. The error for a property's name names the methods of its accessors. The fixture is
/// InterfaceShapes' GaugeExtensions, with a block for Gauge holding a member of each shape, a
/// block with a type parameter, and a second block for Gauge with an attribute on its receiver.
/// Each test fails on a build whose search_code writes the compiler's methods and whose
/// list_members lists them.
/// </summary>
public sealed class ExtensionBlockTests : IClassFixture<ILensServerFixture>
{
    private const string Blocks = "InterfaceShapes.Language.GaugeExtensions";

    private readonly ILensServerFixture _server;
    private readonly string _interfaceShapesPath;

    public ExtensionBlockTests(ILensServerFixture server)
    {
        _server = server;
        _interfaceShapesPath = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "InterfaceShapes.dll");
    }

    // The members of each block under the block's line, a property with its accessors' methods.
    // An internal member is left out by default, as any other is.
    [Fact]
    public async Task ListMembers_ListsABlocksMembersUnderTheBlock()
    {
        Assert.Equal(
            new[]
            {
                $"6 members of public static class {Blocks}:",
                "5 members of extension(Gauge gauge):",
                "  public int Doubled { get; }  [get_Doubled]",
                "  public static Gauge Empty()",
                "  public bool IsZero()",
                "  public int Offset { get; set; }  [get_Offset, set_Offset]",
                "  public void Reset()",
                "1 member of extension<T>(List<T> readings) where T : IComparable<T>:",
                "  public T First()",
                "Not shown: 1 internal or private",
            },
            (await ListMembers(new())).Split('\n'));
    }

    // A block's property is a property, although the compiler made methods of its accessors.
    [Fact]
    public async Task ListMembers_TakesABlocksMemberByItsOwnKind()
    {
        Assert.Equal(
            new[]
            {
                $"2 members of public static class {Blocks}:",
                "2 members of extension(Gauge gauge):",
                "  public int Doubled { get; }  [get_Doubled]",
                "  public int Offset { get; set; }  [get_Offset, set_Offset]",
            },
            (await ListMembers(new() { ["kinds"] = new[] { "property" } })).Split('\n'));
    }

    // The second block for Gauge differs from the first only in an attribute on its receiver,
    // which no line writes, so its IsZero stands in the first block's group beside Empty and Reset.
    [Fact]
    public async Task ListMembers_ListsBlocksWhoseLinesReadAlikeAsOne()
    {
        Assert.Equal(
            new[]
            {
                $"4 members of public static class {Blocks}:",
                "3 members of extension(Gauge gauge):",
                "  public static Gauge Empty()",
                "  public bool IsZero()",
                "  public void Reset()",
                "1 member of extension<T>(List<T> readings) where T : IComparable<T>:",
                "  public T First()",
                "Not shown: 1 internal or private",
            },
            (await ListMembers(new() { ["kinds"] = new[] { "method" } })).Split('\n'));
    }

    // The name filter finds a property by the name of a method the compiler made of it too.
    [Fact]
    public async Task ListMembers_FindsABlocksPropertyByTheNameOfItsSetter()
    {
        Assert.Equal(
            new[]
            {
                $"1 member of public static class {Blocks}:",
                "1 member of extension(Gauge gauge):",
                "  public int Offset { get; set; }  [get_Offset, set_Offset]",
            },
            (await ListMembers(new() { ["name_contains"] = "set_Off" })).Split('\n'));
    }

    // Every accessibility shows the internal member in its block; a limit cuts a block as it cuts
    // a group of a kind, which keeps its full count.
    [Fact]
    public async Task ListMembers_ShowsAnInternalBlockMemberAndCutsABlockAtTheLimit()
    {
        Assert.Equal(
            new[]
            {
                $"7 members of public static class {Blocks}:",
                "6 members of extension(Gauge gauge):",
                "  internal void Calibrate()",
                "  public int Doubled { get; }  [get_Doubled]",
                "... (5 more; raise limit to see all)",
            },
            (await ListMembers(new() { ["accessibility"] = "all", ["limit"] = 2 })).Split('\n'));
    }

    // The line of a property's getter, written as the block declares it, under its method.
    [Fact]
    public async Task SearchCode_WritesABlockAsDecompileWritesItsClass()
    {
        Assert.Equal(
            new[]
            {
                $"1 line matching gauge.Level [*] 2 in {Blocks}:",
                $"{Blocks}.get_Doubled(Gauge)",
                "  public int Doubled => gauge.Level * 2;",
            },
            (await SearchCode("gauge.Level [*] 2")).Split('\n'));
    }

    // The block's line belongs to the class, a property's header and getter to the getter's
    // method, the setter's keyword and body to the setter's, and a method's lines to its method.
    [Fact]
    public async Task SearchCode_ListsEachLineOfABlockMemberUnderItsMethod()
    {
        const string pattern =
            @"^(extension\(Gauge|public int Offset|get$|set$|return gauge\.Level - 1|gauge\.Level = value|public void Reset)";

        Assert.Equal(
            new[]
            {
                $"7 lines matching {pattern} in {Blocks}:",
                Blocks,
                "  extension(Gauge gauge)",
                $"{Blocks}.Reset(Gauge)",
                "  public void Reset()",
                $"{Blocks}.get_Offset(Gauge)",
                "  public int Offset",
                "  get",
                "  return gauge.Level - 1;",
                $"{Blocks}.set_Offset(Gauge, int)",
                "  set",
                "  gauge.Level = value + 1;",
            },
            (await SearchCode(pattern)).Split('\n'));
    }

    // Every answer names a block's property by the methods of its accessors, which the error for
    // its own name names; the name in the bracket of list_members passes on.
    [Fact]
    public async Task Decompile_OfABlockPropertysName_NamesTheMethodsOfItsAccessors()
    {
        string decompiled = await CallToolError("decompile", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = Blocks,
            ["member_name"] = "Offset",
        });
        string analyzed = await CallToolError("analyze", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = Blocks,
            ["member_name"] = "Doubled",
            ["kind"] = "used_by",
        });
        string setter = await CallToolText("decompile", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = Blocks,
            ["member_name"] = "set_Offset",
        });

        Assert.Equal(
            $"Error: ArgumentException: Member 'Offset' not found on {Blocks}, its base types, or as an extension " +
            $"method; Offset is a property of an extension block: {Blocks}.get_Offset(Gauge), {Blocks}.set_Offset(Gauge, int).",
            decompiled);
        Assert.Equal(
            $"Error: ArgumentException: Member 'Doubled' not found on {Blocks}, its base types, or as an extension " +
            $"method; Doubled is a property of an extension block: {Blocks}.get_Doubled(Gauge).",
            analyzed);
        Assert.Equal($"// {Blocks}.set_Offset(Gauge, int) → void", setter.Split('\n')[0]);
    }

    private Task<string> ListMembers(Dictionary<string, object?> arguments)
    {
        arguments["assembly"] = _interfaceShapesPath;
        arguments["type_name"] = Blocks;
        return CallToolText("list_members", arguments);
    }

    private Task<string> SearchCode(string pattern) => CallToolText("search_code", new()
    {
        ["assembly"] = _interfaceShapesPath,
        ["type_name"] = Blocks,
        ["regex"] = pattern,
    });

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
