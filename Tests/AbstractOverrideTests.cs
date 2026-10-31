using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a member that is abstract and overrides a member of a base class.
/// list_members writes it as abstract override, as C# and the decompiled code do, since abstract
/// alone below a member of the same signature declares a new member that hides that one, and
/// compare counts a change between the two as a changed declaration. BaseShift.Declarations'
/// Motor declares the virtual Start, Speed and Started of its base class Engine abstract anew,
/// and BaseShift2's Motor declares them as abstract overrides. WhereClauseTests and
/// ReturnOverrideTests pin the line of a generic abstract override and of one that returns a more
/// specific type.
/// </summary>
public sealed class AbstractOverrideTests : IClassFixture<ILensServerFixture>
{
    private const string Motor = "BaseShift.Declarations.Motor";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public AbstractOverrideTests(ILensServerFixture server)
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

    // A method, a property and an event that override take abstract override. The same members
    // declared abstract anew keep abstract alone, although the base class has members of their
    // signatures.
    [Theory]
    [InlineData("BaseShift2.dll",
        "4 members of public abstract class BaseShift.Declarations.Motor : Engine:\n" +
        "2 methods:\n" +
        "  protected Motor()\n" +
        "  public abstract override void Start()\n" +
        "1 property:\n" +
        "  public abstract override int Speed { get; }\n" +
        "1 event:\n" +
        "  public abstract override event EventHandler Started")]
    [InlineData("BaseShift.dll",
        "4 members of public abstract class BaseShift.Declarations.Motor : Engine:\n" +
        "2 methods:\n" +
        "  protected Motor()\n" +
        "  public abstract void Start()\n" +
        "1 property:\n" +
        "  public abstract int Speed { get; }\n" +
        "1 event:\n" +
        "  public abstract event EventHandler Started")]
    public async Task ListMembers_WritesAbstractOverrideApartFromAMemberDeclaredAbstractAnew(string assembly, string expected)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, assembly),
            ["type_name"] = Motor,
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task Compare_MemberChanges_CountAMemberThatOverridesInOneBuildOnlyAsAChangedDeclaration()
    {
        string text = await CallToolText("compare", Builds(new() { ["type_name"] = Motor }));

        Assert.Equal(
            new[]
            {
                $"3 member changes in {Motor}:",
                "~ public abstract int Speed { get; } → public abstract override int Speed { get; }",
                "~ public abstract void Start() → public abstract override void Start()",
                "~ public abstract event EventHandler Started → public abstract override event EventHandler Started",
            },
            text.Split('\n'));
    }

    // An accessor opens with the declarations of its property or event, as a method opens with its
    // own. Each is abstract, so neither build has a body for it.
    [Theory]
    [InlineData("Start",
        "// public abstract void Start() → public abstract override void Start()\n" +
        $"// {Motor}.Start() → void — neither assembly has a body for it: abstract in both.")]
    [InlineData("get_Speed",
        "// public abstract int Speed { get; } → public abstract override int Speed { get; }\n" +
        $"// {Motor}.get_Speed() → int — neither assembly has a body for it: abstract in both.")]
    [InlineData("add_Started",
        "// public abstract event EventHandler Started → public abstract override event EventHandler Started\n" +
        $"// {Motor}.add_Started(EventHandler) → void — neither assembly has a body for it: abstract in both.")]
    public async Task Compare_Bodies_OfAMemberThatOverridesInOneBuildOnly_NamesBothDeclarationsFirst(string member, string expected)
    {
        string text = await CallToolText("compare", Builds(new() { ["type_name"] = Motor, ["member_name"] = member }));

        Assert.Equal(expected, text);
    }

    private Dictionary<string, object?> Builds(Dictionary<string, object?> arguments)
    {
        arguments["assembly_a"] = Path.Combine(_directory, "BaseShift.dll");
        arguments["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll");
        return arguments;
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
