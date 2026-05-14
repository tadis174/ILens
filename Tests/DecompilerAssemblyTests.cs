using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests: spawn the real ILens MCP server and exercise its tools
/// against ICSharpCode.Decompiler.dll. Assertions check the <em>shape</em> of
/// each response, not exact contents — the inspected assembly's API drifts
/// across versions, but the tool contract (call in, useful text out) does not.
/// </summary>
public sealed class DecompilerAssemblyTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public DecompilerAssemblyTests(ILensServerFixture server) => _server = server;

    [Fact]
    public async Task ListAllowedRoots_ReportsAConfiguredRoot()
    {
        string text = await CallToolText("list_allowed_roots", new());
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("No allowed roots", text);
    }

    [Fact]
    public async Task ListTypes_OnAPopulatedNamespace_ReturnsTypes()
    {
        string text = await CallToolText("list_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespaceName"] = "ICSharpCode.Decompiler.TypeSystem",
        });
        Assert.DoesNotContain("No types found", text);
        Assert.Contains("ICSharpCode.Decompiler.TypeSystem.", text);
    }

    [Fact]
    public async Task SearchTypes_BySubstring_FindsMatches()
    {
        string text = await CallToolText("search_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["pattern"] = "Decompiler",
        });
        Assert.DoesNotContain("No types matching", text);
        Assert.Contains("Decompiler", text);
    }

    [Fact]
    public async Task FindMethods_ByNamePattern_ReturnsMatches()
    {
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namePattern"] = "Decompile",
        });
        Assert.DoesNotContain("(no methods match)", text);
    }

    [Fact]
    public async Task DecompileType_OnAKnownType_YieldsPlausibleCSharp()
    {
        string text = await CallToolText("decompile_type", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        Assert.Contains("CSharpDecompiler", text);
        Assert.Contains("class", text);
    }

    [Fact]
    public async Task Analyze_UsedBy_OnAKnownType_DoesNotError()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
            ["kind"] = "UsedBy",
        });
        // Shape only — a non-empty report, no exception. The cross-references
        // themselves drift with the assembly.
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task ListTypes_OnAPathOutsideTheAllowedRoots_ReturnsError()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = @"C:\Windows\System32\kernel32.dll",
            ["namespaceName"] = "System",
        };
        CallToolResult result = await _server.Client.CallToolAsync("list_types", arguments);

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error for an out-of-root path, got: {text}");
        Assert.Contains("allowed root", text);
    }

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and
    /// returns the concatenated text content.
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
