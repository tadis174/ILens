using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the line ends of code in answers. The decompiler and the disassembler
/// end a line with the platform's line end unless the writer says otherwise, a carriage return and
/// a line feed on Windows, while ILens writes its own lines with a line feed; every path that
/// writes code over several lines ends each line with a line feed alone. The paths are those of
/// decompile, one for each kind of type and member, and of compare where it prints code or IL.
/// SearchText and BaseShift hold the types; BaseShift2 is BaseShift's other build.
/// </summary>
public sealed class LineEndTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public LineEndTests(ILensServerFixture server)
    {
        _server = server;
    }

    [Theory]
    [InlineData("SearchText.dll", "SearchText.Outer", null)]
    [InlineData("SearchText.dll", "SearchText.Outer.Inner", null)]
    [InlineData("SearchText.dll", "<>f__AnonymousType0<,>", null)]
    [InlineData("BaseShift.dll", "BaseShift.Diffs.Ledger", "Settle")]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "Caption")]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "get_Caption")]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "Closed")]
    [InlineData("BaseShift.dll", "BaseShift.Diffs.Ledger", "_count")]
    public async Task DecompiledCode_EndsEachLineWithALineFeedAlone(string assembly, string typeName, string? memberName)
    {
        var arguments = new Dictionary<string, object?> { ["assembly"] = FixturePath(assembly), ["type_name"] = typeName };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        string text = await CallToolText("decompile", arguments);

        Assert.Contains('\n', text);
        Assert.DoesNotContain('\r', text);
    }

    // The code of a member only one build has, and the IL of a name that is a member of another
    // kind in each build, which compare prints whole rather than as a diff.
    [Theory]
    [InlineData("BaseShift.Sides.Panel", "Resized", null)]
    [InlineData("BaseShift.Kinds.Till", "Open", "il")]
    public async Task ComparedCode_EndsEachLineWithALineFeedAlone(string typeName, string memberName, string? format)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly_a"] = FixturePath("BaseShift.dll"),
            ["assembly_b"] = FixturePath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        };
        if (format is not null)
            arguments["format"] = format;

        string text = await CallToolText("compare", arguments);

        Assert.True(text.Split('\n').Length > 3, text);
        Assert.DoesNotContain('\r', text);
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"{toolName} returned an error: {text}");
        return text;
    }

    /// <summary>
    /// The path of a fixture assembly, which Tests.csproj's ProjectReference puts beside the
    /// shared inspection target.
    /// </summary>
    private string FixturePath(string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Its project under Tests/Fixtures/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
    }
}
