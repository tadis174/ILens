using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a parameter that takes any number of arguments through a collection,
/// which C# 13 allows and the compiler marks with ParamCollectionAttribute: the declarations
/// list_members prints write params before it, as they do before an array's, and call_sites reads
/// every call to it. InterfaceShapes' Gatherer holds such parameters over a span, an enumerable and
/// a list, an indexer over a params span, a params array and a span without params, and Caller
/// calls them. compare's lines on BaseShift's Methods, whose Gather gains params on a collection
/// and Spread loses it, are pinned in ComparisonTests.
/// </summary>
public sealed class ParamsCollectionTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _interfaceShapesPath;

    public ParamsCollectionTests(ILensServerFixture server)
    {
        _server = server;
        _interfaceShapesPath = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "InterfaceShapes.dll");
        if (!File.Exists(_interfaceShapesPath))
            throw new FileNotFoundException(
                $"Fixture not found: {_interfaceShapesPath}. Tests/Fixtures/InterfaceShapes/ is expected to land " +
                "here via Tests.csproj's ProjectReference.");
    }

    [Fact]
    public async Task ListMembers_WritesParamsBeforeACollectionAsBeforeAnArray()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = "InterfaceShapes.Params.Gatherer",
        });

        Assert.Equal(
            new[]
            {
                "7 members of public class InterfaceShapes.Params.Gatherer:",
                "6 methods:",
                "  public Gatherer()",
                "  public int Count(params ReadOnlySpan<int> values)",
                "  public int Fill(params List<int> items)",
                "  public int Join(string separator, params IEnumerable<string> parts)",
                "  public int Plain(ReadOnlySpan<int> values)",
                "  public int Sum(params int[] values)",
                "1 property:",
                "  public int Item[params ReadOnlySpan<int> keys] { get; }",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("Count", "3 calls to InterfaceShapes.Params.Gatherer.Count(ReadOnlySpan<int>):", new[]
    {
        "InterfaceShapes.Params.Caller.Empty(Gatherer)",
        "InterfaceShapes.Params.Caller.Expanded(Gatherer, int, string)",
        "InterfaceShapes.Params.Caller.Whole(Gatherer, int[], List<int>)",
    })]
    [InlineData("Join", "2 calls to InterfaceShapes.Params.Gatherer.Join(string, IEnumerable<string>):", new[]
    {
        "InterfaceShapes.Params.Caller.Empty(Gatherer)",
        "InterfaceShapes.Params.Caller.Expanded(Gatherer, int, string)",
    })]
    [InlineData("Fill", "3 calls to InterfaceShapes.Params.Gatherer.Fill(List<int>):", new[]
    {
        "InterfaceShapes.Params.Caller.Empty(Gatherer)",
        "InterfaceShapes.Params.Caller.Expanded(Gatherer, int, string)",
        "InterfaceShapes.Params.Caller.Whole(Gatherer, int[], List<int>)",
    })]
    [InlineData("get_Item", "1 call to InterfaceShapes.Params.Gatherer.get_Item(ReadOnlySpan<int>):", new[]
    {
        "InterfaceShapes.Params.Caller.Indexed(Gatherer, int)",
    })]
    // The control: a params array.
    [InlineData("Sum", "3 calls to InterfaceShapes.Params.Gatherer.Sum(int[]):", new[]
    {
        "InterfaceShapes.Params.Caller.Empty(Gatherer)",
        "InterfaceShapes.Params.Caller.Expanded(Gatherer, int, string)",
        "InterfaceShapes.Params.Caller.Whole(Gatherer, int[], List<int>)",
    })]
    public async Task CallSites_OfAParameterThatTakesAnyNumberOfArguments_ListsEveryCall(
        string memberName, string header, string[] callers)
    {
        // call_sites keeps ILSpy's IsParams, which reads a params array alone, since the decompiled
        // call passes the collection the compiler builds as one argument. Read as params, the
        // indexer over a collection would be left out and its call reported as not shown.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = "InterfaceShapes.Params.Gatherer",
            ["member_name"] = memberName,
            ["kind"] = "call_sites",
        });

        var lines = text.Split('\n');
        Assert.Equal(header, lines[0]);
        Assert.Equal(callers, lines.Skip(1).Where(line => !line.StartsWith("  ")));
        Assert.DoesNotContain("Not shown", text);
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
}
