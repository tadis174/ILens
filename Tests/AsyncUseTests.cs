using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what analyze lists when asked what an async method uses: what its
/// author wrote, without what the compiler added to make the method asynchronous, which
/// <c>include_compiler_generated</c> lists. The fixture's <c>CallShapes.AsyncUses.Clerk</c>
/// has an async method for each shape that changes what the compiler adds, each named after
/// its shape, and a method that is not async and waits for a task by hand.
/// </summary>
public sealed class AsyncUseTests : IClassFixture<ILensServerFixture>
{
    private const string AsyncUses = "CallShapes.AsyncUses";
    private const string Tasks = "System.Threading.Tasks";
    private const string Services = "System.Runtime.CompilerServices";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public AsyncUseTests(ILensServerFixture server)
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

    // The answer is what the method's own lines use: the task it awaits and the ledger. The
    // builder differs with what the method returns, and the lambda starts its class itself.
    [Theory]
    [InlineData("OneAwait()", new[] { $"{AsyncUses}.Ledger.Post(string)", $"{Tasks}.Task.Yield()" })]
    [InlineData("WithResult()", new[] { $"{AsyncUses}.Ledger.Entries", $"{AsyncUses}.Ledger.Post(string)", $"{Tasks}.Task.Yield()" })]
    [InlineData("WithoutTask()", new[] { $"{AsyncUses}.Ledger.Post(string)", $"{Tasks}.Task.Yield()" })]
    [InlineData("TwoAwaiters()", new[]
    {
        $"{AsyncUses}.Ledger.Post(string)", $"{Tasks}.Task.ConfigureAwait(bool)", $"{Tasks}.Task.Delay(int)", $"{Tasks}.Task.Yield()",
    })]
    [InlineData("InLambda()", new[]
    {
        $"{AsyncUses}.Ledger.Post(string)", "System.Func<Task>..ctor(object, nint)", $"{Tasks}.Task.Yield()",
    })]
    public async Task Uses_OnAnAsyncMethod_ListsWhatItsAuthorWrote(string method, string[] expected)
    {
        Assert.Equal(
            $"{expected.Length} symbols used by {AsyncUses}.Clerk.{method} [declared]:\n" + string.Join("\n", expected),
            await Uses(method));
    }

    [Fact]
    public async Task Uses_WithTheSwitch_ListsWhatTheCompilerAdded()
    {
        string[] lines = (await Uses("OneAwait()", includeGenerated: true)).Split('\n');

        Assert.Contains($"{AsyncUses}.Ledger.Post(string)", lines);
        Assert.Contains($"{Services}.AsyncTaskMethodBuilder.Create()", lines);
        Assert.Contains($"{Services}.AsyncTaskMethodBuilder.SetResult()", lines);
        Assert.Contains($"{Services}.YieldAwaitable.GetAwaiter()", lines);
        Assert.Contains($"{Services}.YieldAwaitable.YieldAwaiter", lines);
        Assert.Contains($"{Services}.YieldAwaitable.YieldAwaiter.GetResult()", lines);
        Assert.Contains($"{Services}.YieldAwaitable.YieldAwaiter.get_IsCompleted()", lines);
    }

    // The method's own code awaits a task it was handed and uses nothing. The answer says
    // that it left the compiler's part out, where "No symbols" alone would read as an empty
    // method.
    [Fact]
    public async Task Uses_OnAnAsyncMethodThatOnlyAwaits_SaysWhatItLeftOut()
    {
        string text = await Uses("AwaitOnly(Task)");

        Assert.StartsWith(
            $"No symbols used by {AsyncUses}.Clerk.AwaitOnly(Task) [declared]\nNot shown: ", text);
        Assert.EndsWith(" compiler-generated; include_compiler_generated lists them", text);
    }

    // No await, so no class of the compiler's: the awaiter calls are the author's own.
    [Fact]
    public async Task Uses_OnAMethodThatIsNotAsync_KeepsTheAwaiterCallsItsAuthorWrote()
    {
        Assert.Equal(
            $"4 symbols used by {AsyncUses}.Clerk.WaitsByHand() [declared]:\n" +
            $"{AsyncUses}.Ledger.Post(string)\n" +
            $"{Services}.TaskAwaiter.GetResult()\n" +
            $"{Tasks}.Task.Delay(int)\n" +
            $"{Tasks}.Task.GetAwaiter()",
            await Uses("WaitsByHand()"));
    }

    // The await and the written call both use Task.GetAwaiter() and TaskAwaiter.GetResult(),
    // and the compiled code shows the same calls for both. They are taken for the compiler's,
    // so the default answer leaves the written ones out with them.
    [Fact]
    public async Task Uses_OnAnAsyncMethodThatAlsoWaitsByHand_LeavesTheWrittenAwaiterCallsOutToo()
    {
        Assert.Equal(
            $"2 symbols used by {AsyncUses}.Clerk.AwaitsAndWaitsByHand() [declared]:\n" +
            $"{AsyncUses}.Ledger.Post(string)\n" +
            $"{Tasks}.Task.Delay(int)",
            await Uses("AwaitsAndWaitsByHand()"));

        string[] all = (await Uses("AwaitsAndWaitsByHand()", includeGenerated: true)).Split('\n');
        Assert.Contains($"{Tasks}.Task.GetAwaiter()", all);
        Assert.Contains($"{Services}.TaskAwaiter.GetResult()", all);
    }

    /// <summary>What <paramref name="method"/> of Clerk, written with its parameter list, uses.</summary>
    private async Task<string> Uses(string method, bool includeGenerated = false)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{AsyncUses}.Clerk",
            ["member_name"] = method[..method.IndexOf('(')],
            ["kind"] = "uses",
            ["include_compiler_generated"] = includeGenerated,
        };

        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
