using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the name filters, name_contains and type_name_contains, given a name that
/// ends in angle brackets as ILens writes a generic type or method: the brackets count the type
/// parameters, whatever names or types they hold, the text before them matches the end of the
/// name, a nested type counts its own type parameters alone, and a name that bears brackets as it
/// stands still matches as it stands. The cases are in CallShapes' NameFilters.cs and in the
/// classes TemplateNameAssembly writes.
/// </summary>
public sealed class NameFilterTests : IClassFixture<ILensServerFixture>
{
    private const string Namespace = "CallShapes.NameFilters";
    private const string Kiln = "CallShapes.NameFilters.Kiln<>";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public NameFilterTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "CallShapes.dll");
    }

    // Each name lists one type: Hive, Hive<T, U> and HiveFrame<T> are namesakes of Hive<T> with
    // another count or a longer name, and Kiln<T>.Batch is the namesake of Kiln<T>.Batch<U>.
    [Theory]
    [InlineData("Hive<>", "CallShapes.NameFilters.Hive<>")]
    [InlineData("Hive<T>", "CallShapes.NameFilters.Hive<>")]
    [InlineData("hive<int>", "CallShapes.NameFilters.Hive<>")]
    [InlineData("Hive<string, List<int>>", "CallShapes.NameFilters.Hive<,>")]
    [InlineData("ive<,>", "CallShapes.NameFilters.Hive<,>")]
    [InlineData("Frame<>", "CallShapes.NameFilters.HiveFrame<>")]
    [InlineData("Batch<>", "CallShapes.NameFilters.Kiln<>.Batch<>")]
    public async Task FindTypes_ReadsBracketsThatEndTheTextAsTheTypeParameters(string text, string type)
    {
        Assert.Equal($"1 type:\n{type}", await CallToolText("find_types", new()
        {
            ["assembly"] = _callShapesPath,
            ["namespace"] = Namespace,
            ["name_contains"] = text,
        }));
    }

    [Theory]
    [InlineData("Fire<int>", "Fire<U>(U)")]
    [InlineData("Fire<T>", "Fire<U>(U)")]
    [InlineData("ire<>", "Fire<U>(U)")]
    [InlineData("Fire<U, V>", "Fire<U, V>(U, V)")]
    [InlineData("Again<int>", "FireAgain<U>(U)")]
    public async Task FindMethods_ReadsBracketsThatEndTheTextAsTheTypeParameters(string text, string method)
    {
        Assert.Equal($"1 method:\n{Kiln}.{method} → void", await CallToolText("find_methods", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Kiln,
            ["name_contains"] = text,
        }));
    }

    // The nested classes Batch and Batch<U> are left out: their names do not end in Kiln.
    [Theory]
    [InlineData("Kiln<int>")]
    [InlineData("Kiln<>")]
    [InlineData("iln<T>")]
    public async Task FindMethods_ReadsBracketsThatEndTheClassNameAsItsTypeParameters(string text)
    {
        Assert.Equal(
            $"5 methods:\n{Kiln}..ctor(T) → void\n{Kiln}.Fire() → void\n{Kiln}.Fire<U, V>(U, V) → void\n" +
            $"{Kiln}.Fire<U>(U) → void\n{Kiln}.FireAgain<U>(U) → void",
            await CallToolText("find_methods", new()
            {
                ["assembly"] = _callShapesPath,
                ["namespace"] = Namespace,
                ["type_name_contains"] = text,
            }));
    }

    // A constructor is found by its class's name, read the same way.
    [Theory]
    [InlineData("Fire<int>", "1 method:\n  public void Fire<U>(U item)")]
    [InlineData("Batch<>", "1 nested type:\n  CallShapes.NameFilters.Kiln<>.Batch<>")]
    [InlineData("Kiln<>", "1 method:\n  public Kiln(T stock)")]
    public async Task ListMembers_ReadsBracketsThatEndTheTextAsTheTypeParameters(string text, string members)
    {
        Assert.Equal($"1 member of public class CallShapes.NameFilters.Kiln<T>:\n{members}",
            await CallToolText("list_members", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = Kiln,
                ["name_contains"] = text,
            }));
    }

    // A class that bears brackets in its own name, as C++/CLI names the class of a template
    // instance, matches as it stands, beside the generic class that the brackets read as type
    // parameters find; that reading alone leaves such a class out.
    [Fact]
    public async Task FindTypes_MatchesANameThatBearsBracketsAsItStands()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            Assert.Equal("2 types:\nNative.Wrappers.Handle<>\nNative.Wrappers.Handle<_GUID>",
                await CallToolText("find_types", new() { ["assembly"] = path, ["name_contains"] = "Handle<_GUID>" }));
            Assert.Equal("1 type:\ngcroot<System::String ^>",
                await CallToolText("find_types", new() { ["assembly"] = path, ["name_contains"] = "gcroot<System::String ^>" }));
            Assert.Equal("1 type:\nNative.Wrappers.Handle<>",
                await CallToolText("find_types", new() { ["assembly"] = path, ["name_contains"] = "Handle<>" }));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
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
