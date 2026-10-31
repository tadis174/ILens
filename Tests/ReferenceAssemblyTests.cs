using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for questions that read code, asked of a reference assembly. Such an assembly
/// carries ReferenceAssemblyAttribute and holds a library's declarations without their code, so
/// analyze's kinds that read code, search_code and find_harmony_dependencies refuse it rather than
/// answer from its placeholders that nothing uses, calls or creates a member. The kinds that read
/// declarations alone answer as for any assembly. The build of Plates that
/// <see cref="CodelessAssembly"/> writes with the mark has bodies that hold code, so the mark alone
/// decides, and the build without it is the control.
/// </summary>
public sealed class ReferenceAssemblyTests : IClassFixture<ILensServerFixture>
{
    private const string Stack = "Plates.Stack";

    private const string Refusal =
        "This assembly is a reference assembly, which holds a library's declarations without their code, so ";

    private readonly ILensServerFixture _server;

    public ReferenceAssemblyTests(ILensServerFixture server) => _server = server;

    // A type of another assembly and a name the assembly does not have are refused alike, since
    // no name of a reference assembly can be answered from code.
    [Theory]
    [InlineData(Stack, "Count", "used_by")]
    [InlineData(Stack, "Count", "call_sites")]
    [InlineData(Stack, "Count", "uses")]
    [InlineData(Stack, null, "used_by")]
    [InlineData(Stack, null, "instantiated_by")]
    [InlineData(Stack, "Size", "read_by")]
    [InlineData(Stack, "Size", "assigned_by")]
    [InlineData("System.Object", null, "used_by")]
    [InlineData(Stack, "Missing", "used_by")]
    public async Task Analyze_AKindThatReadsCode_OnAReferenceAssembly_IsRefused(
        string typeName, string? memberName, string kind)
    {
        var path = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        try
        {
            Assert.Equal(
                Refusal + $"'{kind}' cannot be answered from it; ask the build the library runs as. exposed_by, " +
                "extension_methods, applied_to, overridden_by, implemented_by and implements read declarations " +
                "alone and answer here.",
                await CallToolError("analyze", Analyze(path, typeName, memberName, kind)));
        }
        finally
        {
            CodelessAssembly.TryDelete(path);
        }
    }

    [Theory]
    [InlineData(null, "exposed_by")]
    [InlineData(null, "extension_methods")]
    [InlineData("Count", "overridden_by")]
    [InlineData("Count", "implements")]
    public async Task Analyze_AKindThatReadsDeclarations_OnAReferenceAssembly_AnswersAsWithoutTheMark(
        string? memberName, string kind)
    {
        var marked = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        var code = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: false);
        try
        {
            Assert.Equal(await CallToolText("analyze", Analyze(code, Stack, memberName, kind)),
                await CallToolText("analyze", Analyze(marked, Stack, memberName, kind)));
        }
        finally
        {
            CodelessAssembly.TryDelete(marked, code);
        }
    }

    // The mark itself is a declaration, which applied_to finds on the assembly.
    [Fact]
    public async Task Analyze_AppliedTo_OnAReferenceAssembly_NamesTheAssemblyMarked()
    {
        var path = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        try
        {
            Assert.Equal(
                "1 symbol marked with System.Runtime.CompilerServices.ReferenceAssemblyAttribute:\nassembly Plates",
                await CallToolText("analyze",
                    Analyze(path, "System.Runtime.CompilerServices.ReferenceAssemblyAttribute", null, "applied_to")));
        }
        finally
        {
            CodelessAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task SearchCode_OnAReferenceAssembly_IsRefused()
    {
        var path = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        try
        {
            Assert.Equal(
                Refusal + "there is no code to search; ask the build the library runs as. find_methods and " +
                "list_members read declarations alone and answer here.",
                await CallToolError("search_code", new() { ["assembly"] = path, ["regex"] = "Size" }));
        }
        finally
        {
            CodelessAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnAReferenceAssembly_IsRefused()
    {
        var path = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        try
        {
            Assert.Equal(
                Refusal + "the patches it applies and the members it looks up cannot be read from it; ask the " +
                "build the mod runs as.",
                await CallToolError("find_harmony_dependencies", new() { ["assembly"] = path }));
        }
        finally
        {
            CodelessAssembly.TryDelete(path);
        }
    }

    // Without the mark, the same code answers: Stack's constructor sets Size.
    [Fact]
    public async Task Analyze_AndSearchCode_WithoutTheMark_ReadTheCode()
    {
        var path = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: false);
        try
        {
            Assert.Equal("1 writer of Plates.Stack.Size [declared]:\nPlates.Stack..ctor()",
                await CallToolText("analyze", Analyze(path, Stack, "Size", "assigned_by")));
            Assert.Contains("  Size = 1;",
                (await CallToolText("search_code", new() { ["assembly"] = path, ["regex"] = "Size = 1" })).Split('\n'));
        }
        finally
        {
            CodelessAssembly.TryDelete(path);
        }
    }

    private static Dictionary<string, object?> Analyze(string path, string typeName, string? memberName, string kind)
    {
        var arguments = new Dictionary<string, object?> { ["assembly"] = path, ["type_name"] = typeName, ["kind"] = kind };
        if (memberName is not null)
            arguments["member_name"] = memberName;
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

    /// <summary>
    /// Calls an ILens MCP tool, fails the test unless it returned an error, and returns the error's
    /// message without the prefix the error filter puts before it.
    /// </summary>
    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        const string prefix = "Error: ArgumentException: ";
        Assert.StartsWith(prefix, text);
        return text[prefix.Length..];
    }
}
