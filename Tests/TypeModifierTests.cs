using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the words a type's heading writes before its name: compare counts a struct
/// that becomes readonly or a ref struct, and a class or struct that becomes a record or stops being
/// one, as a change of the type's metadata, and its type view ends with both headings.
/// BaseShift.Modifiers holds the types, and BaseShift2 the same types with a word gained or lost on
/// five of them.
/// </summary>
public sealed class TypeModifierTests : IClassFixture<ILensServerFixture>
{
    private const string Modifiers = "BaseShift.Modifiers";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public TypeModifierTests(ILensServerFixture server)
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

    [Fact]
    public async Task Compare_ChangedTypes_CountsReadonlyRefAndRecordAsMetadata()
    {
        // The structs gain readonly or ref and change nothing else. A type that becomes a record or
        // stops being one also gains or loses the members a record brings. The readonly struct, the
        // ref struct and the record that keep their words are not listed.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
            ["namespace"] = Modifiers,
        });

        Assert.Equal(
            new[]
            {
                "5 changed types:",
                $"~ {Modifiers}.Frozen [metadata]",
                $"~ {Modifiers}.Pair [metadata,members]",
                $"~ {Modifiers}.Pinned [metadata]",
                $"~ {Modifiers}.Point [metadata,members]",
                $"~ {Modifiers}.Ticket [metadata,members]",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("Frozen",
        $"No member changes in {Modifiers}.Frozen\n" +
        $"Declaration: public struct {Modifiers}.Frozen → public readonly struct {Modifiers}.Frozen")]
    [InlineData("Pinned",
        $"No member changes in {Modifiers}.Pinned\n" +
        $"Declaration: public struct {Modifiers}.Pinned → public ref struct {Modifiers}.Pinned")]
    public async Task Compare_MemberChanges_OfAStructThatGainsAWord_EndsWithBothDeclarations(string typeName, string expected)
    {
        string text = await CompareType(typeName);

        Assert.Equal(expected, text);
    }

    // A record implements IEquatable of itself, so the heading names that interface beside the word.
    // The members a record brings stand above the line, added or removed.
    [Theory]
    [InlineData("Point", $"9 member changes in {Modifiers}.Point:",
        $"Declaration: public class {Modifiers}.Point → public record {Modifiers}.Point : IEquatable<Point>")]
    [InlineData("Pair", $"7 member changes in {Modifiers}.Pair:",
        $"Declaration: public struct {Modifiers}.Pair → public record struct {Modifiers}.Pair : IEquatable<Pair>")]
    [InlineData("Ticket", $"9 member changes in {Modifiers}.Ticket:",
        $"Declaration: public record {Modifiers}.Ticket : IEquatable<Ticket> → public class {Modifiers}.Ticket")]
    public async Task Compare_MemberChanges_OfATypeThatBecomesARecordOrStopsBeingOne_EndsWithBothDeclarations(
        string typeName, string header, string declaration)
    {
        string[] lines = (await CompareType(typeName)).Split('\n');

        Assert.Equal(header, lines[0]);
        Assert.Equal(declaration, lines[^1]);
        Assert.Single(lines, line => line.StartsWith("Declaration: "));
    }

    private Task<string> CompareType(string typeName) => CallToolText("compare", new()
    {
        ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
        ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
        ["type_name"] = $"{Modifiers}.{typeName}",
    });

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
