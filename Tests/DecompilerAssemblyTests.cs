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
    public async Task DecompileMethod_OnAPropertyGetter_ReturnsAccessorBody()
    {
        // ThrowOnAssemblyResolveErrors is a stable public property on
        // DecompilerSettings — our own AssemblyHost.cs uses it, so the test target
        // is build-guaranteed to expose the get_ accessor.
        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["methodName"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("ThrowOnAssemblyResolveErrors", text);
        // A header alone would satisfy the name check above — require a brace so
        // we know the decompiler actually emitted a property/method body.
        Assert.Contains("{", text);
    }

    [Fact]
    public async Task FindMethods_DoesNotEmitPropertyAccessors()
    {
        // Mirror of the decompile test above: find_methods deliberately hides
        // accessors from generic browsing, so a name-pattern search for a known
        // accessor should report zero matches even though decompile_method finds it.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namePattern"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.Contains("(no methods match)", text);
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

    [Fact]
    public async Task PathGuard_RejectsSymlinkThatEscapesAllowedRoot()
    {
        // PathGuard's lexical check accepts the symlink (it lives under the
        // allow-root); the reparse-point pass then resolves it and rejects the
        // escape. Creating a symbolic link on Windows requires admin or
        // Developer Mode — silently pass if the environment doesn't allow it,
        // so a stricter CI sandbox doesn't fail this suite.
        string allowRoot = Path.GetDirectoryName(_server.AssemblyPath)!;
        string outsideDir = Path.Combine(Path.GetTempPath(),
            "ilens-symlink-test-" + Guid.NewGuid().ToString("N"));
        string outsideFile = Path.Combine(outsideDir, "outside.dll");
        string symlinkInside = Path.Combine(allowRoot,
            "escape-link-" + Guid.NewGuid().ToString("N") + ".dll");

        Directory.CreateDirectory(outsideDir);
        File.Copy(_server.AssemblyPath, outsideFile);
        try
        {
            try
            {
                File.CreateSymbolicLink(symlinkInside, outsideFile);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            CallToolResult result = await _server.Client.CallToolAsync("list_types",
                new Dictionary<string, object?>
                {
                    ["assembly"] = symlinkInside,
                    ["namespaceName"] = "ICSharpCode.Decompiler",
                });

            string text = string.Join(
                "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            Assert.True(result.IsError,
                $"Expected symlink-escape rejection, got: {text}");
            Assert.Contains("symbolic link", text);
        }
        finally
        {
            try { if (File.Exists(symlinkInside)) File.Delete(symlinkInside); } catch { }
            try { if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DecompileMethod_WithParameterTypes_DisambiguatesSameArityOverloads()
    {
        // Find a real same-arity overload group on CSharpDecompiler via reflection,
        // so the test stays valid even if the upstream package adds or removes
        // overloads — instead of hardcoding a signature that drifts.
        Type type = Type.GetType(
            "ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler")
            ?? throw new InvalidOperationException(
                "CSharpDecompiler type not loadable from the referenced ICSharpCode.Decompiler package.");

        var overload = type.GetMethods()
            .GroupBy(m => (m.Name, ArgCount: m.GetParameters().Length))
            .Where(g => g.Count() >= 2)
            .SelectMany(g => g)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No same-arity method overload group found on CSharpDecompiler — " +
                "the test target's API changed; pick a different type.");

        string[] parameterTypeNames = overload.GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToArray();

        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = type.FullName!,
            ["methodName"] = overload.Name,
            ["parameterCount"] = overload.GetParameters().Length,
            ["parameterTypes"] = parameterTypeNames,
        });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(overload.Name, text);
        // A body brace proves the decompiler emitted source, not just a header.
        Assert.Contains("{", text);
    }

    [Fact]
    public async Task GetOrLoad_RejectsAssemblyLargerThanTotalMemoryBudget()
    {
        // The shared fixture's server uses the default 200 MB budget, which fits
        // the multi-MB test target comfortably. Spawn a transient second server
        // with --max-total-size 1 (1 MB) so the same assembly now exceeds the
        // whole budget on its own — exercising the precondition documented in
        // CLAUDE.md's "Sharp edges" section.
        string allowRoot = Path.GetDirectoryName(_server.AssemblyPath)!;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens-budget",
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", allowRoot, "--max-total-size", "1"],
        });
        await using var tightClient = await McpClient.CreateAsync(transport);

        CallToolResult result = await tightClient.CallToolAsync("list_types",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["namespaceName"] = "ICSharpCode.Decompiler",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected over-budget rejection, got: {text}");
        Assert.Contains("exceeds", text);
        Assert.Contains("budget", text);
    }

    [Fact]
    public async Task Analyze_AppliedToOnANonAttributeType_ReturnsError()
    {
        // AppliedTo asks "what is this attribute applied to?" — calling it on a
        // non-attribute type would silently yield an empty result that is
        // indistinguishable from a real attribute applied nowhere. AnalyzeTool
        // guards against this with an explicit base-type check.
        CallToolResult result = await _server.Client.CallToolAsync("analyze",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["kind"] = "AppliedTo",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected AppliedTo-on-non-attribute rejection, got: {text}");
        Assert.Contains("System.Attribute", text);
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
