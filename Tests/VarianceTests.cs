using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the in and out C# declares a variant type parameter of an interface or a
/// delegate with: list_members writes them in the heading as the decompiler writes them, on the
/// type's own type parameters alone, and compare counts a change of them as a change of the type's
/// metadata. BaseShift.Variance holds the types, and BaseShift2 the same types with in or out
/// gained, lost or swapped on five of them.
/// </summary>
public sealed class VarianceTests : IClassFixture<ILensServerFixture>
{
    private const string Variance = "BaseShift.Variance";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public VarianceTests(ILensServerFixture server)
    {
        _server = server;
        _directory = Path.GetDirectoryName(_server.AssemblyPath)!;
        foreach (var name in new[] { "BaseShift.dll", "BaseShift2.dll" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    // A variant type parameter carries its in or out before its name, beside an invariant one and
    // before a where clause, in an interface's heading and a delegate's. A type nested in a variant
    // interface carries its own in alone, since the enclosing interface declares its out on itself.
    // Each build writes what it declares.
    [Theory]
    [InlineData("BaseShift.dll", "IMapper<,,>",
        $"1 member of public interface {Variance}.IMapper<in TIn, TKeep, out TOut> where TKeep : class:")]
    [InlineData("BaseShift.dll", "Mapping<,>", $"4 members of public delegate {Variance}.Mapping<in TIn, out TOut>:")]
    [InlineData("BaseShift.dll", "IOuter<>", $"1 member of public interface {Variance}.IOuter<out T>:")]
    [InlineData("BaseShift.dll", "IOuter<>.IInner<>", $"1 member of public interface {Variance}.IOuter<T>.IInner<in U>:")]
    [InlineData("BaseShift.dll", "ISwitch<>", $"No members of public interface {Variance}.ISwitch<in T>")]
    [InlineData("BaseShift2.dll", "ISwitch<>", $"No members of public interface {Variance}.ISwitch<out T>")]
    [InlineData("BaseShift.dll", "Handler<>", $"4 members of public delegate {Variance}.Handler<in T>:")]
    [InlineData("BaseShift2.dll", "Handler<>", $"4 members of public delegate {Variance}.Handler<T>:")]
    public async Task ListMembers_WritesInAndOutInTheHeading(string assembly, string typeName, string heading)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, assembly),
            ["type_name"] = $"{Variance}.{typeName}",
        });

        Assert.Equal(heading, text.Split('\n')[0]);
    }

    [Fact]
    public async Task Compare_ChangedTypes_CountsAChangeOfInOrOutAsMetadata()
    {
        // A type parameter that gains, loses or swaps its in or out changes nothing else in these
        // types, and the types that keep theirs are not listed.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
            ["namespace"] = Variance,
        });

        Assert.Equal(
            new[]
            {
                "5 changed types:",
                $"~ {Variance}.Factory<> [metadata]",
                $"~ {Variance}.Handler<> [metadata]",
                $"~ {Variance}.IProducer<> [metadata]",
                $"~ {Variance}.ISource<> [metadata]",
                $"~ {Variance}.ISwitch<> [metadata]",
            },
            text.Split('\n'));
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
