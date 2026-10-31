using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for compare asked by the name of a type whose name stands for a class with other
/// type parameters in the other build. Asked by a name its list of changed types writes, compare
/// answers that only one build has the type, as the list says, where each build's own lookup of a
/// name without a count would find the generic class of the other and compare the two as one. A
/// name that fits neither build's class exactly and finds a different class in each is an error
/// that names both. BaseShift.Arity holds the classes, and BaseShift2 the classes of their names
/// with other type parameters.
/// </summary>
public sealed class ChangedArityTests : IClassFixture<ILensServerFixture>
{
    private const string Arity = "BaseShift.Arity";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public ChangedArityTests(ILensServerFixture server)
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
    public async Task Compare_ChangedTypes_ListsTheClassesOfOtherTypeParametersApart()
    {
        // Each class of one build is a removal and its namesake with other type parameters an
        // addition, which the tests below ask about by the names this list writes.
        string text = await CallToolText("compare", Arguments(forward: true, new() { ["namespace"] = Arity }));

        Assert.Equal(
            new[]
            {
                "18 changed types:",
                $"~ {Arity}.Box<> [members]",
                $"~ {Arity}.Clock [members]",
                $"- {Arity}.Command",
                $"+ {Arity}.Command<,>",
                $"+ {Arity}.Command<>",
                $"+ {Arity}.Crate<,>",
                $"- {Arity}.Crate<>",
                $"- {Arity}.Gains",
                $"+ {Arity}.Gains<>",
                $"- {Arity}.Host.Step",
                $"+ {Arity}.Host.Step<>",
                $"+ {Arity}.Pile",
                $"~ {Arity}.Reader [metadata,members]",
                $"- {Arity}.Reader.Cursor",
                $"- {Arity}.Reader.Cursor.Mark",
                $"+ {Arity}.Reader<>",
                $"+ {Arity}.Reader<>.Cursor",
                $"+ {Arity}.Reader<>.Cursor.Mark",
            },
            text.Split('\n'));
    }

    // Gains gains a type parameter; a static Pile joins Pile<T>; Reader's Cursor and the class nested
    // in it move into Reader<T> beside a static Reader; Host's Step gains a type parameter; Command
    // gives way to two generic classes, where a lookup of its name in B alone matches both.
    [Theory]
    [InlineData(true, "Gains",
        $"2 member changes in {Arity}.Gains, which only A has:\n- public Gains()\n- public int Count")]
    [InlineData(true, "Pile",
        $"1 member change in {Arity}.Pile, which only B has:\n+ public static Pile<T> Of<T>(T top)")]
    [InlineData(true, "Reader.Cursor",
        $"2 member changes in {Arity}.Reader.Cursor, which only A has:\n- public Cursor()\n- public bool Next()")]
    [InlineData(true, "Reader.Cursor.Mark",
        $"2 member changes in {Arity}.Reader.Cursor.Mark, which only A has:\n- public Mark()\n- public int Line")]
    [InlineData(true, "Host.Step",
        $"2 member changes in {Arity}.Host.Step, which only A has:\n- public Step()\n- public int Value")]
    [InlineData(true, "Command",
        $"2 member changes in {Arity}.Command, which only A has:\n- public Command()\n- public void Run()")]
    [InlineData(false, "Gains",
        $"2 member changes in {Arity}.Gains, which only B has:\n+ public Gains()\n+ public int Count")]
    [InlineData(false, "Pile",
        $"1 member change in {Arity}.Pile, which only A has:\n- public static Pile<T> Of<T>(T top)")]
    [InlineData(false, "Reader.Cursor",
        $"2 member changes in {Arity}.Reader.Cursor, which only B has:\n+ public Cursor()\n+ public bool Next()")]
    public async Task Compare_MemberChanges_OfAClassOnlyOneBuildHas_ByItsListedName_SaysOnlyThatBuildHasIt(
        bool forward, string typeName, string expected)
    {
        string text = await CallToolText("compare",
            Arguments(forward, new() { ["type_name"] = $"{Arity}.{typeName}" }));

        Assert.Equal(expected, text);
    }

    // Asked in reverse, A is the build with two generic classes named Command, where its own lookup
    // of the name is an error. The lookup of an extension method, which comes first, reads the name
    // through the pair as well.
    [Theory]
    [InlineData(true, "Reader.Cursor", "Next", $"// {Arity}.Reader.Cursor.Next() → bool — only A has it.")]
    [InlineData(true, "Command", "Run", $"// {Arity}.Command.Run() → void — only A has it.")]
    [InlineData(false, "Command", "Run", $"// {Arity}.Command.Run() → void — only B has it.")]
    public async Task Compare_AMethodOfAClassOnlyOneBuildHas_ByItsListedName_SaysOnlyThatBuildHasIt(
        bool forward, string typeName, string memberName, string expected)
    {
        string text = await CallToolText("compare",
            Arguments(forward, new() { ["type_name"] = $"{Arity}.{typeName}", ["member_name"] = memberName }));

        Assert.Equal(expected, text.Split('\n')[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Item")]
    public async Task Compare_ANameThatFitsNoClassExactly_AndFindsADifferentOneInEachBuild_IsAnErrorNamingBoth(
        string? memberName)
    {
        var arguments = new Dictionary<string, object?> { ["type_name"] = $"{Arity}.Crate" };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        CallToolResult result = await _server.Client.CallToolAsync("compare", Arguments(forward: true, arguments));

        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: Type name '{Arity}.Crate' matches {Arity}.Crate<> in A and {Arity}.Crate<,> " +
            "in B. Name one as listed.",
            text);
    }

    // A name both builds read as one generic class still compares that class, and a name with its
    // count still names the class of that count alone.
    [Theory]
    [InlineData("Box", $"1 member change in {Arity}.Box<>:\n+ public int Count")]
    [InlineData("Crate<>", $"2 member changes in {Arity}.Crate<>, which only A has:\n- public Crate()\n- public T Item")]
    public async Task Compare_MemberChanges_OfANameThatFindsOneClassInBoth_OrStatesItsCount_ComparesThatClass(
        string typeName, string expected)
    {
        string text = await CallToolText("compare",
            Arguments(forward: true, new() { ["type_name"] = $"{Arity}.{typeName}" }));

        Assert.Equal(expected, text);
    }

    // Both assemblies define System.Version<T>, and BaseShift2 also references the core library's
    // System.Version, which its own lookup of the name takes, since the name states no count. The
    // class the other build's lookup finds is looked for in this one by its reflection name.
    [Theory]
    [InlineData(true, "1 member change in System.Version<>:\n+ public int Minor")]
    [InlineData(false, "1 member change in System.Version<>:\n- public int Minor")]
    public async Task Compare_MemberChanges_ByANameOneBuildReadsAsAClassItReferences_ComparesTheClassBothDefine(
        bool forward, string expected)
    {
        string text = await CallToolText("compare", Arguments(forward, new() { ["type_name"] = "System.Version" }));

        Assert.Equal(expected, text);
    }

    // The lookup in one assembly keeps its rule: a name without a count means the only class of that
    // name, here the Cursor nested in the generic Reader beside a static Reader that has no Cursor.
    [Fact]
    public async Task ListMembers_ANameWithoutACountInOneAssembly_StillFindsTheOnlyClassOfThatName()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, "BaseShift2.dll"),
            ["type_name"] = $"{Arity}.Reader.Cursor",
        });

        Assert.Equal($"3 members of public class {Arity}.Reader<T>.Cursor:", text.Split('\n')[0]);
    }

    private Dictionary<string, object?> Arguments(bool forward, Dictionary<string, object?> arguments)
    {
        var (a, b) = forward ? ("BaseShift.dll", "BaseShift2.dll") : ("BaseShift2.dll", "BaseShift.dll");
        arguments["assembly_a"] = Path.Combine(_directory, a);
        arguments["assembly_b"] = Path.Combine(_directory, b);
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
