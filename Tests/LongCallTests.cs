using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how analyze's call_sites shortens a call, or with parameter_name an
/// argument, whose code runs past 300 characters, against CallShapes' <c>LongCalls.cs</c>: the
/// bodies of lambdas and anonymous methods are left out first, then the entries of
/// initializers, and what is still too long is cut; an argument that is itself a lambda or an
/// anonymous method keeps its body and is cut; and the answer counts the shortened lines it
/// shows.
/// </summary>
public sealed class LongCallTests : IClassFixture<ILensServerFixture>
{
    private const string Shortened = "; ... marks the code left out";

    // The values Callers.ManyWords passes, in its order.
    private static readonly string[] Words =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india",
        "juliett", "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo",
        "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu",
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india",
    ];

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public LongCallTests(ILensServerFixture server)
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

    [Fact]
    public async Task CallSites_LeavesOutALongLambdaBody_AndKeepsWhatFollowsIt()
    {
        string text = await CallSites("Run");

        Assert.Equal(
            "2 calls to CallShapes.LongCalls.Target.Run(string, Action, bool):\n" +
            "CallShapes.LongCalls.Callers.Brief()\n" +
            "  Target.Run(\"brief\", () => { Console.WriteLine(\"brief\"); }, true)\n" +
            "CallShapes.LongCalls.Callers.LongLambda()\n" +
            "  Target.Run(\"lambda\", () => ..., true)\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_CountsOnlyTheShortenedCallsTheLimitShows()
    {
        // The long call comes second, after the limit.
        string text = await CallSites("Run", limit: 1);

        Assert.Equal(
            "2 calls to CallShapes.LongCalls.Target.Run(string, Action, bool):\n" +
            "CallShapes.LongCalls.Callers.Brief()\n" +
            "  Target.Run(\"brief\", () => { Console.WriteLine(\"brief\"); }, true)\n" +
            "... (1 more; raise limit to see all)",
            text);
    }

    [Fact]
    public async Task CallSites_LeavesOutAnAnonymousMethodsBody_AndKeepsItsParameters()
    {
        string text = await CallSites("Read");

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Read(string, Reader, bool):\n" +
            "CallShapes.LongCalls.Callers.LongDelegate()\n" +
            "  Target.Read(\"delegate\", delegate(ref int position, out string text) { ... }, false)\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_LeavesOutTheEntriesOfALongInitializer()
    {
        string text = await CallSites("Fill");

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Fill(Settings, int):\n" +
            "CallShapes.LongCalls.Callers.LongInitializer()\n" +
            "  Target.Fill(new Settings { ... }, 3)\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_LeavesOutALambdaInAnInitializerBeforeTheInitializer()
    {
        string text = await CallSites("Hold");

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Hold(Holder):\n" +
            "CallShapes.LongCalls.Callers.LambdaInInitializer()\n" +
            "  Target.Hold(new Holder { Label = \"held\", Work = () => ... })\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_CutsACallThatHasNothingToLeaveOut()
    {
        string text = await CallSites("Words");

        string call = "Target.Words(" + string.Join(", ", Words.Select(word => $"\"{word}\"")) + ")";
        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Words(string[]):\n" +
            "CallShapes.LongCalls.Callers.ManyWords()\n" +
            "  " + call[..297] + "...\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_CutsBeforeADot_SoThatTheMarkerStandsAlone()
    {
        // Cut at 297 characters, the line would end in the dot after "is" and the marker.
        string text = await CallSites("Say");

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Say(string):\n" +
            "CallShapes.LongCalls.Callers.Sentence()\n" +
            "  Target.Say(\"A call that passes one long text is cut where its line reaches the length. " +
            "The text of this call is written so that the cut lands right after a dot. " +
            "The line then ends in the three dots of the marker and in no fourth one. " +
            "What follows the dot is left out, as the rest of a cut line is...\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_KeepsACallOfExactlyTheLengthWhole_AndCutsOneOfACharacterMore()
    {
        string text = await CallSites("Note");

        const string Start = "Target.Note(\"This text makes the line of its call exactly as long as the length at " +
            "which a line of call_sites is shortened, so that the line stays whole, while the same text with one " +
            "more letter makes it one character longer, so that the line is cut and the answer counts it as shortened";
        string atTheLength = Start + " every time\")";
        string pastTheLength = Start + " at any time\")";
        Assert.Equal(300, atTheLength.Length);
        Assert.Equal(301, pastTheLength.Length);
        Assert.Equal(
            "2 calls to CallShapes.LongCalls.Target.Note(string):\n" +
            "CallShapes.LongCalls.Callers.AtTheLength()\n" +
            "  " + atTheLength + "\n" +
            "CallShapes.LongCalls.Callers.PastTheLength()\n" +
            "  " + pastTheLength[..297] + "...\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_LeavesOutTheBodyOfALambdaThatMakesAnAnonymousObject()
    {
        string text = await CallSites("Make");

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Make(Func<object>, bool):\n" +
            "CallShapes.LongCalls.Callers.AnonymousObject()\n" +
            "  Target.Make(() => ..., true)\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_LeavesOutTheBodyOfALambdaACollectionInitializerAdds()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.LongCalls.Jobs",
            ["member_name"] = "Add",
            ["kind"] = "call_sites",
        });

        Assert.Equal(
            "1 call to CallShapes.LongCalls.Jobs.Add(Action):\n" +
            "CallShapes.LongCalls.Callers.JobList()\n" +
            "  { () => ... }\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_CutsALineThatLeavingPartsOutCannotFit_AsItStands()
    {
        // Leaving the short lambda out would not make the line fit, so it keeps its body.
        string text = await CallSites("Log");

        string call = "Target.Log(() => { Console.WriteLine(\"short\"); }, \"A text so long that leaving the short " +
            "lambda before it out could not make the line fit, so the line is cut as it stands and keeps the lambda " +
            "whole, where leaving it out would lose its body for no more than a few characters of the text at the " +
            "end of the line, which this sentence only makes long enough\")";
        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Log(Action, string):\n" +
            "CallShapes.LongCalls.Callers.ShortLambdaLongText()\n" +
            "  " + call[..297] + "...\n" +
            "Shortened: 1 call longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_KeepsACallOfAtMostTheLengthWhole()
    {
        // The constructor's call holds the whole initializer, 288 characters.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.LongCalls.Settings",
            ["member_name"] = ".ctor",
            ["kind"] = "call_sites",
        });

        string call = "new Settings { Title = \"A title long enough to count toward the length of the line\", " +
            "Subtitle = \"A subtitle that does the same for the entries of the initializer\", Width = 640, " +
            "Height = 480, Visible = true, Footer = \"A footer that makes sure the line runs past three hundred characters\" }";
        Assert.Equal(288, call.Length);
        Assert.Equal(
            "1 call to CallShapes.LongCalls.Settings..ctor():\n" +
            "CallShapes.LongCalls.Callers.LongInitializer()\n" +
            "  " + call,
            text);
    }

    [Fact]
    public async Task CallSites_ByParameter_KeepsTheBodyOfALambdaAskedAbout_AndCutsIt()
    {
        string text = await CallSites("Run", parameterName: "action");

        string lambda = "() => { int num = 0; for (int i = 0; i < 10; i++) { num += i * i; } " +
            "Console.WriteLine(\"The sum of the squares of the numbers below ten is \" + num + \".\"); " +
            "Console.WriteLine(\"This line only makes the body of the lambda long enough to be shortened.\"); " +
            "Console.WriteLine(\"And this one makes sure of it, whatever the decompiler writes.\"); }";
        Assert.Equal(
            "2 calls to CallShapes.LongCalls.Target.Run(string, Action, bool), argument for action:\n" +
            "CallShapes.LongCalls.Callers.Brief()\n" +
            "  () => { Console.WriteLine(\"brief\"); }\n" +
            "CallShapes.LongCalls.Callers.LongLambda()\n" +
            "  " + lambda[..297] + "...\n" +
            "Shortened: 1 argument longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_ByParameter_KeepsTheBodyOfAnAnonymousMethodAskedAbout_AndCutsIt()
    {
        string text = await CallSites("Read", parameterName: "reader");

        string method = "delegate(ref int position, out string text) { " +
            "text = \"An anonymous method keeps its parameters when its body is left out.\"; " +
            "position += text.Length; " +
            "Console.WriteLine(\"Its body is long enough to be left out when the line is shortened.\"); " +
            "Console.WriteLine(\"And this line adds to it, whatever the decompiler writes.\"); " +
            "return position > 0; }";
        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Read(string, Reader, bool), argument for reader:\n" +
            "CallShapes.LongCalls.Callers.LongDelegate()\n" +
            "  " + method[..297] + "...\n" +
            "Shortened: 1 argument longer than 300 characters" + Shortened,
            text);
    }

    [Fact]
    public async Task CallSites_ByParameter_CutsTheValuesOfAParamsArgumentAsOneLine()
    {
        string text = await CallSites("Words", parameterName: "words");

        string values = string.Join(", ", Words.Select(word => $"\"{word}\""));
        Assert.Equal(
            "1 call to CallShapes.LongCalls.Target.Words(string[]), argument for words:\n" +
            "CallShapes.LongCalls.Callers.ManyWords()\n" +
            "  " + values[..297] + "...\n" +
            "Shortened: 1 argument longer than 300 characters" + Shortened,
            text);
    }

    private Task<string> CallSites(string member, string? parameterName = null, int? limit = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.LongCalls.Target",
            ["member_name"] = member,
            ["kind"] = "call_sites",
        };
        if (parameterName != null)
            arguments["parameter_name"] = parameterName;
        if (limit != null)
            arguments["limit"] = limit;
        return CallToolText("analyze", arguments);
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
