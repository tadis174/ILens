using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what analyze leaves out when asked who uses a type: the type's own
/// members, however the compiler arranged their code. The fixture's
/// <c>CallShapes.SelfUse.Hermit</c> calls its own methods from a lambda, a lambda that
/// captures, a local function, an iterator, an async method and a plain method, and nothing
/// else uses it; <c>Innkeeper</c> does the same from one lambda and is used by <c>Lodger</c>.
/// </summary>
public sealed class SelfUseTests : IClassFixture<ILensServerFixture>
{
    private const string SelfUse = "CallShapes.SelfUse";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public SelfUseTests(ILensServerFixture server)
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

    // The compiler moves the code of Hermit's lambdas, iterator and async method into classes
    // nested in Hermit. A use found there belongs to the method it was written in, which is
    // Hermit's own, and the fields those classes keep the instance in are Hermit's own as well:
    // neither is a user, and nothing was left out that the switch would list.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsedBy_OnATypeOnlyItsOwnCodeUses_ListsNoUser_WhateverTheSwitchSays(bool includeGenerated)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{SelfUse}.Hermit",
            ["kind"] = "used_by",
            ["include_compiler_generated"] = includeGenerated,
        });

        Assert.Equal($"No users of {SelfUse}.Hermit", text);
    }

    // Lodger's lambda calls Innkeeper, so the use is reported under Lodger.Visit, the method
    // the lambda is written in. Innkeeper's own lambda calls Innkeeper too and is not listed.
    [Fact]
    public async Task UsedBy_ListsAnotherClassesLambdaUnderItsMethod_AndNotTheTypesOwn()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{SelfUse}.Innkeeper",
            ["kind"] = "used_by",
        });

        Assert.Equal(
            $"1 user of {SelfUse}.Innkeeper:\n" +
            $"{SelfUse}.Lodger.Visit(Innkeeper)",
            text);
    }

    // The class the compiler made for Lodger's lambda keeps the captured Innkeeper in a
    // field. That class is nested in Lodger, not in Innkeeper, so the switch lists the field,
    // and still nothing from the class made for Innkeeper's own lambda.
    [Fact]
    public async Task UsedBy_WithTheSwitch_ListsWhatOtherClassesGenerated_AndNothingInsideTheType()
    {
        string[] lines = (await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{SelfUse}.Innkeeper",
            ["kind"] = "used_by",
            ["include_compiler_generated"] = true,
        })).Split('\n');

        Assert.Equal($"2 users of {SelfUse}.Innkeeper:", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^CallShapes\.SelfUse\.Lodger\.<>c__DisplayClass\d+_\d+\.innkeeper$", lines[1]);
        Assert.Equal($"{SelfUse}.Lodger.Visit(Innkeeper)", lines[2]);
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
